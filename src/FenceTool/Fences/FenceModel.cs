using System.Drawing;
using System.Text.Json.Serialization;

namespace FenceTool.Fences;

public sealed class FenceModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Fence";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 240;
    public int Height { get; set; } = 200;
    public List<string> IconNames { get; set; } = new();
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
