#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[cfg(target_os = "linux")]
    #[test]
    fn linux_uses_a_real_secret_service_without_accessing_any_user_key() {
        // Entry construction does not open a session, access the bus, or write a key.
        let entry = credential().unwrap();
        assert!(entry
            .get_credential()
            .is::<keyring::secret_service::SsCredential>());
    }

    #[test]
    fn key_input_is_never_interpreted_as_query_text() {
        assert_eq!(normalize_key(" abc123_X-d "), Some("abc123_X-d"));
        assert!(normalize_key("abc&client=no").is_none());
        assert!(normalize_key("abc\nxyz").is_none());
        assert!(normalize_key("").is_none());
    }

    #[test]
    fn selects_keychain_then_environment_then_missing_without_exposing_the_key() {
        use KeySource::*;
        let select = |saved: Result<Option<String>, String>, env: Option<&str>| {
            select_key(saved, || env.map(str::to_owned))
        };
        let saved = select(Ok(Some("stored_key".into())), Some("env_key"))
            .unwrap()
            .unwrap();
        assert_eq!(saved.source, Keychain);
        assert_eq!(saved.value, "stored_key");
        let fallback = select(Ok(None), Some(" env_key ")).unwrap().unwrap();
        assert_eq!(fallback.source, Environment);
        assert_eq!(fallback.value, "env_key");
        assert_eq!(
            select(Err("unavailable".into()), Some("env_key"))
                .unwrap()
                .unwrap()
                .source,
            Environment
        );
        assert!(select(Err("unavailable".into()), None).is_err());
        assert!(select(Ok(None), Some("bad&value")).is_err());
        assert!(select(Ok(None), Some(" ")).unwrap().is_none());
        assert!(select(Ok(None), None).unwrap().is_none());
        let status = serde_json::to_value(key_status(Some(fallback))).unwrap();
        assert_eq!(status, json!({"configured":true,"source":"environment"}));
    }

    #[test]
    fn acoustid_rejects_ambiguous_recordings_even_in_one_match() {
        let record = |title| json!({"title":title, "artists":[{"name":"Artist"}]});
        let matched = |records| json!({"results":[{"score":0.98,"recordings":records}]});
        let good = matched(vec![record("Track")]);
        let ambiguous = matched(vec![record("Track"), record("Remix")]);
        assert_eq!(acoustid_match(&good, "", "").unwrap().title, "Track");
        assert!(acoustid_match(&ambiguous, "", "").is_none());
        assert!(acoustid_match(&good, "Existing Artist", "").is_none());
    }
}

use music_sorter_engine::Track;
use reqwest::blocking::Client;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::{
    sync::Mutex,
    time::{Duration, Instant},
};

const SERVICE: &str = "de.nyphon.musicsorter";
const KEY_ENV: &str = "ACOUSTID_API_KEY";
const LOOKUP: &str = "https://api.acoustid.org/v2/lookup";
const KEY_PROBE: &str = "AQAAK0tCSVESSUrA_6ijFnOiibiO_EXyCLstwc0PMsnwI8aP5xlCZxeaqdiP_HgOvQrxI-KUZ6gSvTgSXULuG22O78Sl7RGelWD8JEWYJbnkIXkNnEzwZiOceEasKHog1Wkw_ojzxCP2JujzVABCAFDSgKMA0ZRRIIBzThOIBDEsC6IIQ4AhACgTxhAqCA";
static LAST_REQUEST: Mutex<Option<Instant>> = Mutex::new(None);
static KEY_ACCESS: Mutex<()> = Mutex::new(());

