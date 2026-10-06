# STool Manual Regression Checklist

Run this checklist on Windows 10/11 after a release build. Record the OS version, display count, DPI scale, and whether each item passes.

## Startup and hotkeys

- [ ] Start STool with no existing process; verify it stays in the tray and does not open a main window.
- [ ] Unzip STool under `C:\Program Files\` (or another read-only folder) and start it; verify a dialog explains that the Data folder is not writable and the process exits instead of staying in the background.
- [ ] Press the five configured hotkeys and verify Screenshot, Translation, Clipboard, Settings, and LAN Transfer open exactly one window each.
- [ ] Right-click the tray icon; verify every feature, including LAN Transfer and "pause/resume clipboard recording", is available.
- [ ] Launch STool a second time; verify the existing process is activated and Settings opens.
- [ ] Enable Hide tray icon; verify every hotkey still works and the second launch still activates Settings.
- [ ] Configure the same hotkey for two features; verify the setting is rejected and the original values remain active.
- [ ] Change a hotkey successfully; verify the input shows the new normalized value (not the previous one).
- [ ] Configure a hotkey already used by another application; verify the affected feature is reported and other features remain active.
- [ ] Focus each hotkey input, record Ctrl/Alt/Shift/Win combinations, F1-F24, arrows, navigation keys, Escape, and Tab.
- [ ] Enable auto-start, move the portable folder, open General settings; verify the auto-start entry is updated to the new path.

## Screenshot and OCR

- [ ] Capture a region on one monitor; verify selection, move, resize, annotation, undo, redo, copy, save, and cancel.
- [ ] Before confirming a selection, verify the magnifier follows the cursor, shows coordinates and color, arrow keys move the cursor by one pixel (Shift: ten), and `C` copies the color value.
- [ ] After confirming, verify arrow keys move the selection, Ctrl+arrow resizes it, and Ctrl+S opens the save dialog.
- [ ] Use each annotation tool with every color and size; add text (Enter for a new line, Ctrl+Enter or Esc to finish, empty text is discarded); verify the exported image matches the screen.
- [ ] Draw mosaic at 100%, 150% and 200% scaling; verify the exported mosaic blocks match the preview, and moving the selection afterwards updates the mosaic content.
- [ ] Capture across multiple monitors with mixed DPI; verify the frozen image, selection coordinates, toolbar placement, and saved crop align.
- [ ] Confirm a screenshot while another process temporarily occupies the clipboard; verify retry or an explicit error and no silent close.
- [ ] Run local OCR and each configured cloud/AI OCR provider; verify result window, provider label, cancellation, and fallback behavior.
- [ ] Run screenshot translation in Fast and Smart modes; verify the button tooltip names the mode and clicking during a request cancels it.
- [ ] Use a small selection, long paragraph, image background, and multi-line text; verify translation blocks remain inside the selection, text in the exported image is sharp, and annotations stay above the translation.

## Translation

- [ ] Translate text with Google, Tencent, and AI providers.
- [ ] Translate a long text (several thousand Chinese characters) with Google; verify it completes instead of failing on URL length.
- [ ] Use an OpenAI reasoning model (for example an o-series model) for AI translation and AI OCR; verify requests succeed without manual parameter changes.
- [ ] Translate a text long enough to hit the model output limit; verify an explicit "incomplete result" error instead of a silently truncated translation.
- [ ] Test empty input, invalid credentials, invalid endpoint, timeout, cancellation, rate limiting, and an empty provider response.
- [ ] Change input while a request is running; verify the old result cannot overwrite the new input.
- [ ] Verify copy, copy-and-hide, and copy-and-input, including when the original target window has closed.
- [ ] Verify AI model discovery, custom endpoint normalization, and test request feedback for both translation and OCR settings.

## Clipboard

- [ ] Copy text, an image, and multiple files from separate applications; verify source app, search, favorite, delete, and restore.
- [ ] Copy A, then B, then A again; verify A moves to the top instead of appearing twice.
- [ ] Copy a password from KeePass/KeePassXC/1Password/Bitwarden; verify it is not recorded. Add an app to the exclusion list and verify its copies are skipped.
- [ ] Pause recording from the tray menu or clipboard settings; verify nothing is recorded until resumed.
- [ ] Keep the Clipboard panel open: type to search immediately, use ↑/↓ to select, Enter to paste into the original window, Ctrl+Enter to copy only, Ctrl+1..5 to switch tabs, Esc to close.
- [ ] Single-click copies and keeps the panel; double-click copies, closes, and pastes to the original window.
- [ ] With more than 200 records, verify favorites and search results include older records and scrolling loads more pages.
- [ ] Hold the clipboard from another process during restore; verify asynchronous retry and no UI freeze.
- [ ] Close the original target window before double-click paste; verify the content remains copied and a manual-paste message appears.
- [ ] Take a large screenshot (4K or multi-monitor) and copy it; verify no UI stutter and that images above the size limit are not written to disk.
- [ ] Switch tabs and search rapidly through a large image history; verify no stale thumbnails appear and memory returns after closing.
- [ ] Change retention, entry limit, and image size limit in Clipboard settings; verify old records are cleaned without restarting.
- [ ] Clear all and a category; verify favorite retention and source image/thumbnail cleanup.
- [ ] Type a new search and immediately press Enter; verify the pasted item belongs to the new query, not the previous list.
- [ ] Copy new content while a page is loading; verify it is not lost when the page finishes loading.
- [ ] In a disposable copy of Data, corrupt clipboard.db and start STool; verify other features still work, the original database and images remain intact, and the clipboard error is visible.
- [ ] Switch away from the original target during the automatic-paste delay; verify STool does not paste into the newly focused application.

## Settings and LAN transfer

- [ ] Change OCR and translation providers, credentials, endpoints, models, fallback policy, and screenshot mode; verify debounced auto-save.
- [ ] Replace `Data\secure.key` in a test copy and open settings; verify a warning appears and saved keys are kept until re-entered.
- [ ] Restart STool and verify all settings persist; corrupt the primary config in a test copy and verify backup recovery/default fallback.
- [ ] Enable diagnostic logging; verify memory checkpoints and capture timing appear in the log only while enabled.
- [ ] Open LAN Transfer without permission; verify the permission state and repair flow.
- [ ] Open and close LAN Transfer repeatedly; verify only one listener exists and no port remains occupied after closing.
- [ ] Pair a phone by QR code and by manual code; verify both codes change after pairing and the window shows the new ones.
- [ ] Pair with "remember this device", close and reopen LAN Transfer; verify the phone reconnects without re-entering the code.
- [ ] Enter wrong codes repeatedly from several phones; verify pairing is locked for a few minutes and the code changes.
- [ ] Open the service via a hostname that resolves to the PC (not its IP); verify requests are rejected.
- [ ] Change receive directory, repair firewall access, rotate the code, revoke devices, and reconnect the phone.
- [ ] Send a file larger than the free space on the receive drive; verify the phone shows a clear disk-space error.
- [ ] Transfer single files, multiple files, folders, and large files; verify confirmation, progress, pause, resume, cancel, retry, range resume, archive cleanup, and history.
- [ ] Close the window during an active transfer; verify confirmation, bounded cleanup, disconnected phone, and removed temporary files.
- [ ] Pair two clients and try to access one client's upload with the other's session; verify initialization, HEAD, PATCH and DELETE are refused without changing the owner's task.
- [ ] Start a screenshot translation, cancel it, and immediately start another; verify callbacks from the old request cannot hide the new loading indicator or result.
- [ ] Close a capture during OCR; verify no result window appears afterwards.
- [ ] Perform several unrelated settings saves with an unreadable secret; verify the warning state and original ciphertext remain unchanged.

## Build verification

- [ ] Restore and build `STool.sln` in Debug and Release; record the actual warning and error counts.
- [ ] Run `dotnet test Tests/STool.Tests.csproj` and retain the result; listener startup errors must not be counted as passing integration tests.
- [ ] Publish with an explicit version and check both the executable metadata and archive filename.
- [ ] Compare ordinary and `-ReadyToRun` packages before claiming startup or memory improvements.
