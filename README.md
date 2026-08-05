# Fence Tool

A Windows desktop app, run from the system tray, built to grow into a
general desktop-organization toolkit over time. Its first (and currently
only) feature is **Fences** — see below.

## Requirements

- .NET 8 SDK
- Windows 10/11

## Build & run

```
dotnet build FenceTool.sln
dotnet run --project src/FenceTool/FenceTool.csproj
```

The app runs as a system tray icon only (no main window). Right-click the
tray icon for the menu.

## Features

### Fences

Draggable, resizable, translucent "fence" regions (Stardock Fences-style)
that group your desktop icons under a name you choose, with drag-and-drop
reordering/moving between fences, a synthetic Recycle Bin item, and
snap-to-fence/snap-to-guide-line dragging.

See [`src/FenceTool/Fences/README.md`](src/FenceTool/Fences/README.md) for
the full feature writeup, settings reference, and known limitations. All of
its code lives under [`src/FenceTool/Fences`](src/FenceTool/Fences).

## Tray menu

- **New Fence** — creates a new, empty fence.
- **Show/Hide All** — toggles every fence's visibility at once; also
  triggered by double-clicking the tray icon.
- **Start with Windows** — adds (or removes) Fence Tool from your user's
  Run key so it launches automatically at sign-in. The checkbox always
  reflects the Run key's actual current state, even if changed by hand.
- **Show Hidden Files** — toggles Windows' own "Show hidden files, folders,
  and drives" Explorer setting (the same one under Folder Options), exposed
  here for convenience since fenced items live in a hidden `hiddenDesktop`
  folder (see [Fences: Desktop icon hiding](src/FenceTool/Fences/README.md#desktop-icon-hiding)).
  This is a system-wide setting, not something scoped to Fence Tool -
  turning it on reveals every hidden file on your machine, not just fenced
  ones, and the checkbox always reflects its actual current state even if
  changed from Explorer's own Folder Options instead.
- **Manage Snap Lines...** — opens the snap-line editor (see
  [Fences: Snap lines](src/FenceTool/Fences/README.md#snap-lines)).
- **Add Recycle Bin** — adds the synthetic Recycle Bin fence item (see
  [Fences: Recycle Bin](src/FenceTool/Fences/README.md#recycle-bin)) to a
  new, dedicated fence. Hidden once one already exists anywhere, since only
  one is allowed.

**Limitations:** the Show Hidden Files setting itself takes effect
immediately, but the desktop's own icon view doesn't visibly pick it up
until manually refreshed (F5, or right-click > Refresh) - a real Refresh
forces Explorer to re-check which items currently match the filter, which
nothing else tried does. Several alternatives were tried and none worked:
the standard `SHChangeNotify` broadcast Explorer's own Folder Options
dialog sends (refreshes ordinary Explorer windows, not the desktop), the
same plus a forced repaint of the icon list, targeted `SHCNE_UPDATEDIR`
notifications at both real desktop folders, simulating an actual F5
keypress (requires genuine keyboard focus, which proved unreliable to
fake from another process), and posting `WM_COMMAND`/`FCIDM_SHVIEW_REFRESH`
directly (the same message the desktop's own right-click Refresh sends).
The setting is correct immediately either way - only the visual update is
delayed.

## Status

Early scaffold — see the implementation plan for the staged build-out
(tray shell, fence UI, desktop icon discovery/repositioning, auto-arrange
handling, z-order integration, DPI/multi-monitor support, resilience).
