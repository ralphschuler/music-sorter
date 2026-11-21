#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod online;

use music_sorter_engine::{Engine, Export, Profile, Review, Track};
use serde::Serialize;
use std::{
    collections::HashSet,
    sync::{Arc, Mutex},
    time::Duration,
};
use tauri::{AppHandle, Manager, State};
use tauri_plugin_dialog::DialogExt;

#[derive(Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
struct IndexSnapshot {
    revision: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    tracks: Option<Arc<Vec<Track>>>,
    indexing: bool,
    completed: usize,
    total: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    message: Option<String>,
}

#[derive(Default)]
struct Active {
    id: Option<String>,
    snapshot: IndexSnapshot,
    attempted: HashSet<(String, String)>,
    scan_due: bool,
    online_backoff: Option<(std::time::Instant, String)>,
}

struct Runtime {
    engine: Engine,
    active: Arc<Mutex<Active>>,
    disk: Arc<Mutex<()>>,
    export: Mutex<Option<ExportProgress>>,
}

fn lookup_profile(engine: &Engine, id: &str) -> Result<Profile, String> {
    engine
        .profiles()?
        .into_iter()
        .find(|p| p.id.as_deref() == Some(id))
        .ok_or_else(|| "Profile not found.".into())
}

#[tauri::command]
async fn pick_folders(app: AppHandle, multiple: bool) -> Result<Vec<String>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let picker = app.dialog().file();
        let folders = if multiple {
            picker.blocking_pick_folders().unwrap_or_default()
        } else {
            picker.blocking_pick_folder().into_iter().collect()
        };
        folders
            .into_iter()
            .map(|folder| {
                folder
                    .into_path()
                    .ok()
                    .and_then(|p| p.into_os_string().into_string().ok())
                    .ok_or_else(|| "That folder path cannot be opened.".into())
            })
            .collect()
    })
    .await
    .map_err(|_| "The folder picker stopped unexpectedly.".to_string())?
}

#[tauri::command]
async fn list_profiles(state: State<'_, Arc<Runtime>>) -> Result<Vec<Profile>, String> {
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let _disk = state.disk.lock().map_err(|_| "Profile storage is busy.")?;
        state.engine.profiles()
    })
    .await
    .map_err(|_| "Profile loading stopped unexpectedly.".to_string())?
}

#[tauri::command]
async fn save_profile(state: State<'_, Arc<Runtime>>, profile: Profile) -> Result<Profile, String> {
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let (previous, saved) = {
            let _disk = state.disk.lock().map_err(|_| "Profile storage is busy.")?;
            let previous = profile
                .id
                .as_deref()
                .and_then(|id| lookup_profile(&state.engine, id).ok());
            (previous, state.engine.save_profile(profile)?)
        };
        let mut active = state.active.lock().map_err(|_| "Index is busy.")?;
        if active.id.is_some()
            && active.id == saved.id
            && !previous.is_some_and(|p| {
                p.sources == saved.sources && p.lookup_enabled == saved.lookup_enabled
            })
        {
            active.scan_due = true;
            active.online_backoff = None;
            active.attempted.clear();
        }
        Ok(saved)
    })
    .await
    .map_err(|_| "Saving the profile stopped unexpectedly.".to_string())?
}

#[tauri::command]
async fn open_profile(
    state: State<'_, Arc<Runtime>>,
    profile_id: String,
) -> Result<IndexSnapshot, String> {
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let tracks = {
            let _disk = state.disk.lock().map_err(|_| "Profile storage is busy.")?;
            let p = lookup_profile(&state.engine, &profile_id)?;
            state.engine.load_index(&p)?
        };
        let mut active = state.active.lock().map_err(|_| "Index is busy.")?;
        if active.id.as_deref() != Some(&profile_id) {
            active.id = Some(profile_id);
            active.attempted.clear();
            active.online_backoff = None;
            let revision = active.snapshot.revision + 1;
            active.snapshot = IndexSnapshot {
                revision,
                total: tracks.len(),
                completed: tracks.len(),
                tracks: Some(Arc::new(tracks)),
                indexing: true,
                message: None,
            };
        }
        active.scan_due = true;
        Ok(active.snapshot.clone())
    })
    .await
    .map_err(|_| "Opening the library stopped unexpectedly.".to_string())?
}

