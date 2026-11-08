# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [0.1.1] - 2026-11-09

### Replay Converter

- Sign in by pasting the code from the Discord `/auth` command. A saved sign-in still lasts 7 days and renews automatically.

## [0.1.0] - 2026-11-08

### Replay Library

- Duty replays filed in `replay/replaylibrary`, filtered and ordered by content or by newest.
- Play a current-patch replay from an inn.
- Record checked content types into `replay/replaylibrary`.
- Keep a set number of replays per duty. Move the rest to `replay/deleted` after the chosen number of days, then trim that folder by size.
- Convert and anonymize from the library card when Replay Converter is loaded and signed in.
- English and Japanese, and window themes.
- Commands `/lib` and `/lib config`.
- The first launch keeps a full copy of the `replay` folder.

### Replay Converter

- Sign in, then convert a replay to the current patch or anonymize it.
- Opening the window files loose replays into `replay/replaylibrary`.
- Usable on its own, or from Replay Library.
