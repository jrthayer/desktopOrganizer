# Widget Manager

A persistent on-screen panel listing the app's three toggleable widgets - Fences, Snap Lines, and
Layout Launcher - each with an on/off switch plus a row-specific action button, so all three can be
reached without opening the tray menu. All of this feature's code lives under this folder -
`WidgetManager/` for the model/store, `WidgetManager/UI/` for the widget itself. It's built on the
same [`LayeredWidgetForm`](../../UI/LayeredWidgetForm.cs) base as a fence and the
[Layout Launcher](../Layouts/README.md#layout-launcher-widget) widget, so it shares their move/
resize/snap/rename/Settings-menu/theming for free - see [UI](../../UI/README.md) for that shared
foundation. Entry points into it (the tray menu toggle) live in the app-wide
[`TrayApplicationContext`](../../TrayApplicationContext.cs) - see the base
[README](../../../../README.md) for those.

## Rows

Unlike the Layout Launcher's own saved-layout list, Widget Manager's three rows are fixed - nothing
adds or removes one. Each row has a label, an on/off switch (a small hand-drawn pill, since there's
no toggle-switch control elsewhere in the app to reuse), and its own action button:

- **Fences** - the switch shows/hides every fence at once, the same as the tray's own **Show/Hide
  All** (both read/flip `FenceManager.AnyVisible`, so they can never drift out of sync with each
  other or with a fence hidden/shown some other way). **Add Fence** creates a new, empty fence, the
  same as the tray's **New Fence**.
- **Snap Lines** - the switch turns every custom snap line (including the seeded default edge
  lines - they're plain entries in the same list, not distinguished from a user-drawn one) off or on
  as a drag candidate app-wide, while fence-to-fence edge snapping keeps working either way - see
  [Fences: Snap lines](../Fences/README.md#snap-lines). **Edit** opens **Manage Snap Lines...**,
  the same as the tray item - editing lines works regardless of the switch's state.
- **Layout Launcher** - the switch shows/hides that widget, the same as the tray's own **Widgets >
  Layout Launcher** toggle (both read/flip the widget's own `Visible`). **Edit Layouts** opens
  **Manage Layouts**, the same as that widget's own button.

## Limitations

Snap Lines had no on/off concept at all before this widget existed - custom lines were always live
snap candidates, only ever suppressed for a single drag by holding the right mouse button. The new
`SnapLineManager.Enabled` flag this row's switch controls is a genuinely new, persisted, app-wide
setting (`snaplines.json`), not just a fresh way to reach something that already existed.