#[tauri::command]
async fn index_snapshot(
    state: State<'_, Arc<Runtime>>,
    profile_id: String,
    revision: Option<u64>,
) -> Result<IndexSnapshot, String> {
    let active = state.active.lock().map_err(|_| "Index is busy.")?;
    if active.id.as_deref() != Some(&profile_id) {
        return Err("This profile is not open.".into());
    }
    let s = &active.snapshot;
    Ok(IndexSnapshot {
        tracks: (revision != Some(s.revision))
            .then(|| s.tracks.clone())
            .flatten(),
        revision: s.revision,
        indexing: s.indexing,
        completed: s.completed,
        total: s.total,
        message: s.message.clone(),
    })
}

#[tauri::command]
async fn review_profile(
    state: State<'_, Arc<Runtime>>,
    profile_id: String,
) -> Result<Review, String> {
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let (p, tracks) = {
            let _disk = state.disk.lock().map_err(|_| "Profile storage is busy.")?;
            let p = lookup_profile(&state.engine, &profile_id)?;
            let tracks = state.engine.load_index(&p)?;
            (p, tracks)
        };
        state.engine.review(&p, &tracks)
    })
    .await
    .map_err(|_| "Review stopped unexpectedly.".to_string())?
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ExportProgress {
    profile_id: String,
    completed: usize,
    total: usize,
    running: bool,
}

struct ExportGuard(Arc<Runtime>);
impl Drop for ExportGuard {
    fn drop(&mut self) {
        if let Ok(mut slot) = self.0.export.lock() {
            if let Some(job) = slot.as_mut() {
                job.running = false;
            }
        }
    }
}

#[tauri::command]
async fn export_progress(
    state: State<'_, Arc<Runtime>>,
    profile_id: String,
) -> Result<Option<ExportProgress>, String> {
    let slot = state
        .export
        .lock()
        .map_err(|_| "Sort status is unavailable.")?;
    Ok(slot
        .as_ref()
        .filter(|job| job.profile_id == profile_id)
        .cloned())
}

#[tauri::command]
async fn export_profile(
    state: State<'_, Arc<Runtime>>,
    profile_id: String,
) -> Result<Export, String> {
    let state = state.inner().clone();
    {
        let mut slot = state
            .export
            .lock()
            .map_err(|_| "Sort status is unavailable.")?;
        if slot.as_ref().is_some_and(|job| job.running) {
            return Err("A sort is already in progress.".into());
        }
        *slot = Some(ExportProgress {
            profile_id: profile_id.clone(),
            completed: 0,
            total: 0,
            running: true,
        });
    }
    let guard = ExportGuard(state.clone());
    tauri::async_runtime::spawn_blocking(move || {
        let _guard = guard;
        let (p, tracks) = {
            let _disk = state.disk.lock().map_err(|_| "Profile storage is busy.")?;
            let p = lookup_profile(&state.engine, &profile_id)?;
            let tracks = state.engine.load_index(&p)?;
            (p, tracks)
        };
        // The profile/index is a frozen input. File I/O and progress never hold the
        // profile-storage lock, so other commands stay available during sorting.
        state.engine.export_live(&p, &tracks, |progress, _| {
            if let Ok(mut slot) = state.export.lock() {
                if let Some(job) = slot.as_mut() {
                    job.completed = progress.completed;
                    job.total = progress.total;
                }
            }
        })
    })
    .await
    .map_err(|_| "Sorting stopped unexpectedly.".to_string())?
}

fn update(state: &Runtime, id: &str, f: impl FnOnce(&mut Active)) {
    if let Ok(mut active) = state.active.lock() {
        if active.id.as_deref() == Some(id) {
            f(&mut active);
        }
    }
}

fn publish(state: &Runtime, id: &str, tracks: Vec<Track>, message: Option<String>) {
    update(state, id, |a| {
        let s = &mut a.snapshot;
        if s.tracks.as_deref() != Some(&tracks) {
            s.revision += 1;
            s.tracks = Some(Arc::new(tracks));
        }
        s.total = s.tracks.as_ref().map_or(0, |tracks| tracks.len());
        s.completed = s.total;
        s.indexing = false;
        s.message = message;
    });
}

