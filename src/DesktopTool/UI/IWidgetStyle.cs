namespace DesktopTool.UI;

/// <summary>How the title text sits within its own row - see IWidgetStyle.TitleAlignment,
/// LayeredWidgetForm.PaintChrome's title draw. Public, not internal like IWidgetStyle itself -
/// FenceModel/LayoutLauncherModel expose it through their own public TitleAlignment properties.</summary>
public enum TitleAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>The six Fence-style per-element styling knobs (tint color, header darkness, opacity,
/// full-opacity-on-hover, tint strength, snap margin) - FenceModel and LayoutLauncherModel both
/// implement this, and StyleMenuRows.Build/SettingsButtonOverflow operate against it instead of a
/// concrete model type, so a third persisted model wanting this exact same styling only needs to
/// implement these six properties (it almost certainly already has them, if it's copied from either
/// existing model) to get the shared settings-menu rows and overflow-aware button positioning for
/// free. Deliberately not IDisposable/INotifyPropertyChanged/etc - this is a pure data contract, no
/// behavior of its own.</summary>
internal interface IWidgetStyle
{
    /// <summary>ARGB int (Color.ToArgb()), not System.Drawing.Color directly - Color doesn't
    /// round-trip through plain System.Text.Json without a custom converter. Null means untinted.</summary>
    int? TintColor { get; set; }

    /// <summary>True only when TintColor came from the Eyedropper (see StyleMenuRows.
    /// TryHandleColorCommand/EyedropperOverlay) rather than a preset/Custom... pick - changes how
    /// the element's own dominant fill colors blend it in (see each caller's own DilutedExact-style
    /// property), starting from the exact sampled pixel and diluting it back toward the plain theme
    /// by TintStrength instead of the usual "start plain, blend toward the pick" direction. A fresh
    /// pick also resets Opacity to 100 and TintStrength to 0 (see the Eyedropper handling in each
    /// caller's own HandleCommand) so it starts out pixel-exact - neither is forced to stay there.</summary>
    bool TintIsExact { get; set; }

    /// <summary>0-100 - how much black is blended into the header/title band's own base color
    /// before tinting (see StyleTint.DarkenTowardBlack), independent of TintColor's own blend amount.</summary>
    int HeaderDarkness { get; set; }

    /// <summary>0-100 - how translucent the whole element renders.</summary>
    int Opacity { get; set; }

    /// <summary>While on, renders fully opaque whenever "in use" (hovered, dragged/resized, or has
    /// its settings dropdown open), ignoring Opacity until none of those apply anymore.</summary>
    bool FullOpacityOnHover { get; set; }

    /// <summary>0-100 - how strongly TintColor blends into the plain dark theme (see StyleTint.Tint).</summary>
    int TintStrength { get; set; }

    /// <summary>0-100 physical pixels - how far this element wants to sit from another snap
    /// target's edge instead of landing flush, like a CSS margin. 0 means flush.</summary>
    int Margin { get; set; }

    /// <summary>Physical pixels - how rounded the element's own body/title corners are (see
    /// LayeredWidgetForm.PaintChrome). 0 means square corners.</summary>
    int CornerRadius { get; set; }

    /// <summary>Point size of the title text specifically (see LayeredWidgetForm.TitleFont) -
    /// independent of Control.Font, which every other themed element (rename box, dropdown, item
    /// labels) still uses unchanged.</summary>
    int TitleFontSize { get; set; }

    /// <summary>How the title text sits within its own row - see LayeredWidgetForm.PaintChrome.</summary>
    TitleAlignment TitleAlignment { get; set; }

    /// <summary>While on, every element this widget draws - its own outer border, its buttons (see
    /// LayeredWidgetForm.PaintSettingsButton/PaintExtraButtons/PaintContentButtons), and its list
    /// container (see PaintList) - is bordered in the header/title band's own color (ThemedTitle)
    /// instead of each one's usual border color, tying the whole widget together as one visibly
    /// matched set.</summary>
    bool HeaderBorderMode { get; set; }
}
