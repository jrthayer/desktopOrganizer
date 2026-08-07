# Desktop Tool

A Windows desktop app, run from the system tray, built to grow into a
general desktop-organization toolkit over time. Its features are
**Fences** and **Layouts**, built on a shared **Snapping** engine — see
below.

## Requirements

- .NET 8 SDK
- Windows 10/11

## Build & run

```
dotnet build DesktopTool.sln
dotnet run --project src/DesktopTool/DesktopTool.csproj
```

The app runs as a system tray icon only (no main window). Right-click the
tray icon for the menu.

## Features

### Fences

Draggable, resizable, translucent "fence" regions (Stardock Fences-style)
that group your desktop icons under a name you choose, with drag-and-drop
reordering/moving between fences, a synthetic Recycle Bin item, and
snap-to-fence/snap-to-guide-line dragging.

See [`src/DesktopTool/Features/Fences/README.md`](src/DesktopTool/Features/Fences/README.md)
for the full feature writeup, settings reference, and known limitations.
All of its code lives under [`src/DesktopTool/Features/Fences`](src/DesktopTool/Features/Fences).

### Layouts

Named profiles that relaunch (or reuse, if already running) a set of
programs and place each one's window on a chosen monitor and position.
**Save Current Layout** builds a profile straight from whatever's
currently open and where it's sitting, instead of picking each program by
hand through **Manage Layouts...**. Both live on the **Layout Launcher**
widget (tray menu > Widgets > Layout Launcher), which also lists every
saved layout for one-click run.

See [`src/DesktopTool/Features/Layouts/README.md`](src/DesktopTool/Features/Layouts/README.md)
for the full feature writeup, the layout entry model, and known limitations.
All of its code lives under [`src/DesktopTool/Features/Layouts`](src/DesktopTool/Features/Layouts).

**Limitations:** Save Current Layout can end up including one of Desktop
Tool's own windows (a fence) as a captured entry - it's excluded by
matching the window's owning process ID against the running app's own, but
that hasn't reliably kept it out in every case seen so far. Harmless to
leave in a saved layout (running it just tries to "relaunch" Desktop Tool
itself, which is already running and a no-op in practice), but worth
deleting by hand via Manage Layouts if you notice it.

### Snapping

The pure edge-snapping geometry (`SnapEngine`) that gives both Fences and
the Layout Launcher widget their drag-to-snap feel — a shared, stateless
utility rather than a user-facing feature of its own.

See [`src/DesktopTool/Features/Snapping/README.md`](src/DesktopTool/Features/Snapping/README.md)
for what it does and how the two features above use it. All of its code
lives under [`src/DesktopTool/Features/Snapping`](src/DesktopTool/Features/Snapping).

## Tray menu

- **New Fence** — creates a new, empty fence.
- **Show/Hide All** — toggles every fence's visibility at once; also
  triggered by double-clicking the tray icon.
- **Start with Windows** — adds (or removes) Desktop Tool from your user's
  Run key so it launches automatically at sign-in. The checkbox always
  reflects the Run key's actual current state, even if changed by hand.
- **Show Hidden Files** — toggles Windows' own "Show hidden files, folders,
  and drives" Explorer setting (the same one under Folder Options), exposed
  here for convenience since fenced items live in a hidden `hiddenDesktop`
  folder (see [Fences: Desktop icon hiding](src/DesktopTool/Features/Fences/README.md#desktop-icon-hiding)).
  This is a system-wide setting, not something scoped to Desktop Tool -
  turning it on reveals every hidden file on your machine, not just fenced
  ones, and the checkbox always reflects its actual current state even if
  changed from Explorer's own Folder Options instead.
- **Manage Snap Lines...** — opens the snap-line editor (see
  [Fences: Snap lines](src/DesktopTool/Features/Fences/README.md#snap-lines)).
- **Add Recycle Bin** — adds the synthetic Recycle Bin fence item (see
  [Fences: Recycle Bin](src/DesktopTool/Features/Fences/README.md#recycle-bin)) to a
  new, dedicated fence. Hidden once one already exists anywhere, since only
  one is allowed.
- **Widgets > Layout Launcher** — toggles the Layout Launcher widget, a
  persistent on-screen panel that lists every saved layout (click to run
  one) plus **Save Current Layout** and **Manage Layouts...** (see
  [Layouts](#layouts) above).

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
