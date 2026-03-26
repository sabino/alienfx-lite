# Changelog

## 0.2.0 - 2026-03-22

- Added capability-driven effect lists per mapped lighting surface across the HID-backed device families.
- Added extra upstream hardware effects in the managed stack: `Breathing`, `Spectrum`, and `Rainbow`.
- Added API v5 whole-surface animation support through the native global-effect bridge for `Pulse`, `Morph`, `Breathing`, and `Rainbow`.
- Hardened persisted profile resolution so stored lighting states survive native device-id prefix changes after re-enumeration.
- Kept thin framework-dependent packaging, with the generated setup still staying small enough for GitHub-release distribution.

## 0.1.0 - 2026-03-21

- Added the unified `AlienFxLite.exe` desktop host with tray/startup behavior.
- Added LocalSystem broker hosting for fan and lighting control.
- Added manual GitHub-release update checks in the desktop UI.
- Added Inno Setup packaging and automated release build scripts.
