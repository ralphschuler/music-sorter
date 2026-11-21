mod audio;
mod fs_safe;
mod pattern;

use serde::{Deserialize, Serialize, de::DeserializeOwned};
use sha2::{Digest, Sha256};
use std::{
    collections::{HashMap, HashSet},
    fs::{self, File},
    io::{self, Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    sync::{
        Mutex,
        atomic::{AtomicUsize, Ordering},
    },
};
use unicode_normalization::UnicodeNormalization;

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Column {
    pub id: String,
    #[serde(default)]
    pub width: Option<u32>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Profile {
    #[serde(default)]
    pub id: Option<String>,
    pub name: String,
    pub sources: Vec<String>,
    pub destination: String,
    pub pattern: String,
    #[serde(default)]
    pub columns: Option<Vec<Column>>,
    #[serde(default)]
    pub lookup_enabled: bool,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Track {
    pub source: String,
    pub title: String,
    pub artist: String,
    pub genre: String,
    pub year: Option<u32>,
    pub format: String,
    pub sha256: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fingerprint: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub duration_seconds: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub metadata_source: Option<String>,
}

#[derive(Clone, Copy, Debug, Serialize)]
pub struct Progress {
    pub completed: usize,
    pub total: usize,
}
#[derive(Clone, Debug, Serialize)]
pub struct Review {
    pub rows: Vec<ReviewRow>,
}
#[derive(Clone, Debug, Serialize)]
pub struct ReviewRow {
    pub track: Track,
    pub destination: Option<String>,
    pub status: String,
    pub message: Option<String>,
}
#[derive(Clone, Debug, Serialize)]
pub struct Export {
    pub rows: Vec<ExportRow>,
}
#[derive(Clone, Debug, Serialize)]
pub struct ExportRow {
    pub source: String,
    pub destination: Option<String>,
    pub outcome: String,
    pub message: Option<String>,
}

#[derive(Serialize, Deserialize)]
struct Index {
    version: u32,
    sources: Vec<String>,
    tracks: Vec<IndexRow>,
}
#[derive(Serialize, Deserialize)]
struct IndexRow {
    track: Track,
    stamp: Option<FileStamp>,
}
#[derive(Serialize, Deserialize, Debug, PartialEq, Eq)]
struct FileStamp {
    volume: u64,
    identity: u64,
    bytes: u64,
    modified_s: i64,
    modified_ns: i64,
    changed_s: i64,
    changed_ns: i64,
}
impl FileStamp {
    #[cfg(unix)]
    fn read(f: &File) -> Option<Self> {
        use std::os::unix::fs::MetadataExt;
        let m = f.metadata().ok()?;
        Some(Self {
            volume: m.dev(),
            identity: m.ino(),
            bytes: m.size(),
            modified_s: m.mtime(),
            modified_ns: m.mtime_nsec(),
            changed_s: m.ctime(),
            changed_ns: m.ctime_nsec(),
        })
    }
    #[cfg(windows)]
    fn read(f: &File) -> Option<Self> {
        use std::os::windows::io::AsRawHandle;
        #[repr(C)]
        struct Time {
            low: u32,
            high: u32,
        }
        #[repr(C)]
        struct Info {
            attributes: u32,
            created: Time,
            accessed: Time,
            written: Time,
            volume: u32,
            size_high: u32,
            size_low: u32,
            links: u32,
            index_high: u32,
            index_low: u32,
        }
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn GetFileInformationByHandle(handle: *mut std::ffi::c_void, info: *mut Info) -> i32;
        }
        let mut raw = std::mem::MaybeUninit::<Info>::uninit();
        if unsafe { GetFileInformationByHandle(f.as_raw_handle(), raw.as_mut_ptr()) } == 0 {
            return None;
        }
        let i = unsafe { raw.assume_init() };
        let words = |high: u32, low: u32| u64::from(high) << 32 | u64::from(low);
        let index = words(i.index_high, i.index_low);
        if index == 0 {
            return None;
        }
        let modified = words(i.written.high, i.written.low);
        let created = words(i.created.high, i.created.low);
        Some(Self {
            volume: u64::from(i.volume),
            identity: index,
            bytes: words(i.size_high, i.size_low),
            modified_s: (modified / 10_000_000) as i64,
            modified_ns: ((modified % 10_000_000) * 100) as i64,
            changed_s: (created / 10_000_000) as i64,
            changed_ns: ((created % 10_000_000) * 100) as i64,
        })
    }
    #[cfg(not(any(unix, windows)))]
    fn read(_: &File) -> Option<Self> {
        None
    }
}
#[derive(Serialize, Deserialize)]
struct ReceiptLedger {
    version: u32,
    destination: String,
    entries: HashMap<String, Receipt>,
}
#[derive(Serialize, Deserialize)]
struct Receipt {
    relative_path: String,
    final_sha256: String,
}

#[derive(Clone, Debug)]
pub struct Engine {
    root: PathBuf,
}
type Result<T> = std::result::Result<T, String>;
fn explain(e: io::Error) -> String {
    e.to_string()
}
fn text(path: &Path) -> Result<String> {
    path.to_str()
        .map(str::to_owned)
        .ok_or_else(|| "A path is not valid Unicode.".to_owned())
}
fn digest(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
fn digest_syntax(s: &str) -> bool {
    s.len() == 64 && s.bytes().all(|b| b.is_ascii_hexdigit())
}
fn fold(s: &str) -> String {
    s.nfkc().flat_map(char::to_lowercase).collect()
}
fn path_key(p: &Path) -> String {
    fold(&p.to_string_lossy())
}
fn overlap(a: &Path, b: &Path) -> bool {
    let a = a
        .components()
        .map(|x| fold(&x.as_os_str().to_string_lossy()))
        .collect::<Vec<_>>();
    let b = b
        .components()
        .map(|x| fold(&x.as_os_str().to_string_lossy()))
        .collect::<Vec<_>>();
    a.starts_with(&b) || b.starts_with(&a)
}
fn read_json<T: DeserializeOwned>(p: &Path) -> Result<Option<T>> {
    match fs_safe::open(p) {
        Ok(f) => serde_json::from_reader(io::BufReader::new(f))
            .map(Some)
            .map_err(|e| e.to_string()),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(explain(e)),
    }
}
fn write_json<T: Serialize>(p: &Path, v: &T) -> Result<()> {
    let bytes = serde_json::to_vec(v).map_err(|e| e.to_string())?;
    fs_safe::replace_state(p, &bytes).map_err(explain)
}
fn source_paths(profile: &Profile) -> Result<Vec<PathBuf>> {
    let mut paths = profile
        .sources
        .iter()
        .map(|s| fs_safe::absolute(s).map_err(explain))
        .collect::<Result<Vec<_>>>()?;
    if paths.is_empty() {
        return Err("Choose at least one source folder.".into());
    }
    paths.sort_by_key(|p| path_key(p));
    paths.dedup();
    Ok(paths)
}
fn id(value: &str) -> Result<String> {
    uuid::Uuid::parse_str(value)
        .map(|v| v.to_string())
        .map_err(|_| "Invalid profile ID.".to_owned())
}
fn track_hash(file: &mut File, mut out: Option<&mut File>) -> io::Result<String> {
    let mut hash = Sha256::new();
    let mut buffer = vec![0; 128 * 1024];
    loop {
        let n = file.read(&mut buffer)?;
        if n == 0 {
            break;
        }
        hash.update(&buffer[..n]);
        if let Some(f) = out.as_deref_mut() {
            f.write_all(&buffer[..n])?;
        }
    }
    Ok(format!("{:x}", hash.finalize()))
}
fn hash_at(path: &Path) -> io::Result<String> {
    track_hash(&mut fs_safe::open(path)?, None)
}
fn supported(path: &Path) -> Option<String> {
    let ext = path.extension()?.to_str()?.to_ascii_lowercase();
    matches!(
        ext.as_str(),
        "mp3"
            | "flac"
            | "m4a"
            | "m4b"
            | "aac"
            | "wav"
            | "aiff"
            | "aif"
            | "ogg"
            | "oga"
            | "opus"
            | "wma"
            | "ape"
            | "wv"
            | "mpc"
            | "alac"
    )
    .then_some(ext)
}

impl Engine {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        Self { root: root.into() }
    }
    fn state(&self) -> Result<PathBuf> {
        fs_safe::absolute(&text(&self.root)?).map_err(explain)
    }
    fn folders(&self, profile: &Profile) -> Result<(Vec<PathBuf>, PathBuf)> {
        let sources = source_paths(profile)?;
        let dest = fs_safe::absolute(&profile.destination).map_err(explain)?;
        let state = self.state()?;
        if sources.iter().any(|p| overlap(p, &dest)) {
            return Err("Source and destination folders must not overlap.".into());
        }
        if sources
            .iter()
            .chain(std::iter::once(&dest))
            .any(|p| overlap(p, &state))
        {
            return Err("Application state must not overlap source or destination folders.".into());
        }
        Ok((sources, dest))
    }
    pub fn profiles(&self) -> Result<Vec<Profile>> {
        let folder = self.state()?.join("profiles");
        match fs_safe::directory(&folder, false) {
            Ok(()) => (),
            Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(vec![]),
            Err(e) => return Err(explain(e)),
        }
        let mut entries = Vec::new();
        for file in fs::read_dir(folder).map_err(explain)?.flatten() {
            let p = file.path();
            if p.extension().and_then(|e| e.to_str()) != Some("json") {
                continue;
            }
            let stem = p.file_stem().and_then(|s| s.to_str()).unwrap_or_default();
            if id(stem).as_deref() != Ok(stem) {
                continue;
            }
            if let Ok(Some(profile)) = read_json::<Profile>(&p) {
                if profile.id.as_deref() == Some(stem) {
                    entries.push(profile);
                }
            }
        }
        entries.sort_by(|a, b| fold(&a.name).cmp(&fold(&b.name)).then(a.id.cmp(&b.id)));
        Ok(entries)
    }
    pub fn save_profile(&self, mut profile: Profile) -> Result<Profile> {
        profile.name = profile.name.trim().into();
        if profile.name.is_empty() {
            return Err("Give this profile a name.".into());
        }
        let (sources, dest) = self.folders(&profile)?;
        pattern::Pattern::parse(&profile.pattern)?;
        let key = profile
            .id
            .as_deref()
            .filter(|s| !s.is_empty())
            .map(id)
            .transpose()?
            .unwrap_or_else(|| uuid::Uuid::new_v4().to_string());
        profile.id = Some(key.clone());
        profile.sources = sources.iter().map(|p| text(p)).collect::<Result<_>>()?;
        profile.destination = text(&dest)?;
        if let Some(cols) = profile.columns.as_mut() {
            let mut seen = HashSet::new();
            cols.retain(|c| {
                [
                    "title", "artist", "genre", "year", "filename", "format", "source", "sha256",
                ]
                .contains(&c.id.as_str())
                    && seen.insert(c.id.clone())
            });
            for column in cols.iter_mut() {
                column.width = column.width.map(|w| w.clamp(48, 2000));
            }
            if cols.is_empty() {
                profile.columns = None;
            }
        }
        let state = self.state()?.join("profiles");
        let _lock = fs_safe::state_lock(&state.join(format!("{key}.lock"))).map_err(explain)?;
        write_json(&state.join(format!("{key}.json")), &profile)?;
        Ok(profile)
    }
    fn index_location(&self, profile: &Profile) -> Result<(Vec<String>, PathBuf)> {
        let (sources, _) = self.folders(profile)?;
        let roots = sources
            .iter()
            .map(|s| text(s))
            .collect::<Result<Vec<_>>>()?;
        let profile_id = profile
            .id
            .as_deref()
            .filter(|s| !s.is_empty())
            .map(id)
            .transpose()?;
        let encoded = serde_json::to_vec(&(profile_id, &roots, profile.lookup_enabled))
            .map_err(|e| e.to_string())?;
        Ok((
            roots,
            self.state()?
                .join("index")
                .join(format!("{}.json", digest(&encoded))),
        ))
    }
    fn index_rows(&self, profile: &Profile) -> Result<Vec<IndexRow>> {
        let (sources, path) = self.index_location(profile)?;
        match read_json::<Index>(&path) {
            Ok(index) => Ok(index
                .filter(|v| v.version == 2 && v.sources == sources)
                .map(|v| v.tracks)
                .unwrap_or_default()),
            // A corrupt or older disposable index is not a valid snapshot.
            Err(_) => Ok(Vec::new()),
        }
    }
    pub fn load_index(&self, profile: &Profile) -> Result<Vec<Track>> {
        Ok(self
            .index_rows(profile)?
            .into_iter()
            .map(|r| r.track)
            .collect())
    }
    pub fn persist_index(&self, profile: &Profile, tracks: &[Track]) -> Result<()> {
        let (sources, path) = self.index_location(profile)?;
        let _lock = fs_safe::state_lock(&path.with_extension("lock")).map_err(explain)?;
        let mut known = self
            .index_rows(profile)?
            .into_iter()
            .map(|r| (r.track.source, r.stamp.map(|s| (r.track.sha256, s))))
            .collect::<HashMap<_, _>>();
        let rows = tracks
            .iter()
            .map(|track| {
                let stamp = known
                    .remove(&track.source)
                    .flatten()
                    .filter(|(hash, _)| hash == &track.sha256)
                    .map(|(_, stamp)| stamp);
                IndexRow {
                    track: track.clone(),
                    stamp,
                }
            })
            .collect();
        write_json(
            &path,
            &Index {
                version: 2,
                sources,
                tracks: rows,
            },
        )
    }
    fn save_scanned_index(&self, profile: &Profile, rows: Vec<IndexRow>) -> Result<()> {
        let (sources, path) = self.index_location(profile)?;
        let _lock = fs_safe::state_lock(&path.with_extension("lock")).map_err(explain)?;
        write_json(
            &path,
            &Index {
                version: 2,
                sources,
                tracks: rows,
            },
        )
    }
    pub fn scan<F: Fn(Progress) + Sync>(
        &self,
        profile: &Profile,
        progress: F,
    ) -> Result<Vec<Track>> {
        self.scan_live(profile, |p, _| progress(p))
    }
    pub fn scan_live<F: Fn(Progress, Option<&Track>) + Sync>(
        &self,
        profile: &Profile,
        progress: F,
    ) -> Result<Vec<Track>> {
        let (sources, _) = self.folders(profile)?;
        let mut paths = HashSet::new();
        for source in sources {
            fs_safe::directory(&source, false).map_err(explain)?;
            let mut queue = vec![source];
            while let Some(dir) = queue.pop() {
                if fs_safe::directory(&dir, false).is_err() {
                    continue;
                }
                let entries = match fs::read_dir(dir) {
                    Ok(e) => e,
                    Err(e)
                        if matches!(
                            e.kind(),
                            io::ErrorKind::NotFound | io::ErrorKind::PermissionDenied
                        ) =>
                    {
                        continue;
                    }
                    Err(e) => return Err(explain(e)),
                };
                for e in entries.flatten() {
                    let path = e.path();
                    let m = match fs::symlink_metadata(&path) {
                        Ok(m) => m,
                        Err(_) => continue,
                    };
                    if fs_safe::linked(&m) {
                        continue;
                    }
                    if m.is_dir() {
                        queue.push(path);
                    } else if m.is_file() && path.to_str().is_some() && supported(&path).is_some() {
                        paths.insert(path);
                    }
                }
            }
        }
        let mut paths = paths.into_iter().collect::<Vec<_>>();
        sort_paths(&mut paths);
        let old = self.index_rows(profile)?;
        let old = old
            .into_iter()
            .map(|r| (r.track.source.clone(), r))
            .collect::<HashMap<_, _>>();
        let number = paths.len();
        progress(
            Progress {
                completed: 0,
                total: number,
            },
            None,
        );
        let next = AtomicUsize::new(0);
        let result = Mutex::new((Vec::new(), 0usize));
        let workers = std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(2)
            .clamp(2, 8)
            .min(number);
        std::thread::scope(|scope| {
            for _ in 0..workers {
                scope.spawn(|| {
                    loop {
                        let i = next.fetch_add(1, Ordering::Relaxed);
                        let Some(path) = paths.get(i) else { break };
                        let item = scan_one(path, &old);
                        let mut res = result.lock().unwrap_or_else(|e| e.into_inner());
                        res.1 += 1;
                        progress(
                            Progress {
                                completed: res.1,
                                total: number,
                            },
                            item.as_ref().ok().map(|r| &r.track),
                        );
                        if let Ok(item) = item {
                            res.0.push(item);
                        }
                    }
                });
            }
        });
        let (mut result, _) = result.into_inner().unwrap_or_else(|e| e.into_inner());
        result.sort_by(|a, b| compare_tracks(&a.track, &b.track));
        let tracks = result.iter().map(|r| r.track.clone()).collect();
        self.save_scanned_index(profile, result)?;
        Ok(tracks)
    }
    pub fn review(&self, profile: &Profile, tracks: &[Track]) -> Result<Review> {
        let (sources, dest) = self.folders(profile)?;
        let template = pattern::Pattern::parse(&profile.pattern)?;
        let mut tracks = tracks.to_vec();
        tracks.sort_by(compare_tracks);
        let mut keys: HashMap<(String, String), HashSet<String>> = HashMap::new();
        for t in &tracks {
            if let Some(key) = tag_key(t) {
                keys.entry(key)
                    .or_default()
                    .insert(t.sha256.to_ascii_lowercase());
            }
        }
        let mut seen = HashSet::new();
        let mut planner = pattern::Planner::new(&dest, template);
        let mut rows = Vec::new();
        for track in tracks {
            let src = fs_safe::absolute(&track.source).map_err(explain)?;
            if !sources
                .iter()
                .any(|root| src != *root && src.starts_with(root))
                || !digest_syntax(&track.sha256)
            {
                return Err("An index row is outside the approved source folders or has an invalid SHA-256.".into());
            }
            let ext = supported(&src)
                .ok_or_else(|| "An index row has an unsupported extension.".to_owned())?;
            if !seen.insert(track.sha256.to_ascii_lowercase()) {
                rows.push(ReviewRow {
                    track,
                    destination: None,
                    status: "duplicate".into(),
                    message: Some("Byte-identical SHA-256; copy skipped.".into()),
                });
                continue;
            }
            let variant =
                tag_key(&track).is_some_and(|key| keys.get(&key).is_some_and(|h| h.len() > 1));
            let destination = text(&planner.target(&track, &ext, variant)?)?;
            rows.push(ReviewRow {
                track,
                destination: Some(destination),
                status: if variant { "variant" } else { "ready" }.into(),
                message: variant
                    .then(|| "Matching artist/title, different bytes; both versions kept.".into()),
            });
        }
        Ok(Review { rows })
    }
    pub fn export(&self, profile: &Profile, tracks: &[Track]) -> Result<Export> {
        self.export_live(profile, tracks, |_, _| {})
    }
    /// Stage and verify files in parallel; publish and receipt them in fixed
    /// plan order. The callback receives only committed/decided rows and is
    /// always called from the coordinating thread.
    pub fn export_live<F: Fn(Progress, Option<&ExportRow>) + Sync>(
        &self,
        profile: &Profile,
        tracks: &[Track],
        progress: F,
    ) -> Result<Export> {
        let workers = std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(2)
            .clamp(2, 4);
        self.export_inner(profile, tracks, progress, workers, |_| {})
    }
    fn export_inner<F, C>(
        &self,
        profile: &Profile,
        tracks: &[Track],
        progress: F,
        workers: usize,
        copy_started: C,
    ) -> Result<Export>
    where
        F: Fn(Progress, Option<&ExportRow>) + Sync,
        C: Fn(&Path) + Sync,
    {
        let (_, root) = self.folders(profile)?;
        let plan = self.review(profile, tracks)?;
        let root_text = text(&root)?;
        let key = profile
            .id
            .as_deref()
            .filter(|s| !s.is_empty())
            .map(id)
            .transpose()?
            .unwrap_or_else(|| "adhoc".into());
        let ledger_file = self
            .state()?
            .join("receipts")
            .join(format!("{key}-{}.json", digest(path_key(&root).as_bytes())));
        let _lock = fs_safe::state_lock(&ledger_file.with_extension("lock")).map_err(explain)?;
        let mut ledger = read_json::<ReceiptLedger>(&ledger_file)?.unwrap_or(ReceiptLedger {
            version: 1,
            destination: root_text.clone(),
            entries: HashMap::new(),
        });
        if ledger.version != 1 || ledger.destination != root_text {
            return Err("The export receipt does not match this destination.".into());
        }
        let mut managed = HashSet::new();
        for (check, receipt) in &ledger.entries {
            let path = relative_receipt(&root, &receipt.relative_path)?;
            if !digest_syntax(check)
                || !digest_syntax(&receipt.final_sha256)
                || !managed.insert(path_key(&path))
            {
                return Err("The export receipt contains ambiguous entries.".into());
            }
        }

        let total = plan.rows.len();
        let mut rows = Vec::with_capacity(total);
        progress(
            Progress {
                completed: 0,
                total,
            },
            None,
        );
        let mut plan = plan.rows.into_iter();
        'export: loop {
            let batch = plan.by_ref().take(workers).collect::<Vec<_>>();
            if batch.is_empty() {
                break;
            }
            // A group bounds both the number of threads and temporary files.
            // Hash checks on existing copies can run alongside fresh copies.
            let prepared = std::thread::scope(|scope| {
                let jobs = batch
                    .into_iter()
                    .map(|row| {
                        let (ledger, managed, root, copy_started) =
                            (&ledger, &managed, &root, &copy_started);
                        scope.spawn(move || {
                            prepare_export(profile, row, root, ledger, managed, copy_started)
                        })
                    })
                    .collect::<Vec<_>>();
                jobs.into_iter()
                    .map(|job| {
                        job.join()
                            .map_err(|_| "A copy worker stopped unexpectedly.".to_owned())?
                    })
                    .collect::<Result<Vec<_>>>()
            })?;
            for PreparedExport { mut result, copy } in prepared {
                let mut stop = false;
                if let Some(copy) = copy {
                    let PendingExport {
                        stage,
                        destination,
                        source,
                        source_stamp,
                        source_sha,
                        final_sha,
                        tags_written,
                    } = copy;
                    // A separate process or user may add a normalized alias while
                    // a copy is staging. Never trust the worker's earlier check.
                    let publish = current_source(&source, source_stamp.as_ref(), &source_sha)
                        .and_then(|()| match find_alias(&destination) {
                            Ok(None) => stage.publish(),
                            Ok(Some(_)) => Err(io::Error::new(
                                io::ErrorKind::AlreadyExists,
                                "Destination exists.",
                            )),
                            Err(e) => Err(e),
                        });
                    match publish {
                        Ok(()) => {
                            result.outcome = "copied".into();
                            result.message = tags_written.then(|| {
                                "Missing tags filled and verified on the copy only.".into()
                            });
                            let relative = destination
                                .strip_prefix(&root)
                                .map_err(|_| "Invalid copy receipt path.".to_owned())?;
                            ledger.entries.insert(
                                source_sha,
                                Receipt {
                                    relative_path: text(relative)?,
                                    final_sha256: final_sha,
                                },
                            );
                            managed.insert(path_key(&destination));
                            if let Err(e) = write_json(&ledger_file, &ledger) {
                                result.message = Some(format!(
                                    "Copied, then stopped: export receipt could not be saved ({e})."
                                ));
                                stop = true;
                            }
                        }
                        Err(e) if e.kind() == io::ErrorKind::AlreadyExists => {
                            result.outcome = "conflict".into();
                            result.message = Some(
                                "Destination appeared during export; nothing replaced.".into(),
                            );
                        }
                        Err(e) => {
                            result.outcome = "failed".into();
                            result.message = Some(explain(e));
                        }
                    }
                }
                rows.push(result);
                progress(
                    Progress {
                        completed: rows.len(),
                        total,
                    },
                    rows.last(),
                );
                // Dropping remaining prepared rows cleans private partial files;
                // their destination files have not been published.
                if stop {
                    break 'export;
                }
            }
        }
        Ok(Export { rows })
    }
}

struct PreparedExport {
    result: ExportRow,
    copy: Option<PendingExport>,
}
struct PendingExport {
    stage: fs_safe::StagedCopy,
    destination: PathBuf,
    source: PathBuf,
    source_stamp: Option<FileStamp>,
    source_sha: String,
    final_sha: String,
    tags_written: bool,
}
fn prepare_export(
    profile: &Profile,
    row: ReviewRow,
    root: &Path,
    ledger: &ReceiptLedger,
    managed: &HashSet<String>,
    copy_started: &impl Fn(&Path),
) -> Result<PreparedExport> {
    let mut result = ExportRow {
        source: row.track.source.clone(),
        destination: row.destination.clone(),
        outcome: "skippedDuplicate".into(),
        message: row.message.clone(),
    };
    let Some(dest) = row.destination else {
        return Ok(PreparedExport { result, copy: None });
    };
    let src = fs_safe::absolute(&row.track.source).map_err(explain)?;
    let dst = fs_safe::absolute(&dest).map_err(explain)?;
    let sha = row.track.sha256.to_ascii_lowercase();
    if let Some(receipt) = ledger.entries.get(&sha) {
        let prior = relative_receipt(root, &receipt.relative_path)?;
        result.destination = Some(text(&prior)?);
        match (hash_at(&src), hash_at(&prior)) {
            (Ok(a), Ok(b)) if a == sha && b == receipt.final_sha256 => {
                result.outcome = "alreadyPresent".into();
                result.message = Some("Verified an earlier copy for this profile.".into());
            }
            (Ok(a), _) if a != sha => {
                result.outcome = "failed".into();
                result.message =
                    Some("The source changed after the scan; rescan before exporting.".into());
            }
            _ => {
                result.outcome = "conflict".into();
                result.message =
                    Some("An earlier copy no longer matches its receipt; nothing replaced.".into());
            }
        }
        return Ok(PreparedExport { result, copy: None });
    }
    let actual = match find_alias(&dst) {
        Ok(a) => a,
        Err(e) => {
            result.outcome = "conflict".into();
            result.message = Some(explain(e));
            return Ok(PreparedExport { result, copy: None });
        }
    };
    let is_managed = managed.contains(&path_key(&dst));
    if actual.is_some() || is_managed {
        let equal = !is_managed
            && actual
                .as_deref()
                .filter(|p| *p == dst)
                .and_then(|p| hash_at(p).ok())
                .as_ref()
                == Some(&sha)
            && hash_at(&src).ok().as_ref() == Some(&sha);
        result.outcome = if equal { "alreadyPresent" } else { "conflict" }.into();
        result.message = Some(
            if equal {
                "Byte-identical file is already present."
            } else {
                "Destination already exists or is reserved; nothing replaced."
            }
            .into(),
        );
        return Ok(PreparedExport { result, copy: None });
    }

    let staged = fs_safe::stage_copy(&dst, |stage| {
        let mut from = fs_safe::open(&src)?;
        let source_stamp = FileStamp::read(&from);
        copy_started(&src);
        if track_hash(&mut from, Some(stage))? != sha {
            return Err(fs_safe::error(
                "The source changed after the scan; rescan before exporting.",
            ));
        }
        let tags_written = profile.lookup_enabled
            && row.track.metadata_source.as_deref() == Some("acoustid")
            && audio::fill_staged(stage, &row.track)?;
        let final_sha = if tags_written {
            stage.seek(SeekFrom::Start(0))?;
            track_hash(stage, None)?
        } else {
            sha.clone()
        };
        Ok((final_sha, tags_written, source_stamp))
    });
    match staged {
        Ok((stage, (final_sha, tags_written, source_stamp))) => Ok(PreparedExport {
            result,
            copy: Some(PendingExport {
                stage,
                destination: dst,
                source: src,
                source_stamp,
                source_sha: sha,
                final_sha,
                tags_written,
            }),
        }),
        Err(e) => {
            result.outcome = "failed".into();
            result.message = Some(explain(e));
            Ok(PreparedExport { result, copy: None })
        }
    }
}
fn current_source(path: &Path, stamp: Option<&FileStamp>, sha: &str) -> io::Result<()> {
    // A different worker may take much longer to stage. Refuse to publish a
    // stale earlier snapshot while it waits for the group to finish.
    let mut source = fs_safe::open(path)?;
    let same = if stamp.is_some() && FileStamp::read(&source).as_ref() == stamp {
        true
    } else {
        track_hash(&mut source, None)? == sha
    };
    if !same {
        return Err(fs_safe::error(
            "The source changed after staging; rescan before exporting.",
        ));
    }
    Ok(())
}

fn compare_tracks(a: &Track, b: &Track) -> std::cmp::Ordering {
    fold(&a.source)
        .cmp(&fold(&b.source))
        .then(a.source.cmp(&b.source))
        .then(a.sha256.cmp(&b.sha256))
}
fn sort_paths(paths: &mut [PathBuf]) {
    paths.sort_by(|a, b| path_key(a).cmp(&path_key(b)).then(a.cmp(b)));
}
fn tag_key(t: &Track) -> Option<(String, String)> {
    let normalize = |s: &str| fold(&s.split_whitespace().collect::<Vec<_>>().join(" "));
    if t.artist.trim().is_empty() || t.title.trim().is_empty() {
        None
    } else {
        Some((normalize(&t.artist), normalize(&t.title)))
    }
}
fn relative_receipt(root: &Path, rel: &str) -> Result<PathBuf> {
    let p = Path::new(rel);
    if rel.is_empty()
        || p.components()
            .any(|c| !matches!(c, std::path::Component::Normal(_)))
    {
        return Err("An export receipt has an unsafe path.".into());
    }
    Ok(root.join(p))
}
fn find_alias(path: &Path) -> io::Result<Option<PathBuf>> {
    let parent = path
        .parent()
        .ok_or_else(|| fs_safe::error("Missing output folder."))?;
    match fs_safe::directory(parent, false) {
        Ok(()) => (),
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e),
    };
    let key = path_key(path);
    for e in fs::read_dir(parent)? {
        let p = e?.path();
        if path_key(&p) == key {
            return Ok(Some(p));
        }
    }
    Ok(None)
}
fn scan_one(path: &Path, old: &HashMap<String, IndexRow>) -> io::Result<IndexRow> {
    let source = path
        .to_str()
        .ok_or_else(|| fs_safe::error("Not a Unicode path."))?;
    let mut file = fs_safe::open(path)?;
    let stamp = FileStamp::read(&file);
    if let Some(previous) = old
        .get(source)
        .filter(|r| stamp.is_some() && r.stamp == stamp)
    {
        return Ok(IndexRow {
            track: previous.track.clone(),
            stamp,
        });
    }
    let sha = track_hash(&mut file, None)?;
    let track = if let Some(previous) = old.get(source).filter(|r| r.track.sha256 == sha) {
        previous.track.clone()
    } else {
        let format = supported(path).ok_or_else(|| fs_safe::error("Unsupported audio format."))?;
        file.seek(SeekFrom::Start(0))?;
        let mut track = Track {
            source: source.into(),
            format,
            sha256: sha,
            ..Track::default()
        };
        audio::inspect(file.try_clone()?, &mut track);
        track
    };
    if stamp.is_some() && FileStamp::read(&file) != stamp {
        return Err(fs_safe::error("File changed while indexing."));
    }
    Ok(IndexRow { track, stamp })
}

