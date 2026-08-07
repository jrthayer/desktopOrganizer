# Snapping

Pure, stateless edge-snapping geometry ([`SnapEngine`](SnapEngine.cs)) — the math behind every
fence and widget's "snaps to other fences' edges and to custom guide lines" dragging feel, kept
independent of fences, windows, or any particular widget type so any future draggable/resizable UI
element in this app can reuse it the same way [`FenceForm`](../Fences/UI/FenceForm.cs) and
[`LayoutLauncherWidget`](../Layouts/UI/LayoutLauncherWidget.cs) both already do. This is the whole
feature — no model, store, or UI of its own, just the one static class.

`SnapEngine` never looks up where a candidate position comes from — another fence's edge, a
user-placed guide line, or something else entirely later — the caller supplies plain screen-pixel
coordinates (X values for vertical lines, Y values for horizontal ones) and gets back a possibly-
adjusted rectangle plus which candidates it actually snapped to:

- **`SnapMove`** — snaps a pure translation. Width/height must stay identical to what was proposed,
  or the drag would visibly jitter/resize; Left/Right are compared against vertical candidates and
  Top/Bottom against horizontal candidates independently, and whichever edge on each axis lands
  closest to a candidate within the threshold decides that axis's offset, applied to the whole rect
  so it only ever translates.
- **`SnapResize`** — snaps only the edge(s) named by `SnapEdges` (a corner drag moves two at once);
  the opposite edge(s) come back unchanged, matching how an OS resize drag only ever moves the
  edge(s) under the cursor. A snap that would push an edge past its own opposite is skipped rather
  than emitting an inverted rect, which the OS's own drag-tracking can't recover from.

Both default to an 8px threshold (`DefaultThresholdPx`) and are used by fences' own snap-to-fence/
snap-to-guide-line dragging and by the Layout Launcher widget's dragging — see
[Fences: Snap lines](../Fences/README.md#snap-lines) for where the actual candidate positions (fence
edges, saved guide lines) come from and how they're managed.
