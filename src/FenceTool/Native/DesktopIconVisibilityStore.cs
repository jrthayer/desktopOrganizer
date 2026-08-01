using System.Text.Json;

namespace FenceTool.Native;

/// <summary>
/// Persists each currently-hidden real desktop icon's original position (see DesktopIconHider), so
/// a shortcut removed from its last fence can still be restored to the right spot even if Fence Tool
/// was restarted (or crashed) in between - not just within a single run's lifetime. Mirrors
/// FenceTool.Fences.FenceStore's own load/save shape, in a sibling file under the same app-data folder.
/// </summary>
internal sealed class DesktopIconVisibilityStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FenceTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "hidden-icons.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private sealed class Entry
    {
        public string Path { get; set; } = string.Empty;
        public int X { get; set; }
        public int Y { get; set; }
    }

    public Dictionary<string, Point> Load()
    {
        if (!File.Exists(FilePath))
            return new Dictionary<string, Point>();

        try
        {
            var json = File.ReadAllText(FilePath);
            var entries = JsonSerializer.Deserialize<List<Entry>>(json, SerializerOptions) ?? new List<Entry>();
            return entries.ToDictionary(e => e.Path, e => new Point(e.X, e.Y));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable: treat as "nothing hidden" rather than crash the app.
            return new Dictionary<string, Point>();
        }
    }

    public void Save(Dictionary<string, Point> positions)
    {
        Directory.CreateDirectory(DirectoryPath);
        var entries = positions.Select(kv => new Entry { Path = kv.Key, X = kv.Value.X, Y = kv.Value.Y }).ToList();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(entries, SerializerOptions));
    }
}
