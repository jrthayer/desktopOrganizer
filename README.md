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

Click a fence to activate it, then click **Settings** near the top of its
title bar to open its settings menu. Two more buttons sit next to it: a
duplicate-icon button that creates a new, empty fence with this one's
settings (color, Hide Title/Labels, OCD sizing) copied over, and an **x**
that deletes the fence (with a confirmation prompt) — its shortcuts aren't
deleted, only removed from the fence; their real desktop icons are
restored (see Desktop icon hiding above).

- **Hide Shortcut Names** — hides the label under each icon, showing icons
  only. Toggle it again to bring labels back.
- **Hide Title** — hides the fence's title bar entirely, reclaiming that
  space for the icon grid. The fence can still be moved via its outer
  margin.
- **OCD Fence Sizing** — after you resize the fence by hand, automatically
  snaps it to the tightest size that fits its icons (equivalent to running
  OCD → Both after every manual resize).
- **Full Opacity When Active** — off by default. When on, the fence
  renders fully opaque while hovered, while being dragged or resized, or
  while its own settings menu is open, easing back down to the Fence
  Opacity slider's value once none of those still apply.
- **Fence Color** — pick one of the eight preset swatches, **Custom...**
  for the full Windows color picker, **Eyedropper** to sample a color from
  anywhere on screen (even outside the app), or **Default** to reset to
  the plain dark theme.
  - **Header Darkness** — how much black blends into the title bar,
    independent of the fence's own color.
  - **Fence Opacity** — how translucent the fence renders, clamped to a
    15% floor so it can never be dragged all the way to
    invisible/unclickable.
  - **Tint Strength** — how strongly a preset/Custom... color blends into
    the dark theme rather than replacing it outright. An Eyedropper pick
    uses this the opposite way: 0% (where every fresh pick starts) keeps
    the sampled color exact, and dragging it up mutes that color back
    toward the plain theme instead. Picking any color — even re-picking
    the one already selected — resets Header Darkness, Fence Opacity, and
    Tint Strength back to their defaults.
- **OCD** — a submenu with three one-off resize actions: **Both** (trims
  width and height), **Left/Right** (width only), and **Top/Down** (height
  only). Each shrinks or grows the fence to fit its current icons without
  changing its top-left corner.

**Limitations:** Full Opacity When Active's hover detection covers the
outer margin (used for dragging/resizing) as well as the visible body, but
it does so via a separate, lower-level Windows message path than normal
mouse events use — an edge case in a future Windows version changing that
behavior could in theory leave the margin's hover detection stale, though
the visible body would be unaffected either way.

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
