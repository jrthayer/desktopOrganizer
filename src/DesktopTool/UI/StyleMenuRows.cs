namespace DesktopTool.UI;

/// <summary>The Fence-style settings-dropdown block shared by anything styled via IWidgetStyle -
/// the color grid (Default + 8 presets + Custom...) and the Header Darkness/Opacity/Tint Strength
/// sliders plus the Margin stepper, in that order. A caller (FenceForm, LayoutLauncherWidget, or a
/// future widget) builds its own full row list by prepending/appending whatever extra rows are
/// specific to it (Hide Title, OCD Sizing, etc.) around what Build returns here, instead of
/// re-typing this same block - and re-risking a subtly different copy - every time.
///
/// Command ids for the color rows are supplied by the caller (colorDefaultId/colorCustomId/
/// colorPresetBaseId) rather than fixed constants here, so this can slot into an existing
/// command-id scheme (like FenceForm's own Cmd* consts) without renumbering anything or risking a
/// collision with that caller's other rows.</summary>
internal static class StyleMenuRows
{
    public static List<DropdownMenu.Row> Build(
        IWidgetStyle style,
        Color defaultSwatch,
        int colorDefaultId,
        int colorCustomId,
        int colorPresetBaseId,
        Action<int> onHeaderDarknessChange,
        Action<int> onOpacityChange,
        Action<int> onTintStrengthChange,
        Action<int> onMarginChange)
    {
        var rows = new List<DropdownMenu.Row>
        {
            new(0, "Color", IsHeader: true),
            new(colorDefaultId, string.Empty, IsGridItem: true, Swatch: defaultSwatch,
                IsChecked: () => style.TintColor is null, Tooltip: "Default"),
        };
        for (var i = 0; i < StyleTint.Presets.Length; i++)
        {
            var presetArgb = StyleTint.Presets[i].ToArgb();
            rows.Add(new DropdownMenu.Row(colorPresetBaseId + i, string.Empty, IsGridItem: true, Swatch: StyleTint.Presets[i],
                IsChecked: () => style.TintColor == presetArgb, Tooltip: StyleTint.PresetNames[i]));
        }
        rows.Add(new DropdownMenu.Row(colorCustomId, string.Empty, IsGridItem: true,
            Glyph: DropdownMenu.GridGlyph.Plus, Tooltip: "Custom..."));

        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
        rows.Add(new DropdownMenu.Row(0, "Header Darkness", IsHeader: true));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSlider: true,
            SliderValue: () => style.HeaderDarkness / 100.0,
            OnSliderChange: value => onHeaderDarknessChange((int)Math.Round(value * 100))));
        rows.Add(new DropdownMenu.Row(0, "Opacity", IsHeader: true));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSlider: true,
            SliderValue: () => style.Opacity / 100.0,
            OnSliderChange: value => onOpacityChange((int)Math.Round(value * 100))));
        rows.Add(new DropdownMenu.Row(0, "Tint Strength", IsHeader: true));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSlider: true,
            SliderValue: () => style.TintStrength / 100.0,
            OnSliderChange: value => onTintStrengthChange((int)Math.Round(value * 100))));

        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
        rows.Add(new DropdownMenu.Row(0, "Margin", IsHeader: true));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsStepper: true,
            StepperValue: () => style.Margin, OnStepperChange: onMarginChange,
            StepperMin: 0, StepperMax: 100, StepperStep: 5, StepperSuffix: "px"));

        return rows;
    }

    /// <summary>Handles whichever of the three color-row command ids Build produced above - returns
    /// false for anything else so a caller's own HandleCommand switch can fall through to its own
    /// cases unchanged. currentTint seeds the ColorDialog with the element's current pick (or black,
    /// for "never picked one yet") the same way both FenceForm.PickCustomColor and
    /// LayoutLauncherWidget.PickCustomColor already did before this replaced their private copies.</summary>
    public static bool TryHandleColorCommand(int id, int colorDefaultId, int colorCustomId, int colorPresetBaseId,
        IWin32Window owner, Color? currentTint, Action<Color?> setColor)
    {
        if (id == colorDefaultId)
        {
            setColor(null);
            return true;
        }

        if (id == colorCustomId)
        {
            using var dialog = new ColorDialog { Color = currentTint ?? Color.Black, FullOpen = true };
            if (dialog.ShowDialog(owner) == DialogResult.OK)
                setColor(dialog.Color);
            return true;
        }

        if (id >= colorPresetBaseId && id < colorPresetBaseId + 100)
        {
            setColor(StyleTint.GetPreset(id - colorPresetBaseId));
            return true;
        }

        return false;
    }

    /// <summary>Same overflow check as FenceForm.ShouldSettingsButtonOpenLeft: measures the actual
    /// menu (plus the widest row tooltip, which reaches further right than the menu's own edge once
    /// hovered) against the screen buttonScreenRectIfRight is on, using the button's default
    /// top-right placement as the anchor - "would the menu fit opening rightward from there".</summary>
    public static bool ShouldOpenLeft(Rectangle buttonScreenRectIfRight, IEnumerable<DropdownMenu.Row> rows, Font font)
    {
        var rowList = rows as IReadOnlyList<DropdownMenu.Row> ?? rows.ToList();
        var workingArea = Screen.FromRectangle(buttonScreenRectIfRight).WorkingArea;
        var menuSize = DropdownMenu.Measure(rowList, font);
        var maxTooltipWidth = DropdownMenu.MaxTooltipWidth(rowList, font);
        var tooltipReach = maxTooltipWidth > 0 ? DropdownMenu.AnchorGap + maxTooltipWidth : 0;
        return buttonScreenRectIfRight.Right + DropdownMenu.AnchorGap + menuSize.Width + tooltipReach > workingArea.Right;
    }
}
