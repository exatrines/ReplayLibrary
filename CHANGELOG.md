# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [0.1.5] - 2026-11-23

### Fixed

- Expansion icons load when Replay Library is installed from the plugin repository. The release archive includes `Assets/patch.png`.

## [0.1.4] - 2026-11-18

### Changed

- For one recording, the list keeps the current patch. When an anonymized replay is shown, the original stays on disk and leaves the list. That original is not locked. A source replay is also left unlocked when its current-patch conversion is in the library. Reconvert still finds the original after it moves to Deleted.
- Locks are current-patch clears, then older-patch clears, then uncleared replays, newest first.
- The refresh button files loose replays into the library and reloads the list. A replay that was not already there starts its delete timer that day. The date shown stays the recording date.
- Unlocked replays move to Deleted that many days after they entered the library. Replays already there keep a saved timer, or the recording date when none was saved. Converting does not restart the timer. The first launch copies those timers into `library.json` and removes the json file next to each replay.
- A finished recording that matches a replay already in the library drops the slot file and keeps the library copy.
- The playback bar stays hidden during cutscenes and subtitles, and until the first load finishes or the position stays still for 3 seconds. Settings start closed. Unstuck appears between the waymark button and the pin while playback is stalled.
- Clicking the seek bar fast-forwards to that time and continues until it arrives. Dragging the handle does not seek. The speed slider runs from 0.05x to 10x, and the speed buttons stay on the bar.

### Fixed

- Unloading Replay Library during playback pauses the replay and reports that playback will break until the plugin or the replay is loaded again.

## [0.1.3] - 2026-11-13

### Changed

- Replay Library lists the replays in a duty with locked clears first, then locked replays that are not cleared, then other clears, then the rest. Within each of those, newer recordings come first. Duties stay ordered by their newest replay.
- Replay Library and Replay Converter start the delete timer when a replay is filed into the library. Replays already there keep the timer they have. A replay left outside the library uses the time Windows recorded for that file, so an old replay copied in is not removed on the next check. The date shown on the replay stays the recording date.
- Replay Library checks the delete deadline and the Deleted folder size only while its window is open and not collapsed. Finishing a recording, or changing the delete settings, still runs that check right away.
- Replay Converter converts only 7.57 replays. Other builds are left unchanged, and Convert stays off. Replay Library uses the same rule. A 7.58 replay converted from 7.57 can still be converted again.

## [0.1.2] - 2026-11-12

### Changed

- Replay Library treats patch 7.58 as the current patch.
- Replay Library locks unconverted replays only, up to the count set for that content. Order is current-patch clears, then older-patch clears, then uncleared replays, newest first. Locks are chosen again from the files present. A locked replay can still be deleted.
- Replay Library moves only the selected file to Deleted. Files in Deleted are removed when the folder passes the size limit, starting with the ones moved there first.
- Converting a replay restarts the delete timer for the original and the converted file. The date shown on the replay stays the recording date.
- Replay Library and Replay Converter use the same list. A group with a file in progress reads `Waiting - (2)`. Rows and the card show the clear and anonymize marks.
- Replay Converter's card names the patch a file was converted from.

### Fixed

- Replay Library no longer stalls the game while scanning the library.
- Playing a library replay again loads the file when that file has been replaced.

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
