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

### Desktop icon hiding

When a shortcut is added to a fence, Fence Tool hides its real desktop icon
so it isn't visible twice — once as the fence's own drawing of it, and once
underneath on the actual desktop. There's no supported "hidden" state for
an individual item on the desktop's icon list, so this works by moving the
real icon far off-screen, remembering its original position (persisted to
disk, so it survives an app restart or crash) so it can be moved back once
the shortcut isn't in any fence anymore.

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
