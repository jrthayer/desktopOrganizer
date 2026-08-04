using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FenceTool.Fences;

/// <summary>A file/shortcut a fence holds a reference to. DisplayName, when set, overrides the
/// label shown in the fence (renaming here never touches the real file on disk).</summary>
public sealed class FenceItem
{
    /// <summary>Wherever the file currently lives on disk - its original location, unless
    /// RealDesktopPath is set, in which case DesktopIconHider has relocated it into its hidden
    /// folder (see that class's own doc comment) and this points at its hiding place instead.</summary>
    public string Path { get; set; } = string.Empty;
    public string? DisplayName { get; set; }

    /// <summary>Non-null only while this item is a relocated real desktop file - the original
    /// Desktop/Public Desktop location to move it back to once it's no longer in any fence, or
    /// when Fence Tool exits cleanly. Null for anything dragged in from elsewhere, which never had
    /// a real desktop icon to hide in the first place. See DesktopIconHider.</summary>
    public string? RealDesktopPath { get; set; }

    /// <summary>True only for the single synthetic Recycle Bin item a fence can hold (see
    /// FenceManager.AddRecycleBin) - not backed by a real file, so DesktopIconHider/AddFiles'
    /// existence checks must never run against it, and dropping other items onto it deletes them
    /// instead of the usual add/reorder/move behavior. Path is set to the Recycle Bin's own shell
    /// namespace CLSID string purely so the existing icon-extraction code can render its (empty/
    /// full-aware) system icon unmodified - it's never treated as a filesystem path anywhere else.</summary>
    public bool IsRecycleBin { get; set; }
}

/// <summary>Reads both the current fences.json format (Files as an array of FenceItem objects)
/// and the older pre-rename format (Files as a plain array of path strings), so upgrading doesn't
/// silently wipe out fences saved by an earlier version - see FenceStore.Load, which discards the
/// whole file on any deserialization failure. Always writes the current object-array format.</summary>
internal sealed class FenceItemListConverter : JsonConverter<List<FenceItem>>
{
    public override List<FenceItem> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException();

        var result = new List<FenceItem>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
                result.Add(new FenceItem { Path = reader.GetString() ?? string.Empty });
            else
                result.Add(JsonSerializer.Deserialize<FenceItem>(ref reader, options) ?? new FenceItem());
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<FenceItem> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

public sealed class FenceModel
{
    // Shared with FenceManager.SetTintColor's "click the same color again resets these" gesture, so
    // the reset target and each property's own initial value can never drift apart.
    public const int DefaultHeaderDarkness = 65;
    public const int DefaultOpacity = 85;
    public const int DefaultTintStrength = 55;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Fence";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 240;
    public int Height { get; set; } = 200;

    [JsonConverter(typeof(FenceItemListConverter))]
    public List<FenceItem> Files { get; set; } = new();
    public bool Collapsed { get; set; }
    public bool HideLabels { get; set; }
    public bool HideTitle { get; set; }
    public bool OcdFenceSizing { get; set; }

    /// <summary>ARGB int (Color.ToArgb()), not System.Drawing.Color directly - Color doesn't
    /// round-trip through System.Text.Json without a custom converter. Null means the default dark
    /// gray theme (see FenceForm.RenderAndPresent's Tint helper).</summary>
    public int? TintColor { get; set; }

    /// <summary>True only for a color picked via the Eyedropper (see FenceForm.PickEyedropperColor) -
    /// every other source (a preset, Custom...'s dialog, or no tint at all) leaves this false. Presets
    /// and Custom... are still blended toward the fixed dark theme (see FenceForm.Tint) so a fully
    /// saturated pick still reads as part of the same theme; an eyedropped color is meant to match an
    /// exact on-screen pixel, so it applies at full strength instead - see ThemedBody/ThemedTitle.</summary>
    public bool TintIsExact { get; set; }

    /// <summary>0-100 - how much black is blended into the title bar's own base color before tinting
    /// (see FenceForm.HeaderBaseColor/ThemedTitle), independent of TintColor's own blend amount. 65
    /// approximates the fixed near-black title color this used to be before it became adjustable.</summary>
    public int HeaderDarkness { get; set; } = DefaultHeaderDarkness;

    /// <summary>0-100 - how translucent the whole fence renders (see FenceForm.EffectiveOpacity),
    /// clamped to a safe minimum by FenceManager.SetOpacity so a fence can never be dragged all the
    /// way to fully invisible/unclickable. 85 matches the fixed opacity this used to be before it
    /// became adjustable. FenceForm.PickEyedropperColor sets this to 100 at the moment of an Eyedropper
    /// pick so it starts pixel-exact, but it's freely adjustable from there same as any other fence.</summary>
    public int Opacity { get; set; } = DefaultOpacity;

    /// <summary>Off by default - while on, this fence renders fully opaque (see
    /// FenceForm.TargetOpacity) whenever it's "in use": hovered, being dragged/resized, or has its
    /// settings dropdown open, ignoring the Opacity slider until none of those apply anymore.
    /// Independent of Opacity itself, which stays whatever it was set to and simply resumes once. The
    /// property name still says "OnHover" from before it covered the other two cases too - the
    /// display label ("Full Opacity When Active") reflects the current behavior instead; renaming
    /// this would silently drop the setting for anyone who already has it saved.</summary>
    public bool FullOpacityOnHover { get; set; }

    /// <summary>0-100 - how strongly TintColor blends into the fixed dark theme for every non-exact
    /// source (a preset or Custom...'s dialog - see FenceForm.Tint/TintAmount), independent of
    /// TintIsExact's own full-strength Eyedropper path. 55 matches the fixed blend this used to be
    /// before it became adjustable.</summary>
    public int TintStrength { get; set; } = DefaultTintStrength;

    /// <summary>0-100 physical pixels - how far this fence wants to sit from another fence's edge
    /// when it snaps against one (see FenceManager.GetOtherFenceEdges), instead of landing flush.
    /// It's this fence's own value that applies while it's the one being dragged, not the other
    /// fence's - like a CSS margin, which is also where the name comes from. 0 (the default) means
    /// flush edge-to-edge, the original behavior. Doesn't affect snapping to a custom snap line.</summary>
    public int Margin { get; set; }

    [JsonIgnore]
    public Rectangle Bounds
    {
        get => new(X, Y, Width, Height);
        set
        {
            X = value.X;
            Y = value.Y;
            Width = value.Width;
            Height = value.Height;
        }
    }
}
