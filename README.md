# Music Sorter
Music-library sorting for Windows, with a native macOS development build. The app uses .NET 11 and Avalonia and does not require a server.

## Workflow
1. Create or open a named profile. Setup uses two steps: choose one or more input folders and one separate output folder, then choose the path pattern and save. For example: `{artistInitial}/{artist}/{year}/{title}`.
2. Open the profile to see its local index. While the profile is open, file changes refresh the index, hashes, local tags and available fingerprints in the background.
3. Use **Start sorting** to copy the current indexed tracks. Review uncertain matches separately if useful; skipping review does not discard variants. An exact duplicate is not copied a second time.

Existing destination files are never overwritten and original music is not deleted or retagged. A reviewed tag suggestion changes only the exported copy. The optional online review is manual; the live index scans locally only.

| What matches | Result |
| --- | --- |
| Original-file SHA-256 | One copy in this export; other byte-identical entries are skipped. |
| Normalized artist and title, different SHA-256 | Keep distinct files with unique paths, and flag the group as variants. |
| Audio fingerprint, different SHA-256 | A possible audio match, never permission to discard a file. |

A local fingerprint is computed by the separate Chromaprint `fpcalc` executable and may be stored with the index. Set `MUSIC_SORTER_FPCALC` to its absolute path when it is not available on PATH. Use the official [Chromaprint releases](https://github.com/acoustid/chromaprint/releases) for the target CPU and OS. Local fingerprint calculation does not contact an online service. Online catalog searches are opt-in and never upload audio; [MusicBrainz](https://musicbrainz.org/doc/MusicBrainz_API) uses search metadata, and [AcoustID](https://acoustid.org/webservice) uses a fingerprint and duration. No provider match is proof that two files are interchangeable. AcoustID's free service is noncommercial only.

## Build and test

```sh
dotnet restore MusicSorter.slnx
dotnet build MusicSorter.slnx --no-restore
dotnet run --project tests/MusicSorter.Smoke --no-build
dotnet run --project src/MusicSorter.Desktop --no-build
```

On the development Mac, `./scripts/run-mac.sh` uses the locally isolated SDK and fingerprint helper when present. Add `-- --fingerprint` to the smoke test command to also exercise the local helper. The tests generate synthetic audio in a temporary directory; they never scan a real music library.

For release packaging on Windows:

```powershell
dotnet publish src/MusicSorter.Desktop -c Release -r win-x64 --self-contained true
```
