use crate::{Result, Track, path_key};
use std::{
    collections::{HashMap, HashSet},
    path::{Path, PathBuf},
};
use unicode_normalization::UnicodeNormalization;

#[derive(Debug)]
enum Piece {
    Literal(String),
    Artist,
    Title,
    Genre,
    Year,
    ArtistInitial,
    FileName,
    Extension,
}
pub(crate) struct Pattern(Vec<Vec<Piece>>);
impl Pattern {
    pub(crate) fn parse(template: &str) -> Result<Self> {
        let template = template.replace('\\', "/");
        let parts: Vec<_> = template.split('/').collect();
        if template.chars().count() > 10_000
            || parts.len() > 32
            || parts.iter().any(|s| {
                matches!(s.trim(), "" | "." | "..")
                    || s.contains(':')
                    || s.chars().any(char::is_control)
            })
        {
            return Err("Use a relative pattern with up to 32 nonempty path parts.".into());
        }
        let mut output = Vec::new();
        for mut part in parts {
            let mut pieces = Vec::new();
            while let Some(open) = part.find(['{', '}']) {
                if part.as_bytes()[open] == b'}' {
                    return Err("Pattern variables require matching braces.".into());
                }
                if open > 0 {
                    pieces.push(Piece::Literal(part[..open].into()));
                }
                let remaining = &part[open + 1..];
                let close = remaining
                    .find('}')
                    .ok_or("Pattern variables require matching braces.")?;
                let name = &remaining[..close];
                let token = match name.to_ascii_lowercase().as_str() {
                    "artist" => Piece::Artist,
                    "title" => Piece::Title,
                    "genre" => Piece::Genre,
                    "year" => Piece::Year,
                    "artistinitial" => Piece::ArtistInitial,
                    "filename" => Piece::FileName,
                    "extension" => Piece::Extension,
                    _ => return Err(format!("Unknown pattern variable: {{{name}}}")),
                };
                pieces.push(token);
                part = &remaining[close + 1..];
            }
            if !part.is_empty() {
                pieces.push(Piece::Literal(part.into()));
            }
            output.push(pieces);
        }
        Ok(Self(output))
    }
    fn render(&self, track: &Track, ext: &str) -> Vec<String> {
        let filename = Path::new(&track.source)
            .file_stem()
            .and_then(|x| x.to_str())
            .unwrap_or_default();
        let year = track
            .year
            .filter(|y| (1..=9999).contains(y))
            .map(|y| y.to_string())
            .unwrap_or_default();
        let initial = track
            .artist
            .trim()
            .chars()
            .next()
            .map(|c| c.to_uppercase().collect::<String>())
            .unwrap_or_default();
        self.0
            .iter()
            .map(|part| {
                let mut name = String::new();
                for piece in part {
                    let item = match piece {
                        Piece::Literal(l) => {
                            name.push_str(l);
                            continue;
                        }
                        Piece::Artist => &track.artist,
                        Piece::Title => &track.title,
                        Piece::Genre => &track.genre,
                        Piece::Year => &year,
                        Piece::FileName => filename,
                        Piece::Extension => ext,
                        Piece::ArtistInitial => &initial,
                    }
                    .trim();
                    name.push_str(if item.is_empty() { "Unknown" } else { item });
                }
                sanitize(&name)
            })
            .collect()
    }
    pub(crate) fn relative(&self, track: &Track, ext: &str) -> PathBuf {
        let mut names = self.render(track, ext);
        let file = names.pop().unwrap_or_else(|| "Unknown".into());
        let mut path = names.into_iter().collect::<PathBuf>();
        path.push(add_suffix(&file, "", ext));
        path
    }
}

fn sanitize(raw: &str) -> String {
    let normalized = raw
        .nfkc()
        .map(|c| {
            if c.is_control() || "<>:\"/\\|?*".contains(c) {
                '_'
            } else {
                c
            }
        })
        .collect::<String>();
    let name = bounded(normalized.trim().trim_end_matches(['.', ' ']), 200, 230);
    let name = name.trim_end_matches(['.', ' ']);
    if name.is_empty() {
        return "Unknown".into();
    }
    let before_dot = name
        .split('.')
        .next()
        .unwrap_or_default()
        .trim_end()
        .to_ascii_uppercase();
    let port = before_dot
        .strip_prefix("COM")
        .or_else(|| before_dot.strip_prefix("LPT"))
        .is_some_and(|s| s.len() == 1 && matches!(s.as_bytes()[0], b'1'..=b'9'));
    if port
        || matches!(
            before_dot.as_str(),
            "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$"
        )
    {
        format!("_{}", bounded(name, 199, 229))
    } else {
        name.into()
    }
}
fn bounded(value: &str, max_units: usize, max_bytes: usize) -> &str {
    let mut units = 0;
    let mut end = 0;
    for (i, c) in value.char_indices() {
        let next = i + c.len_utf8();
        units += c.len_utf16();
        if units > max_units || next > max_bytes {
            break;
        }
        end = next;
    }
    &value[..end]
}
fn add_suffix(name: &str, suffix: &str, ext: &str) -> String {
    let extension = if ext.is_empty() {
        String::new()
    } else {
        format!(".{ext}")
    };
    let stem = if !extension.is_empty() && name.to_ascii_lowercase().ends_with(&extension) {
        &name[..name.len() - extension.len()]
    } else {
        name
    };
    let stem = sanitize(stem);
    let added = suffix.len() + extension.len();
    let stem = bounded(&stem, 200 - added, 230 - added).trim_end_matches(['.', ' ']);
    format!("{stem}{suffix}{extension}")
}

pub(crate) struct Planner<'a> {
    root: &'a Path,
    template: Pattern,
    dirs: HashMap<String, PathBuf>,
    reserved: HashSet<String>,
}
impl<'a> Planner<'a> {
    pub(crate) fn new(root: &'a Path, template: Pattern) -> Self {
        Self {
            root,
            template,
            dirs: HashMap::new(),
            reserved: HashSet::new(),
        }
    }
    pub(crate) fn target(&mut self, track: &Track, ext: &str, variant: bool) -> Result<PathBuf> {
        let mut names = self.template.render(track, ext);
        let file = names.pop().ok_or("Empty pattern.")?;
        let mut parent = self.root.to_owned();
        for name in names {
            let base = parent.join(&name);
            let key = path_key(&base);
            if let Some(choice) = self.dirs.get(&key) {
                parent = choice.clone();
                continue;
            }
            let mut chosen = base;
            for bump in 2.. {
                if self.reserved.insert(path_key(&chosen)) {
                    break;
                }
                chosen = parent.join(add_suffix(&name, &format!("~{bump}"), ""));
            }
            self.dirs.insert(key, chosen.clone());
            parent = chosen;
        }
        let stable = format!("~{}", track.sha256[..12].to_ascii_lowercase());
        let mut file_path = parent.join(add_suffix(&file, if variant { &stable } else { "" }, ext));
        let mut suffix = stable.clone();
        for bump in 2.. {
            if self.reserved.insert(path_key(&file_path)) {
                break;
            }
            file_path = parent.join(add_suffix(&file, &suffix, ext));
            suffix = format!("{stable}-{bump}");
        }
        Ok(file_path)
    }
}
