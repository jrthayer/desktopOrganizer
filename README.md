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
