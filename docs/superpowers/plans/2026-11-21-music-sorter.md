# Music Sorter Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans.

**Goal:** Build a testable preview-and-copy desktop app with verifiable duplicate handling.
**Architecture:** Separate scan/plan/export, metadata adapters and Avalonia UI. Application commits export independently from the read-only scan.
**Tech Stack:** .NET 11, Avalonia, TagLib#, SQLite.
**Spec:** docs/superpowers/specs/2026-11-21-music-sorter-design.md

## Tasks
- [ ] Implement bounded scan, current-file SHA-256 verification and duplicate/variant planning; tests cover identical content under different names, same artist/title with different content, blank tags, and colliding names.
- [ ] Implement exporter: source recheck, staging, metadata patch against staged copy only, no overwrite, rollback the partial temporary file on failure, reject nested source/output and linked destinations.
- [ ] Implement TagLib# local metadata, persisted local index, throttled MusicBrainz suggestions (review before application), no external transfer during ordinary scans.
- [ ] Add native folder pickers, token-based scheme preview, duplicate/variant indicators, live scan and export status, and cancellation.
- [ ] Build and run synthetic Mac tests, attempt win-x64 publish, document actual verification limits.
