use crate::{Track, fs_safe};
use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
use lofty::{
    config::{ParseOptions, WriteOptions},
    file::{AudioFile, TaggedFile, TaggedFileExt},
    probe::Probe,
    tag::{Accessor, Tag, TagExt},
};
use rusty_chromaprint::{Configuration, FingerprintCompressor, Fingerprinter};
use std::{
    fs::File,
    io::{self, Seek, SeekFrom},
};
use symphonia::core::{
    audio::SampleBuffer, codecs::DecoderOptions, errors::Error as DecodeError,
    formats::FormatOptions, io::MediaSourceStream, meta::MetadataOptions, probe::Hint,
};

#[derive(Debug, Default, PartialEq, Eq)]
struct Tags {
    artist: String,
    title: String,
    genre: String,
    year: Option<u32>,
}

fn probe(file: &mut File, artwork: bool) -> Option<TaggedFile> {
    file.seek(SeekFrom::Start(0)).ok()?;
    Probe::new(file)
        .options(ParseOptions::new().read_cover_art(artwork))
        .guess_file_type()
        .ok()?
        .read()
        .ok()
}

fn read_tags(file: &TaggedFile) -> Tags {
    let mut result = Tags::default();
    for tag in file.primary_tag().into_iter().chain(file.tags()) {
        if result.artist.is_empty() {
            result.artist = tag
                .artist()
                .map(|s| s.trim().to_owned())
                .unwrap_or_default();
        }
        if result.title.is_empty() {
            result.title = tag.title().map(|s| s.trim().to_owned()).unwrap_or_default();
        }
        if result.genre.is_empty() {
            result.genre = tag.genre().map(|s| s.trim().to_owned()).unwrap_or_default();
        }
        if result.year.is_none() {
            result.year = tag.year().filter(|y| (1..=9999).contains(y));
        }
    }
    result
}
fn seconds(value: f64) -> Option<u32> {
    (value.is_finite() && value >= 0.5 && value < u32::MAX as f64).then(|| value.round() as u32)
}
pub(crate) fn inspect(mut file: File, track: &mut Track) {
    if let Some(parsed) = probe(&mut file, false) {
        let tags = read_tags(&parsed);
        track.artist = tags.artist;
        track.title = tags.title;
        track.genre = tags.genre;
        track.year = tags.year;
        if !(track.artist.is_empty()
            && track.title.is_empty()
            && track.genre.is_empty()
            && track.year.is_none())
        {
            track.metadata_source = Some("embeddedTags".into());
        }
        track.duration_seconds = seconds(parsed.properties().duration().as_secs_f64());
    }
    // Recognition is computed locally and uses at most 120 seconds of audio. A
    // decoder failure does not remove the SHA-256 index row or trigger a lookup.
    if file.seek(SeekFrom::Start(0)).is_ok() {
        if let Some((fingerprint, known_duration)) =
            compute(file, &track.format, track.duration_seconds)
        {
            track.fingerprint = Some(fingerprint);
            track.duration_seconds = known_duration;
        }
    }
}

