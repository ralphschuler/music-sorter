# Music Sorter for Tauri

A local desktop app with an embedded browser UI and a Rust indexing, fingerprinting and sorting engine. This edition is separate from the older .NET app. Set up a new profile with one or more input folders and a separate output folder. Sorting is explicit. The app prepares and verifies up to four copies at a time and then publishes them in a deterministic order; it never moves or modifies input files or overwrites existing output. You can navigate to Profiles or Settings while the job runs and return to progress from the header.

Audio is decoded and fingerprinted locally without a system installation of Chromaprint or FFmpeg. A file hash proves byte equality; a fingerprint or shared title is only evidence for review, never permission to discard a variant. For optional online matching, enter your AcoustID **application key** in Settings, then enable online lookup for the profile. A saved key in macOS Keychain or Windows Credential Manager takes precedence. If none is present, the app uses `ACOUSTID_API_KEY` from its launch environment without storing it or reading a `.env` file. The connection test uses a short synthetic fingerprint, never your library. Online lookup sends a fingerprint and duration, not audio. AcoustID's public service is limited to noncommercial use: https://acoustid.org/webservice.

## Development
Use the platform prerequisites in the official [Tauri docs](https://v2.tauri.app/start/prerequisites/), then from this directory:

```sh
cargo test --locked --manifest-path engine/Cargo.toml
cargo test --locked --manifest-path src-tauri/Cargo.toml
npx --yes --package=@tauri-apps/cli@2.12.6 tauri dev
```

## Delivery

The private GitHub package workflow builds and verifies:

- **Windows x64:** a single NSIS setup EXE with the offline WebView2 installer included. The destination computer does not need a separate WebView2 download. The test build is not Authenticode-signed.
- **macOS:** separate Apple Silicon and Intel ZIPs, each containing one self-contained app using system WebKit. The builds are ad-hoc signed, not Developer ID-signed or notarized.
- **Linux x64:** an AppImage built on Ubuntu 22.04 for glibc compatibility. Make it executable before launching if your download tool did not preserve permission. If FUSE is unavailable, launch with `APPIMAGE_EXTRACT_AND_RUN=1`. Optional persistent keys require an available Secret Service; `ACOUSTID_API_KEY` still works without it.

No installer contains an AcoustID key. Scan, fingerprint and sort do not need an internet connection. Online identification only runs if enabled for the profile.

The package workflow uploads Actions artifacts. A release is prepared from one green workflow run and publishes those exact verified files and their SHA-256 checksums. Use a `tauri-v*` tag to avoid triggering the legacy .NET release.
