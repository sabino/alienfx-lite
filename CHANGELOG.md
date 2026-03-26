# Changelog

## 0.2.2 - 2026-03-26

- Added in-app installer handoff, so `Check for updates` can download the latest setup and launch the upgrade flow instead of only opening GitHub.
- Added a weekly GitHub Actions release workflow that only tags and publishes a new build when `main` has changed since the latest release tag.
- Explicitly ignore local `artifacts/camera-probe` helper captures so BRIO debugging files stay out of git history.

## 0.2.1 - 2026-03-26

- Added per-zone effect selectors and live per-zone color state in the desktop deck, with clearer pending-save feedback and undo-to-saved behavior.
- Moved version/update status into `Live State`, tightened the right rail layout, shortened the bottom save bar, and fixed the combo-box popup readability.
- Switched lighting edits to instant live preview with explicit `Save Lighting` persistence, plus unsaved-change prompts when exiting.
- Fixed API v4 `Breathing` handling and implemented chunked multi-phase `Spectrum` and `Rainbow` writes so the Dell G-series 4-zone keyboard no longer advertises dead effects.
- Added a direct hardware debug probe path for validating HID-backed lighting behavior outside the broker.

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