pub fn preview_pattern(pattern: &str, track: &Track) -> Result<String> {
    let template = pattern::Pattern::parse(pattern)?;
    let src = Path::new(&track.source);
    let ext = supported(src).unwrap_or_else(|| "mp3".into());
    Ok(template
        .relative(track, &ext)
        .to_string_lossy()
        .into_owned())
}

#[cfg(test)]
mod parallel_export_tests {
    use super::*;
    use std::{
        sync::Condvar,
        thread::{self, ThreadId},
        time::Duration,
    };

    struct Scenario {
        root: PathBuf,
        output: PathBuf,
        engine: Engine,
        profile: Profile,
        tracks: Vec<Track>,
    }
    impl Scenario {
        fn new() -> Self {
            let root = std::env::temp_dir()
                .canonicalize()
                .unwrap()
                .join(format!("music-sorter-parallel-{}", uuid::Uuid::new_v4()));
            let input = root.join("source");
            fs::create_dir_all(&input).unwrap();
            let output = root.join("output");
            let engine = Engine::new(root.join("state"));
            let profile = Profile {
                name: "Parallel test".into(),
                sources: vec![text(&input).unwrap()],
                destination: text(&output).unwrap(),
                pattern: "{FileName}".into(),
                ..Profile::default()
            };
            // Synthetic bytes suffice to isolate the disk transport; real
            // music/tag/fingerprint coverage remains in the integration tests.
            let tracks = [("b.mp3", 1), ("a.mp3", 2)]
                .into_iter()
                .map(|(name, byte)| {
                    let path = input.join(name);
                    let bytes = vec![byte; 256 * 1024];
                    fs::write(&path, &bytes).unwrap();
                    Track {
                        source: text(&path).unwrap(),
                        format: "mp3".into(),
                        sha256: digest(&bytes),
                        ..Track::default()
                    }
                })
                .collect();
            Self {
                root,
                output,
                engine,
                profile,
                tracks,
            }
        }
        fn assert_no_partials(&self) {
            assert!(
                !fs::read_dir(&self.output)
                    .unwrap()
                    .flatten()
                    .any(|f| f.file_name().to_string_lossy().ends_with(".partial"))
            );
        }
    }
    impl Drop for Scenario {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.root);
        }
    }

    #[test]
    fn two_real_copy_workers_overlap_and_report_in_plan_order() {
        let s = Scenario::new();
        let threads = (Mutex::new(HashSet::<ThreadId>::new()), Condvar::new());
        let progress = Mutex::new(Vec::new());
        let main = thread::current().id();
        let export = s
            .engine
            .export_inner(
                &s.profile,
                &s.tracks,
                |p, row| {
                    assert_eq!(thread::current().id(), main);
                    progress.lock().unwrap().push((
                        p.completed,
                        p.total,
                        row.map(|r| r.source.clone()),
                    ));
                },
                2,
                |_| {
                    // This hook is called after both source and stage file handles are
                    // open. A serial copy cannot pass the two-participant wait.
                    let mut ids = threads.0.lock().unwrap();
                    assert_ne!(thread::current().id(), main);
                    ids.insert(thread::current().id());
                    threads.1.notify_all();
                    let (ids, _) = threads
                        .1
                        .wait_timeout_while(ids, Duration::from_secs(4), |v| v.len() < 2)
                        .unwrap();
                    assert_eq!(
                        ids.len(),
                        2,
                        "both copy workers must have live handles concurrently"
                    );
                },
            )
            .unwrap();
        assert_eq!(
            export
                .rows
                .iter()
                .map(|r| r.outcome.as_str())
                .collect::<Vec<_>>(),
            ["copied", "copied"]
        );
        assert!(export.rows[0].source.ends_with("a.mp3"));
        assert!(export.rows[1].source.ends_with("b.mp3"));
        let expected = [
            (0, 2, None),
            (1, 2, Some(export.rows[0].source.clone())),
            (2, 2, Some(export.rows[1].source.clone())),
        ];
        assert_eq!(progress.into_inner().unwrap(), expected);
        assert_eq!(
            fs::read(s.output.join("a.mp3")).unwrap(),
            vec![2; 256 * 1024]
        );
        assert_eq!(
            fs::read(s.output.join("b.mp3")).unwrap(),
            vec![1; 256 * 1024]
        );
        s.assert_no_partials();
    }

    #[test]
    fn an_arriving_destination_is_not_replaced_by_a_parallel_copy() {
        let s = Scenario::new();
        let path = s.output.join("a.mp3");
        let export = s
            .engine
            .export_inner(
                &s.profile,
                &s.tracks,
                |_, _| {},
                2,
                |source| {
                    if source.ends_with("a.mp3") {
                        fs::write(&path, b"another process").unwrap();
                    }
                },
            )
            .unwrap();
        assert_eq!(export.rows[0].outcome, "conflict");
        assert_eq!(export.rows[1].outcome, "copied");
        assert_eq!(fs::read(path).unwrap(), b"another process");
        s.assert_no_partials();
    }

    #[test]
    fn changed_source_after_parallel_staging_is_not_published() {
        let s = Scenario::new();
        let second = s.root.join("source/b.mp3");
        let export = s
            .engine
            .export_inner(
                &s.profile,
                &s.tracks,
                |p, _| {
                    if p.completed == 1 {
                        fs::write(&second, b"changed after staging").unwrap();
                    }
                },
                2,
                |_| {},
            )
            .unwrap();
        assert_eq!(export.rows[0].outcome, "copied");
        assert_eq!(export.rows[1].outcome, "failed");
        assert!(
            export.rows[1]
                .message
                .as_ref()
                .unwrap()
                .contains("source changed after staging")
        );
        assert!(!s.output.join("b.mp3").exists());
        assert_eq!(fs::read(&second).unwrap(), b"changed after staging");
        s.assert_no_partials();
    }

    #[test]
    fn a_receipt_failure_publishes_no_later_staged_copies() {
        let s = Scenario::new();
        let bad_ledger = s.engine.state().unwrap().join("receipts").join(format!(
            "adhoc-{}.json",
            digest(path_key(&s.output).as_bytes())
        ));
        let progress = Mutex::new(Vec::new());
        let export = s
            .engine
            .export_inner(
                &s.profile,
                &s.tracks,
                |p, _| progress.lock().unwrap().push(p.completed),
                2,
                |source| {
                    if source.ends_with("a.mp3") {
                        fs::create_dir(&bad_ledger).unwrap();
                    }
                },
            )
            .unwrap();
        assert_eq!(export.rows.len(), 1);
        assert_eq!(export.rows[0].outcome, "copied");
        assert!(
            export.rows[0]
                .message
                .as_ref()
                .unwrap()
                .contains("receipt could not be saved")
        );
        assert_eq!(progress.into_inner().unwrap(), [0, 1]);
        assert!(s.output.join("a.mp3").exists());
        assert!(!s.output.join("b.mp3").exists());
        s.assert_no_partials();
    }
}