fn index_pass(state: &Runtime, id: &str) -> Result<(), String> {
    let p = {
        let _disk = state.disk.lock().map_err(|_| "Profile storage is busy.")?;
        lookup_profile(&state.engine, id)?
    };
    // While a new index is built, expose completed tracks in batches. An existing
    // index stays visible until the atomic replacement is available.
    let show_partial = state.active.lock().ok().is_some_and(|a| {
        a.id.as_deref() == Some(id)
            && a.snapshot
                .tracks
                .as_ref()
                .is_none_or(|tracks| tracks.is_empty())
    });
    let partial = Mutex::new((Vec::<Track>::new(), std::time::Instant::now()));
    let mut tracks = state.engine.scan_live(&p, |progress, track| {
        if show_partial {
            if let Some(track) = track {
                if let Ok(mut partial) = partial.lock() {
                    partial.0.push(track.clone());
                    if partial.0.len() == 1 || partial.1.elapsed() >= Duration::from_millis(400) {
                        partial.1 = std::time::Instant::now();
                        let batch = Arc::new(partial.0.clone());
                        update(state, id, |a| {
                            a.snapshot.revision += 1;
                            a.snapshot.tracks = Some(batch);
                        });
                    }
                }
            }
        }
        update(state, id, |a| {
            a.snapshot.completed = a.snapshot.completed.max(progress.completed);
            a.snapshot.total = progress.total;
        });
    })?;
    publish(state, id, tracks.clone(), None);
    if !p.lookup_enabled {
        return Ok(());
    }
    if let Some((_, message)) = state
        .active
        .lock()
        .ok()
        .and_then(|a| a.online_backoff.clone())
        .filter(|(until, _)| *until > std::time::Instant::now())
    {
        return Err(message);
    }
    let key = match online::read_key()? {
        Some(key) => key,
        None => {
            return Err("Online lookup is enabled, but no AcoustID key is configured. Set a key in Settings or ACOUSTID_API_KEY.".into());
        }
    };
    let client = online::client()?;
    let mut changed = false;
    let mut error = None;
    let mut attempts = 0;
    for track in &mut tracks {
        if attempts == 8 {
            break;
        }
        if track.fingerprint.is_none() || (!track.artist.is_empty() && !track.title.is_empty()) {
            continue;
        }
        let marker = (track.source.clone(), track.sha256.clone());
        let eligible = {
            let mut active = state.active.lock().map_err(|_| "Index is busy.")?;
            active.id.as_deref() == Some(id) && active.attempted.insert(marker.clone())
        };
        if !eligible {
            continue;
        }
        {
            let _disk = state.disk.lock().map_err(|_| "Profile storage is busy.")?;
            let current = lookup_profile(&state.engine, id)?;
            if !current.lookup_enabled || current.sources != p.sources {
                break;
            }
        }
        attempts += 1;
        match online::enrich(&client, &key, track) {
            Ok(()) => changed |= track.metadata_source.as_deref() == Some("acoustid"),
            Err(message) => {
                // Transient errors stay eligible, but do not pile requests onto a failing service.
                update(state, id, |a| {
                    a.attempted.remove(&marker);
                    a.online_backoff = Some((
                        std::time::Instant::now() + Duration::from_secs(90),
                        message.clone(),
                    ));
                });
                error = Some(message);
                break;
            }
        }
    }
    if changed {
        let _disk = state.disk.lock().map_err(|_| "Profile storage is busy.")?;
        let current = lookup_profile(&state.engine, id)?;
        if current.lookup_enabled && current.sources == p.sources {
            state.engine.persist_index(&p, &tracks)?;
            publish(state, id, tracks, None);
        }
    }
    if let Some(error) = error {
        return Err(error);
    }
    Ok(())
}

fn run_indexer(state: Arc<Runtime>) {
    let mut last_scan = std::time::Instant::now();
    loop {
        std::thread::sleep(Duration::from_millis(400));
        let id = {
            let Ok(mut active) = state.active.lock() else {
                return;
            };
            if active.id.is_none()
                || !(active.scan_due || last_scan.elapsed() >= Duration::from_secs(12))
            {
                continue;
            }
            active.scan_due = false;
            active.snapshot.indexing = true;
            active.snapshot.completed = 0;
            active.id.clone().unwrap_or_default()
        };
        if let Err(error) = index_pass(&state, &id) {
            update(&state, &id, |a| {
                a.snapshot.indexing = false;
                a.snapshot.message = Some(error);
            });
        }
        last_scan = std::time::Instant::now();
    }
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            let path = app.path().app_data_dir()?;
            let state = Arc::new(Runtime {
                engine: Engine::new(path),
                active: Arc::new(Mutex::new(Active::default())),
                disk: Arc::new(Mutex::new(())),
                export: Mutex::new(None),
            });
            app.manage(state.clone());
            std::thread::Builder::new()
                .name("library-indexer".into())
                .spawn(move || run_indexer(state))?;
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            pick_folders,
            list_profiles,
            save_profile,
            open_profile,
            index_snapshot,
            review_profile,
            export_profile,
            export_progress,
            online::acoustid_status,
            online::acoustid_save,
            online::acoustid_delete,
            online::acoustid_test,
        ])
        .run(tauri::generate_context!())
        .expect("Music Sorter could not start");
}

#[cfg(test)]
mod ipc_tests {
    use super::*;
    use serde_json::{json, Value};
    use tauri::test::{get_ipc_response, mock_builder, mock_context, noop_assets, INVOKE_KEY};