#[derive(Serialize)]
pub struct KeyStatus {
    configured: bool,
    source: Option<KeySource>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
enum KeySource {
    Keychain,
    Environment,
}

struct ResolvedKey {
    source: KeySource,
    value: String,
}
#[derive(Serialize)]
pub struct KeyTest {
    ok: bool,
    message: String,
}
#[derive(Default, Deserialize)]
struct Match {
    title: String,
    artist: String,
}

fn normalize_key(value: &str) -> Option<&str> {
    let value = value.trim();
    (!value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_'))
    .then_some(value)
}

#[cfg(target_os = "linux")]
fn credential() -> Result<keyring::Entry, String> {
    // Bind explicitly rather than relying on keyring's default (which can
    // otherwise silently become its memory-only test backend).
    keyring::secret_service::SsCredential::new_with_target(
        None,
        SERVICE,
        "acoustid-application-key",
    )
    .map(|credential| keyring::Entry::new_with_credential(Box::new(credential)))
    .map_err(|_| "The Linux desktop Secret Service is unavailable.".into())
}

#[cfg(any(target_os = "macos", target_os = "windows"))]
fn credential() -> Result<keyring::Entry, String> {
    keyring::Entry::new(SERVICE, "acoustid-application-key")
        .map_err(|_| "The operating system credential store is unavailable.".into())
}

#[cfg(not(any(target_os = "linux", target_os = "macos", target_os = "windows")))]
compile_error!(
    "Music Sorter is configured to store credentials on Linux, macOS, and Windows only."
);

fn read_keychain() -> Result<Option<String>, String> {
    let _lock = KEY_ACCESS
        .lock()
        .map_err(|_| "Cannot access the credential store.")?;
    match credential()?.get_password() {
        Ok(key) => Ok(Some(key)),
        Err(keyring::Error::NoEntry) => Ok(None),
        Err(_) => Err("The saved key could not be read from the operating system.".into()),
    }
}

fn select_key(
    saved: Result<Option<String>, String>,
    environment: impl FnOnce() -> Option<String>,
) -> Result<Option<ResolvedKey>, String> {
    let issue = match saved {
        Ok(Some(value)) if !value.trim().is_empty() => {
            if let Some(key) = normalize_key(&value) {
                return Ok(Some(ResolvedKey {
                    source: KeySource::Keychain,
                    value: key.to_owned(),
                }));
            }
            Some("The key in the operating system credential store has an invalid format.".into())
        }
        Ok(_) => None,
        Err(error) => Some(error),
    };
    match environment().filter(|value| !value.trim().is_empty()) {
        Some(value) => normalize_key(&value)
            .map(|key| {
                Some(ResolvedKey {
                    source: KeySource::Environment,
                    value: key.to_owned(),
                })
            })
            .ok_or_else(|| format!("{KEY_ENV} has an invalid format.")),
        None => issue.map_or(Ok(None), Err),
    }
}

fn resolve_key() -> Result<Option<ResolvedKey>, String> {
    select_key(read_keychain(), || {
        std::env::var_os(KEY_ENV).map(|value| value.to_string_lossy().into_owned())
    })
}

pub fn read_key() -> Result<Option<String>, String> {
    resolve_key().map(|selected| selected.map(|key| key.value))
}

fn key_status(selected: Option<ResolvedKey>) -> KeyStatus {
    let source = selected.map(|key| key.source);
    KeyStatus {
        configured: source.is_some(),
        source,
    }
}

#[tauri::command]
pub async fn acoustid_status() -> Result<KeyStatus, String> {
    tauri::async_runtime::spawn_blocking(|| resolve_key().map(key_status))
        .await
        .map_err(|_| "Could not check the key configuration.".to_string())?
}

#[tauri::command]
pub async fn acoustid_save(key: String) -> Result<KeyStatus, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let key = normalize_key(&key).ok_or("Enter a valid AcoustID application key.")?;
        let _lock = KEY_ACCESS
            .lock()
            .map_err(|_| "Cannot access the credential store.")?;
        credential()?
            .set_password(key)
            .map_err(|_| "The operating system could not save the key.")?;
        Ok(KeyStatus {
            configured: true,
            source: Some(KeySource::Keychain),
        })
    })
    .await
    .map_err(|_| "Could not save the key.".to_string())?
}

#[tauri::command]
pub async fn acoustid_delete() -> Result<KeyStatus, String> {
    tauri::async_runtime::spawn_blocking(|| {
        {
            let _lock = KEY_ACCESS
                .lock()
                .map_err(|_| "Cannot access the credential store.")?;
            match credential()?.delete_credential() {
                Ok(()) | Err(keyring::Error::NoEntry) => (),
                Err(_) => return Err("The operating system could not remove the saved key.".into()),
            }
        }
        resolve_key().map(key_status)
    })
    .await
    .map_err(|_| "Could not remove the key.".to_string())?
}

