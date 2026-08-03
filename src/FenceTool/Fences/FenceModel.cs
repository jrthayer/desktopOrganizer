using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FenceTool.Fences;

/// <summary>A file/shortcut a fence holds a reference to. DisplayName, when set, overrides the
/// label shown in the fence (renaming here never touches the real file on disk).</summary>
public sealed class FenceItem
{
    public string Path { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
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
    public int HeaderDarkness { get; set; } = 65;

    /// <summary>0-100 - how translucent the whole fence renders (see FenceForm.EffectiveOpacity),
    /// clamped to a safe minimum by FenceManager.SetOpacity so a fence can never be dragged all the
    /// way to fully invisible/unclickable. 85 matches the fixed opacity this used to be before it
    /// became adjustable. Ignored (forced to 100) while TintIsExact - see EffectiveOpacity.</summary>
    public int Opacity { get; set; } = 85;

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
