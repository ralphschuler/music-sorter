# Profile Wizard Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans.

**Spec:** docs/superpowers/specs/2026-11-21-profile-wizard-design.md

- [ ] Implement multiple source validation and one global deduplication plan, source-integrity checks and per-item collision results; test using independent synthetic source roots.
- [ ] Persist profiles atomically and run an indexed folder session with cached rows, cancellable local refresh, debounce, and optional local fingerprints by original SHA-256. Tests must not touch the user's library, saved settings or online services.
- [ ] Replace the one-screen dashboard with saved profiles, a setup wizard, live index, separate review, and dedicated sort progress; sparse labels and no decorative status clutter.
- [ ] Run .NET build, synthetic multi-source+watcher tests, and Windows package verification; distinguish build/automated tests from real desktop operation.
