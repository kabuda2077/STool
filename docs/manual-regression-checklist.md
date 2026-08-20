# STool Manual Regression Checklist

Run this checklist on Windows 10/11 after a release build. Record the OS version, display count, DPI scale, and whether each item passes.

## Startup and hotkeys

- [ ] Start STool with no existing process; verify it stays in the tray and does not open a main window.
- [ ] Press the five configured hotkeys and verify Screenshot, Translation, Clipboard, Settings, and LAN Transfer open exactly one window each.
- [ ] Launch STool a second time; verify the existing process is activated and Settings opens.
- [ ] Enable Hide tray icon; verify every hotkey still works and the second launch still activates Settings.
- [ ] Configure the same hotkey for two features; verify the setting is rejected and the original values remain active.
- [ ] Configure a hotkey already used by another application; verify the affected feature is reported and other features remain active.
- [ ] Focus each hotkey input, record Ctrl/Alt/Shift/Win combinations, F1-F24, arrows, navigation keys, Escape, and Tab.

## Screenshot and OCR

- [ ] Capture a region on one monitor; verify selection, move, resize, annotation, undo, redo, copy, save, and cancel.
- [ ] Capture across multiple monitors with mixed DPI; verify the frozen image, selection coordinates, toolbar placement, and saved crop align.
- [ ] Confirm a screenshot while another process temporarily occupies the clipboard; verify retry or an explicit error and no silent close.
- [ ] Run local OCR and each configured cloud/AI OCR provider; verify result window, provider label, cancellation, and fallback behavior.
- [ ] Run screenshot translation in Fast and Smart modes; verify the button tooltip names the mode and clicking during a request cancels it.
- [ ] Use a small selection, long paragraph, image background, and multi-line text; verify translation blocks remain inside the selection and do not overlap controls.

## Translation

- [ ] Translate text with Google, Tencent, and AI providers.
- [ ] Test empty input, invalid credentials, invalid endpoint, timeout, cancellation, rate limiting, and an empty provider response.
- [ ] Change input while a request is running; verify the old result cannot overwrite the new input.
- [ ] Verify copy, copy-and-hide, and copy-and-input, including when the original target window has closed.
- [ ] Verify AI model discovery, custom endpoint normalization, and test request feedback.

## Clipboard

- [ ] Copy text, an image, and multiple files from separate applications; verify source app, deduplication, search, favorite, delete, and restore.
- [ ] Keep the Clipboard panel open: single-click copies and keeps the panel; double-click copies, closes, and pastes to the original window.
- [ ] Hold the clipboard from another process during restore; verify asynchronous retry and no UI freeze.
- [ ] Close the original target window before double-click paste; verify the content remains copied and a manual-paste message appears.
- [ ] Switch tabs and search rapidly through a large image history; verify no stale thumbnails appear and memory returns after closing.
- [ ] Clear all, a category, and favorites; verify favorite retention and source image/thumbnail cleanup.

## Settings and LAN transfer

- [ ] Change OCR and translation providers, credentials, endpoints, models, fallback policy, and screenshot mode; verify debounced auto-save.
- [ ] Restart STool and verify all settings persist; corrupt the primary config in a test copy and verify backup recovery/default fallback.
- [ ] Open LAN Transfer without permission; verify the permission state and repair flow.
- [ ] Open and close LAN Transfer repeatedly; verify only one listener exists and no port remains occupied after closing.
- [ ] Change receive directory, repair firewall access, rotate the code, revoke devices, and reconnect the phone.
- [ ] Transfer single files, multiple files, folders, and large files; verify confirmation, QR/manual code auth, progress, pause, resume, cancel, retry, range resume, archive cleanup, and history.
- [ ] Close the window during an active transfer; verify confirmation, bounded cleanup, disconnected phone, and removed temporary files.