#[tauri::command]
pub async fn acoustid_test() -> Result<KeyTest, String> {
    tauri::async_runtime::spawn_blocking(|| {
        let key = read_key()?
            .ok_or("Set an AcoustID application key in Settings or ACOUSTID_API_KEY first.")?;
        Ok(match request(&client()?, &key, KEY_PROBE, 8) {
            Ok(_) => KeyTest {
                ok: true,
                message: "AcoustID accepted the key. No music from your library was sent.".into(),
            },
            Err(message) => KeyTest { ok: false, message },
        })
    })
    .await
    .map_err(|_| "Key test stopped unexpectedly.".to_string())?
}

pub fn client() -> Result<Client, String> {
    Client::builder()
        .https_only(true)
        .redirect(reqwest::redirect::Policy::none())
        .timeout(Duration::from_secs(8))
        .connect_timeout(Duration::from_secs(4))
        .user_agent(concat!(
            "MusicSorter/",
            env!("CARGO_PKG_VERSION"),
            " (https://nyphon.de/music-sorter/)"
        ))
        .build()
        .map_err(|_| "Cannot initialise the AcoustID connection.".into())
}

fn request(client: &Client, key: &str, fingerprint: &str, seconds: u32) -> Result<Value, String> {
    let mut last = LAST_REQUEST
        .lock()
        .map_err(|_| "The AcoustID service is busy.")?;
    if let Some(previous) = *last {
        std::thread::sleep(Duration::from_millis(400).saturating_sub(previous.elapsed()));
    }
    *last = Some(Instant::now());
    let response = client
        .post(LOOKUP)
        .form(&[
            ("client", key),
            ("duration", &seconds.to_string()),
            ("fingerprint", fingerprint),
            ("format", "json"),
            ("meta", "recordings"),
        ])
        .send()
        .map_err(|_| "AcoustID could not be reached. Try again later.".to_string())?;
    let status = response.status();
    if status.as_u16() == 429 {
        return Err("AcoustID rate limit reached. Try later.".into());
    }
    let response: Value = serde_json::from_reader(std::io::Read::take(response, 256 * 1024))
        .map_err(|_| "AcoustID did not return a readable response.")?;
    if status.is_success() && response["status"] == "ok" {
        Ok(response)
    } else if response["error"]["code"].as_i64() == Some(4) || status.as_u16() == 401 {
        Err("AcoustID rejected the application key.".into())
    } else {
        Err("AcoustID did not accept the request.".into())
    }
}

fn normalized(value: &str) -> String {
    value
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
        .to_lowercase()
}

fn acoustid_match(response: &Value, artist: &str, title: &str) -> Option<Match> {
    let mut match_value: Option<Match> = None;
    for hit in response["results"].as_array()? {
        if hit["score"].as_f64().unwrap_or_default() < 0.95 {
            continue;
        }
        for item in hit["recordings"].as_array().into_iter().flatten() {
            let title_candidate = item["title"].as_str().unwrap_or_default().trim();
            let artist_candidate = item["artists"]
                .as_array()
                .into_iter()
                .flatten()
                .filter_map(|a| a["name"].as_str())
                .collect::<Vec<_>>()
                .join(", ");
            if title_candidate.is_empty() || artist_candidate.is_empty() {
                continue;
            }
            if !artist.is_empty() && normalized(artist) != normalized(&artist_candidate) {
                continue;
            }
            if !title.is_empty() && normalized(title) != normalized(title_candidate) {
                continue;
            }
            if let Some(found) = &match_value {
                if normalized(&found.title) != normalized(title_candidate)
                    || normalized(&found.artist) != normalized(&artist_candidate)
                {
                    return None;
                }
            } else {
                match_value = Some(Match {
                    title: title_candidate.to_string(),
                    artist: artist_candidate,
                });
            }
        }
    }
    match_value
}

pub fn enrich(client: &Client, key: &str, track: &mut Track) -> Result<(), String> {
    if !track.artist.is_empty() && !track.title.is_empty() {
        return Ok(());
    }
    let (Some(fingerprint), Some(seconds)) = (track.fingerprint.as_deref(), track.duration_seconds)
    else {
        return Ok(());
    };
    let response = request(client, key, fingerprint, seconds)?;
    if let Some(candidate) = acoustid_match(&response, &track.artist, &track.title) {
        if track.artist.is_empty() {
            track.artist = candidate.artist;
        }
        if track.title.is_empty() {
            track.title = candidate.title;
        }
        track.metadata_source = Some("acoustid".into());
    }
    Ok(())
}
