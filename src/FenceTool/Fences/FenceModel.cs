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
