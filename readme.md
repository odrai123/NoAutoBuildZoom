# Build Mode No AutoZoom

A quality-of-life camera mod for **Dyson Sphere Program**.

This mod disables the automatic camera zoom / pitch / distance changes that occur when entering or exiting build mode, while keeping full manual camera control intact.

It also permanently increases the maximum zoom-out distance slightly, matching the comfortable zoom range normally available in build mode — without causing any camera jumps or snapping.

---

## Features

- 🚫 **No automatic camera zoom or pitch changes** when entering or exiting build mode
- 🎮 **Manual camera controls remain fully functional**
- 🔄 No freezing, snapping, delays, or post-build camera corrections
- 🔭 **Slightly increased maximum zoom-out distance** (permanent, configurable)
- ⚡ Extremely lightweight — no per-frame locking or heavy patches

---

## How It Works (High Level)

- Prevents the camera system from switching to the special *build-mode camera pose*
- Allows the camera blender to run normally using the non-build pose
- Applies a **one-time** increase to the camera’s maximum zoom distance without altering the current zoom level

---

## Configuration

The config file is generated automatically on first launch.

```ini
[General]
## Enable the mod
Enabled = true

## Permanent extra maximum zoom-out distance (meters)
## Set to 0 to disable zoom extension
ExtraMaxZoomOut = 2
