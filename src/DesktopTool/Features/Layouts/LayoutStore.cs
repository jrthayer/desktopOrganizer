using System.Text.Json;

namespace DesktopTool.Features.Layouts;

public sealed class LayoutStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "layouts.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public List<LayoutProfile> Load()
    {
        if (!File.Exists(FilePath))
            return new List<LayoutProfile>();

        try
        {
            var json = File.ReadAllText(FilePath);
            var profiles = JsonSerializer.Deserialize<List<LayoutProfile>>(json, SerializerOptions) ?? new List<LayoutProfile>();

            // Placement is persisted as a raw int (no JsonStringEnumConverter), so a LayoutPlacement
            // member ever being inserted/removed/reordered leaves old files holding an ordinal that
            // no longer matches any current member - System.Text.Json deserializes that silently
            // rather than failing, so without this it would carry through as an unmatched enum value
            // (DescribePlacement renders it as a raw number, and WindowPlacer.ResolveRect's default
            // arm falls back to the full monitor area, ignoring the entry's own Custom* rect) until
            // the next time that entry happened to be re-saved. Custom, not the Placement-declared-
            // property default of Maximized, is the correct normalization - every affected entry
            // still has its real captured CustomX/Y/Width/Height sitting right there to fall back on.
            foreach (var entry in profiles.SelectMany(p => p.Entries))
            {
                if (!Enum.IsDefined(entry.Placement))
                    entry.Placement = LayoutPlacement.Custom;
            }

            return profiles;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable config: start fresh rather than crash the app.
            return new List<LayoutProfile>();
        }
    }

    public void Save(IReadOnlyList<LayoutProfile> profiles)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(profiles, SerializerOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }
}
