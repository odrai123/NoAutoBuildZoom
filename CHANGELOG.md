# Changelog

## 1.0.6

- Fixed the main no-autozoom behaviour being disabled by an incorrect compatibility check.
- Prevented camera FOV, pitch, and distance changes when switching between build and dismantle modes.

## 1.0.5

- Added a compact startup compatibility check for the DSP camera and build-mode members used by the mod.
- Added graceful feature-level fallback when a required game member or Harmony patch is unavailable.
- Cached reflected camera fields instead of resolving them repeatedly during camera updates.
- Corrected Shift-click pin timing to use Unity's current frame count.
- Removed temporary diagnostic probes and per-frame diagnostic logging used during development.

## 1.0.3

- Improved performance.

## 1.0.2

- Fixed camera movement when entering build mode with Shift-click.

## 1.0.1

- Fixed keyboard camera panning being disabled during blueprint placement and paste mode.

## 1.0.0

- Initial release.
