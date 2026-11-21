use lofty::{
    config::WriteOptions,
    tag::{Accessor, Tag, TagExt, TagType},
};
use music_sorter_engine::{Engine, Profile, Track, preview_pattern};
use std::{
    fs,
    path::{Path, PathBuf},
    sync::Mutex,
};

struct Test {
    root: PathBuf,
    src: PathBuf,
    output: PathBuf,
    engine: Engine,
}
impl Test {
    fn new() -> Self {
        let root =
            std::env::temp_dir().join(format!("music-sorter-engine-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&root).unwrap();
        let root = root.canonicalize().unwrap();
        let src = root.join("src");
        fs::create_dir(&src).unwrap();
        let output = root.join("dest");
        let engine = Engine::new(root.join("state"));
        Self {
            root,
            src,
            output,
            engine,
        }
    }
    fn profile(&self) -> Profile {
        Profile {
            name: " test profiles ".into(),
            sources: vec![text(&self.src)],
            destination: text(&self.output),
            pattern: "{Genre}/{Artist}/{Title}".into(),
            ..Profile::default()
        }
    }
}
impl Drop for Test {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.root);
    }
}
fn text(s: &Path) -> String {
    s.to_string_lossy().into_owned()
}
fn make_tagged_wav(path: &Path, duration: u32) {
    fs::write(path, make_wav(duration, 1.)).unwrap();
    let mut tag = Tag::new(TagType::RiffInfo);
    tag.set_title("Generated melody".into());
    tag.set_artist("Test performer".into());
    tag.save_to_path(path, WriteOptions::default()).unwrap();
}
fn make_wav(duration: u32, detune: f64) -> Vec<u8> {
    let rate = 11025u32;
    let size = rate * duration * 2;
    let mut data = Vec::new();
    let u16le = |v: u16| v.to_le_bytes();
    let u32le = |v: u32| v.to_le_bytes();
    data.extend(b"RIFF");
    data.extend(u32le(size + 36));
    data.extend(b"WAVEfmt ");
    data.extend(u32le(16));
    data.extend(u16le(1));
    data.extend(u16le(1));
    data.extend(u32le(rate));
    data.extend(u32le(rate * 2));
    data.extend(u16le(2));
    data.extend(u16le(16));
    data.extend(b"data");
    data.extend(u32le(size));
    for i in 0..rate * duration {
        let s = i as f64 / rate as f64;
        let step = (i / (rate / 2)) % 5;
        let f = 220.0 * (1.12f64).powi(step as i32) + detune;
        data.extend(
            ((10_000.0 * (std::f64::consts::TAU * f * s).sin()
                + 2500.0 * (std::f64::consts::TAU * 2.3 * f * s).sin()) as i16)
                .to_le_bytes(),
        );
    }
    data
}
#[test]
fn known_music_metadata_and_self_contained_fingerprint() {
    let t = Test::new();
    let p = t.engine.save_profile(t.profile()).unwrap();
    make_tagged_wav(&t.src.join("generated.wav"), 11);
    let events = Mutex::new(Vec::new());
    let tracks = t
        .engine
        .scan_live(&p, |progress, row| {
            events
                .lock()
                .unwrap()
                .push((progress.completed, progress.total, row.is_some()))
        })
        .unwrap();
    assert_eq!(tracks.len(), 1);
    let track = &tracks[0];
    assert_eq!(track.title, "Generated melody");
    assert_eq!(track.artist, "Test performer");
    assert_eq!(track.metadata_source.as_deref(), Some("embeddedTags"));
    assert!(track.duration_seconds.is_some_and(|d| d > 10));
    let fp = track.fingerprint.as_ref().unwrap();
    assert!(
        fp.len() > 20
            && fp
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
    );
    assert_eq!(
        events.into_inner().unwrap(),
        vec![(0, 1, false), (1, 1, true)]
    );
    assert_eq!(t.engine.load_index(&p).unwrap(), tracks);
    let second = t.engine.scan(&p, |_| {}).unwrap();
    assert_eq!(second, tracks);
    assert_eq!(t.engine.profiles().unwrap()[0].name, "test profiles");
}
#[test]
fn exact_sha_dedup_retains_byte_distinct_variants_and_repeated_export() {
    let t = Test::new();
    let mut p = t.engine.save_profile(t.profile()).unwrap();
    fs::create_dir(t.src.join("nested")).unwrap();
    let one = make_wav(5, 0.);
    let two = make_wav(5, 6.);
    fs::write(t.src.join("01.wav"), &one).unwrap();
    fs::write(t.src.join("02.wav"), &two).unwrap();
    fs::write(t.src.join("nested/repeat.wav"), &one).unwrap();
    let mut tracks = t.engine.scan(&p, |_| {}).unwrap();
    assert_eq!(tracks.len(), 3);
    assert!(tracks.iter().all(|a| a.fingerprint.is_some()));
    for track in &mut tracks {
        track.artist = "Artist".into();
        track.title = "One".into();
        track.genre = "Pop".into();
    }
    let review = t.engine.review(&p, &tracks).unwrap();
    assert_eq!(
        review
            .rows
            .iter()
            .map(|r| r.status.as_str())
            .collect::<Vec<_>>(),
        ["variant", "variant", "duplicate"]
    );
    let first = t.engine.export(&p, &tracks).unwrap();
    assert_eq!(
        first
            .rows
            .iter()
            .map(|r| r.outcome.as_str())
            .collect::<Vec<_>>(),
        ["copied", "copied", "skippedDuplicate"]
    );
    assert_eq!(
        fs::read(first.rows[0].destination.as_ref().unwrap()).unwrap(),
        one
    );
    assert_eq!(
        fs::read(first.rows[1].destination.as_ref().unwrap()).unwrap(),
        two
    );
    p.pattern = "{Year}/{Title}".into();
    let again = t.engine.export(&p, &tracks).unwrap();
    assert_eq!(again.rows[0].destination, first.rows[0].destination);
    assert_eq!(again.rows[0].outcome, "alreadyPresent");
    fs::write(
        again.rows[0].destination.as_ref().unwrap(),
        b"user-modified",
    )
    .unwrap();
    let third = t.engine.export(&p, &tracks).unwrap();
    assert_eq!(third.rows[0].outcome, "conflict");
    assert_eq!(
        fs::read(again.rows[0].destination.as_ref().unwrap()).unwrap(),
        b"user-modified"
    );
    assert_eq!(fs::read(t.src.join("01.wav")).unwrap(), one);
}
#[test]
fn invalid_paths_and_collisions_never_change_originals() {
    let t = Test::new();
    let mut p = t.profile();
    let data = make_wav(5, 0.);
    let src = t.src.join("one.wav");
    fs::write(&src, &data).unwrap();
    let mut row = t.engine.scan(&p, |_| {}).unwrap();
    assert_eq!(row.len(), 1);
    let original_pattern = p.pattern.clone();
    for unsafe_pattern in [
        "../{Title}",
        "/Music/{Title}",
        "C:\\Music\\{Title}",
        "{Name}",
        "{Title}}",
    ] {
        p.pattern = unsafe_pattern.into();
        assert!(t.engine.review(&p, &row).is_err());
    }
    p.pattern = original_pattern;
    row[0].title = "../escaped".into();
    row[0].artist = "NUL".into();
    row[0].genre = "N/F".into();
    let review = t.engine.review(&p, &row).unwrap();
    let dst = PathBuf::from(review.rows[0].destination.as_ref().unwrap());
    assert!(dst.starts_with(&t.output) && dst.ends_with("N_F/_NUL/.._escaped.wav"));
    fs::create_dir_all(dst.parent().unwrap()).unwrap();
    fs::write(&dst, b"existing").unwrap();
    assert_eq!(
        t.engine.export(&p, &row).unwrap().rows[0].outcome,
        "conflict"
    );
    assert_eq!(fs::read(&dst).unwrap(), b"existing");
    let src2 = t.src.join("two.wav");
    fs::rename(&src, &src2).unwrap();
    assert_eq!(
        t.engine.export(&p, &row).unwrap().rows[0].outcome,
        "conflict"
    );
    assert_eq!(fs::read(src2).unwrap(), data);
    let path = preview_pattern(
        "{ArtistInitial}/{FileName}.{Extension}",
        &Track {
            source: "/my/abc.wav".into(),
            artist: "Élan".into(),
            ..Track::default()
        },
    )
    .unwrap();
    assert_eq!(Path::new(&path), Path::new("É").join("abc.wav"));
}
#[test]
fn source_mutation_prevents_copy_and_same_sources_do_not_share_lookup_index() {
    let t = Test::new();
    let mut p = t.engine.save_profile(t.profile()).unwrap();
    let src = t.src.join("source.wav");
    fs::write(&src, make_wav(5, 0.)).unwrap();
    let mut tracks = t.engine.scan(&p, |_| {}).unwrap();
    p.lookup_enabled = true;
    assert!(t.engine.load_index(&p).unwrap().is_empty());
    tracks[0].metadata_source = Some("acoustid".into());
    tracks[0].artist = "Enriched".into();
    t.engine.persist_index(&p, &tracks).unwrap();
    let another = t.engine.save_profile(t.profile()).unwrap();
    assert!(t.engine.load_index(&another).unwrap().is_empty());
    let cached = t.engine.scan(&p, |_| {}).unwrap();
    assert_eq!(cached[0].artist, "Enriched");
    fs::write(&src, make_wav(5, 5.)).unwrap();
    let result = t.engine.export(&p, &cached).unwrap();
    assert_eq!(result.rows[0].outcome, "failed");
    assert!(!Path::new(result.rows[0].destination.as_ref().unwrap()).exists());
    assert!(t.engine.scan(&p, |_| {}).unwrap()[0].artist.is_empty());
}
#[test]
fn only_missing_tags_are_filled_on_verified_staged_copy_and_rerun_reuses_it() {
    let t = Test::new();
    let mut p = t.profile();
    p.lookup_enabled = true;
    let p = t.engine.save_profile(p).unwrap();
    let src = t.src.join("tagged.wav");
    make_tagged_wav(&src, 5);
    let mut scanned = t.engine.scan(&p, |_| {}).unwrap();
    assert_eq!(scanned[0].title, "Generated melody");
    assert_eq!(scanned[0].year, None);
    assert!(scanned[0].genre.is_empty());
    scanned[0].title = "Wrong new title must not overwrite".into();
    scanned[0].year = Some(1975);
    scanned[0].genre = "New test genre".into();
    scanned[0].metadata_source = Some("acoustid".into());
    let original_bytes = fs::read(&scanned[0].source).unwrap();
    let first = t.engine.export(&p, &scanned).unwrap();
    assert_eq!(first.rows[0].outcome, "copied", "{:?}", first.rows);
    assert!(
        first.rows[0]
            .message
            .as_ref()
            .is_some_and(|m| m.contains("verified"))
    );
    assert_eq!(fs::read(&scanned[0].source).unwrap(), original_bytes);
    assert_ne!(
        fs::read(first.rows[0].destination.as_ref().unwrap()).unwrap(),
        original_bytes
    );
    let again = t.engine.export(&p, &scanned).unwrap();
    assert_eq!(again.rows[0].outcome, "alreadyPresent");
    assert_eq!(again.rows[0].destination, first.rows[0].destination);
    let copy_profile = Profile {
        sources: vec![text(&t.output)],
        destination: text(&t.root.join("other")),
        ..t.profile()
    };
    let copy_index = t.engine.scan(&copy_profile, |_| {}).unwrap();
    let exported = &copy_index[0];
    let input = cached_source_tags(&t.engine, &p, &scanned[0].source);
    assert_eq!(exported.fingerprint, input.fingerprint);
    if !input.title.is_empty() {
        assert_eq!(exported.title, input.title);
    }
    if input.year.is_none() {
        assert_eq!(exported.year, scanned[0].year);
    }
}
fn cached_source_tags(engine: &Engine, profile: &Profile, source: &str) -> Track {
    engine
        .scan(profile, |_| {})
        .unwrap()
        .into_iter()
        .find(|t| t.source == source)
        .unwrap()
}
#[test]
fn changed_file_identity_invalidates_even_with_matched_timestamp_and_length() {
    let t = Test::new();
    let p = t.profile();
    let path = t.src.join("same.wav");
    fs::write(&path, make_wav(5, 0.)).unwrap();
    let first = t.engine.scan(&p, |_| {}).unwrap();
    let modified = fs::metadata(&path).unwrap().modified().unwrap();
    let path_replacement = t.src.join("replacement.partial");
    fs::write(&path_replacement, make_wav(5, 1.)).unwrap();
    fs::File::options()
        .write(true)
        .open(&path_replacement)
        .unwrap()
        .set_modified(modified)
        .unwrap();
    fs::rename(&path_replacement, &path).unwrap();
    let second = t.engine.scan(&p, |_| {}).unwrap();
    assert_ne!(first[0].sha256, second[0].sha256);
}
#[cfg(unix)]
#[test]
fn symlinked_sources_and_destinations_are_never_followed() {
    use std::os::unix::fs::symlink;
    let t = Test::new();
    let mut p = t.profile();
    p.pattern = "folder/{FileName}".into();
    let outside = t.root.join("outside");
    fs::create_dir(&outside).unwrap();
    let external = outside.join("external.wav");
    fs::write(&external, make_wav(5, 0.)).unwrap();
    let safe = t.src.join("safe.wav");
    fs::write(&safe, make_wav(5, 1.)).unwrap();
    symlink(&external, t.src.join("linked.wav")).unwrap();
    symlink(&outside, t.src.join("linked-tree")).unwrap();
    let tracks = t.engine.scan(&p, |_| {}).unwrap();
    assert_eq!(tracks.len(), 1);
    assert!(tracks[0].source.ends_with("safe.wav"));
    fs::create_dir(&t.output).unwrap();
    symlink(&outside, t.output.join("folder")).unwrap();
    let report = t.engine.export(&p, &tracks).unwrap();
    assert_eq!(report.rows[0].outcome, "conflict");
    assert!(!outside.join("safe.wav").exists());
    assert!(external.exists());
}