fn compute(file: File, ext: &str, prior_duration: Option<u32>) -> Option<(String, Option<u32>)> {
    let mut hint = Hint::new();
    hint.with_extension(ext);
    let mss = MediaSourceStream::new(Box::new(file), Default::default());
    let mut format = symphonia::default::get_probe()
        .format(
            &hint,
            mss,
            &FormatOptions::default(),
            &MetadataOptions::default(),
        )
        .ok()?
        .format;
    let track = format.default_track()?;
    let params = track.codec_params.clone();
    let id = track.id;
    let mut decoder = symphonia::default::get_codecs()
        .make(&params, &DecoderOptions::default())
        .ok()?;
    let config = Configuration::preset_test2();
    let mut printer = Fingerprinter::new(&config);
    let mut spec = None;
    let mut total_frames: u64 = 0;
    let mut complete = false;
    loop {
        let packet = match format.next_packet() {
            Ok(p) => p,
            Err(DecodeError::IoError(e)) if e.kind() == io::ErrorKind::UnexpectedEof => {
                complete = true;
                break;
            }
            Err(_) => return None,
        };
        if packet.track_id() != id {
            continue;
        }
        let decoded = match decoder.decode(&packet) {
            Ok(a) => a,
            Err(DecodeError::DecodeError(_)) => continue,
            Err(_) => return None,
        };
        let sample_spec = *decoded.spec();
        let channels = sample_spec.channels.count();
        match spec {
            None => {
                if !(8000..=384000).contains(&sample_spec.rate) || !(1..=8).contains(&channels) {
                    return None;
                }
                printer.start(sample_spec.rate, channels as u32).ok()?;
                spec = Some(sample_spec);
            }
            Some(s) if s == sample_spec => (),
            _ => return None,
        }
        let max_frames = 120 * u64::from(sample_spec.rate);
        let copied_frames = decoded.frames() as u64;
        let mut samples = SampleBuffer::<i16>::new(decoded.capacity() as u64, sample_spec);
        samples.copy_interleaved_ref(decoded);
        let accepted = copied_frames.min(max_frames.saturating_sub(total_frames));
        printer.consume(&samples.samples()[..accepted as usize * channels]);
        total_frames += accepted;
        if total_frames >= max_frames {
            break;
        }
    }
    let spec = spec?;
    printer.finish();
    let raw = printer.fingerprint();
    if raw.is_empty() {
        return None;
    }
    let packed = FingerprintCompressor::from(&config).compress(raw);
    let length = prior_duration
        .or_else(|| {
            params.n_frames.and_then(|frames| match params.time_base {
                Some(base) => {
                    let t = base.calc_time(frames);
                    seconds(t.seconds as f64 + t.frac)
                }
                None => seconds(frames as f64 / f64::from(spec.rate)),
            })
        })
        .or_else(|| {
            complete
                .then(|| seconds(total_frames as f64 / f64::from(spec.rate)))
                .flatten()
        });
    Some((URL_SAFE_NO_PAD.encode(packed), length))
}

pub(crate) fn fill_staged(file: &mut File, proposed: &Track) -> io::Result<bool> {
    let current = probe(file, true)
        .ok_or_else(|| fs_safe::error("Could not verify existing tags on the staged copy."))?;
    let old = read_tags(&current);
    fn prefer(existing: &str, new: &str) -> String {
        if !existing.is_empty() {
            existing.into()
        } else {
            new.trim().chars().take(512).collect()
        }
    }
    let expected = Tags {
        artist: prefer(&old.artist, &proposed.artist),
        title: prefer(&old.title, &proposed.title),
        genre: prefer(&old.genre, &proposed.genre),
        year: old
            .year
            .or(proposed.year.filter(|y| (1..=9999).contains(y))),
    };
    if old == expected {
        return Ok(false);
    }
    let mut tag = current
        .primary_tag()
        .cloned()
        .unwrap_or_else(|| Tag::new(current.primary_tag_type()));
    if old.artist != expected.artist {
        tag.set_artist(expected.artist.clone());
    }
    if old.title != expected.title {
        tag.set_title(expected.title.clone());
    }
    if old.genre != expected.genre {
        tag.set_genre(expected.genre.clone());
    }
    if old.year != expected.year {
        if let Some(year) = expected.year {
            tag.set_year(year);
        }
    }
    file.seek(SeekFrom::Start(0))?;
    tag.save_to(file, WriteOptions::default())
        .map_err(|e| fs_safe::error(format!("Could not write tags on the staged copy: {e}")))?;
    let verified = probe(file, true)
        .ok_or_else(|| fs_safe::error("Could not reopen tags on the staged copy."))?;
    if read_tags(&verified) != expected {
        return Err(fs_safe::error(
            "The staged copy did not preserve existing tags and verify all filled fields.",
        ));
    }
    Ok(true)
}
