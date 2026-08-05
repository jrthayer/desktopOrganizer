namespace DesktopTool.UI;

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
}