    #[test]
    fn frontend_ipc_saves_indexes_and_sorts_without_mutating_source() {
        use std::fs;
        let temp = std::env::temp_dir().canonicalize().unwrap().join(format!(
            "music-sorter-ipc-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let _cleanup = Cleanup(temp.clone());
        let source = temp.join("input");
        let destination = temp.join("output");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&destination).unwrap();
        let state = Arc::new(Runtime {
            engine: Engine::new(temp.join("state")),
            active: Arc::new(Mutex::new(Active::default())),
            disk: Arc::new(Mutex::new(())),
            export: Mutex::new(None),
        });
        let app = mock_builder()
            .manage(state.clone())
            .invoke_handler(tauri::generate_handler![
                save_profile,
                list_profiles,
                open_profile,
                index_snapshot,
                review_profile,
                export_profile,
                export_progress
            ])
            .build(mock_context(noop_assets()))
            .unwrap();
        let webview = tauri::WebviewWindowBuilder::new(&app, "main", Default::default())
            .build()
            .unwrap();
        let invoke = |cmd: &str, body: Value| {
            get_ipc_response(
                &webview,
                tauri::webview::InvokeRequest {
                    cmd: cmd.into(),
                    callback: tauri::ipc::CallbackFn(0),
                    error: tauri::ipc::CallbackFn(1),
                    url: if cfg!(windows) {
                        "http://tauri.localhost"
                    } else {
                        "tauri://localhost"
                    }
                    .parse()
                    .unwrap(),
                    body: tauri::ipc::InvokeBody::Json(body),
                    headers: Default::default(),
                    invoke_key: INVOKE_KEY.to_string(),
                },
            )
            .unwrap()
            .deserialize::<Value>()
            .unwrap()
        };
        let mut saved = invoke(
            "save_profile",
            json!({ "profile": {
                "name": "IPC smoke", "sources": [source], "destination": destination,
                "pattern": "{artist}/{title}", "lookupEnabled": false
            }}),
        );
        assert_eq!(saved["name"], "IPC smoke");
        let id = saved["id"].clone();
        assert!(id.as_str().is_some_and(|id| !id.is_empty()));
        saved["columns"] = json!([{"id": "format", "width": 110}]);
        assert_eq!(invoke("save_profile", json!({"profile": saved}))["id"], id);
        let persisted = invoke("list_profiles", json!({}));
        assert_eq!(persisted.as_array().unwrap().len(), 1);
        assert_eq!(persisted[0]["columns"][0]["id"], "format");
        assert_eq!(persisted[0]["columns"][0]["width"], 110);

        let original = source.join("fixture.wav");
        let mut wav = b"RIFF".to_vec();
        wav.extend(68u32.to_le_bytes());
        wav.extend(b"WAVEfmt ");
        wav.extend(16u32.to_le_bytes());
        wav.extend(1u16.to_le_bytes());
        wav.extend(1u16.to_le_bytes());
        wav.extend(11025u32.to_le_bytes());
        wav.extend(22050u32.to_le_bytes());
        wav.extend(2u16.to_le_bytes());
        wav.extend(16u16.to_le_bytes());
        wav.extend(b"data");
        wav.extend(32u32.to_le_bytes());
        wav.extend([0; 32]);
        fs::write(&original, &wav).unwrap();
        let profile = serde_json::from_value(persisted[0].clone()).unwrap();
        assert_eq!(state.engine.scan(&profile, |_| {}).unwrap().len(), 1);

        let args = json!({"profileId": id});
        let opened = invoke("open_profile", args.clone());
        assert_eq!(opened["tracks"].as_array().unwrap().len(), 1);
        let progress = invoke(
            "index_snapshot",
            json!({
                "profileId": id, "revision": opened["revision"]
            }),
        );
        assert!(progress.get("tracks").is_none());
        assert_eq!(
            invoke("review_profile", args.clone())["rows"]
                .as_array()
                .unwrap()
                .len(),
            1
        );
        assert!(invoke("export_progress", args.clone()).is_null());
        let export = invoke("export_profile", args.clone());
        assert_eq!(export["rows"][0]["outcome"], "copied");
        let output = export["rows"][0]["destination"].as_str().unwrap();
        assert_eq!(fs::read(output).unwrap(), wav);
        assert_eq!(fs::read(original).unwrap(), wav);
        let status = invoke("export_progress", args);
        assert_eq!(status["running"], false);
        assert_eq!(status["completed"], 1);
        assert_eq!(status["total"], 1);
    }

    struct Cleanup(std::path::PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
}
