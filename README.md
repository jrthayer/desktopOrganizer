# Fence Tool

A Windows desktop-icon organizer (Stardock Fences-style): draggable, resizable,
translucent "fence" regions that group desktop icons.

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

A fence is a draggable, resizable, translucent region that groups desktop
shortcuts under a name you choose. It owns a plain list of file paths
(`FenceModel.Files`) and draws its own icon+label grid for them rather than
moving the real desktop icons around. Dropping a file onto a fence adds a
reference to it; dragging an item within a fence reorders it; dragging an
item onto a *different* fence's window moves it there; dragging an item off
any fence entirely removes it from that fence (see Desktop icon hiding
below for what happens to its real desktop icon when that happens).

**Limitations:** a fence only remembers a file's path, not a live watch on
it - if the underlying file is later moved or deleted outside Fence Tool,
its entry stays in the fence but its icon/label may go stale (see
`FenceForm.GetIcon`'s fallback handling) until removed by hand.

### Desktop icon hiding

When a shortcut is added to a fence, Fence Tool hides its real desktop icon
so it isn't visible twice — once as the fence's own drawing of it, and once
underneath on the actual desktop. There's no supported "hidden" state for
an individual item on the desktop's icon list, so this works by moving the
real icon far off-screen, remembering its original position (persisted to
disk, so it survives an app restart or crash) so it can be moved back once
the shortcut isn't in any fence anymore.

**Limitations:** only applies to files that live directly in your (or the
Public) Desktop folder - anything dragged in from elsewhere never had a
real desktop icon to hide. Matching a fenced path to its desktop icon is
done by display label (filename, since that's all the desktop's icon list
exposes), so two different real desktop files that happen to share a
display name (e.g. `Notes.txt` and `Notes.docx` with extensions shown)
can't be told apart. If Fence Tool is closed uncleanly (crash, Task Manager
kill) rather than via the tray's Exit, hidden icons stay hidden until it's
run again.

### Fence settings

Click a fence to activate it, then click the cog that appears near the top
of its title bar to open its settings menu.

- **Hide Shortcut Names** — hides the label under each icon, showing icons
  only. Toggle it again to bring labels back.
- **Hide Title** — hides the fence's title bar entirely, reclaiming that
  space for the icon grid. The fence can still be moved via its outer
  margin.
- **OCD Fence Sizing** — after you resize the fence by hand, automatically
  snaps it to the tightest size that fits its icons (equivalent to running
  OCD Formatting → Both after every manual resize).
- **OCD Formatting** — a submenu with three one-off resize actions: **Both**
  (trims width and height), **Left/Right** (width only), and **Top/Down**
  (height only). Each shrinks or grows the fence to fit its current icons
  without changing its top-left corner.
- **Fence Color** — a submenu to tint the fence's body and title bar.
  Choose one of the eight preset swatches, pick **Custom...** to open the
  full Windows color picker, or pick **Default** to reset to the plain dark
  gray. The picked color is blended into the existing dark theme rather
  than replacing it outright, so the fence stays readable no matter how
  bright the chosen color is.
- **Delete Fence** — deletes the fence (with a confirmation prompt). Its
  shortcuts aren't deleted; their real desktop icons are restored (see
  Desktop icon hiding above).

## Status

Early scaffold — see the implementation plan for the staged build-out
(tray shell, fence UI, desktop icon discovery/repositioning, auto-arrange
handling, z-order integration, DPI/multi-monitor support, resilience).

## Credits

Fence contents (each fence owning its own list of file references and
rendering its own icon grid, rather than moving the real desktop icons
around) follows the approach used by
[NoFences](https://github.com/Twometer/NoFences), an open-source Stardock
Fences alternative. No code from that project is reused directly here, but
its design is what this app's drag-and-drop model is based on.
