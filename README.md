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
