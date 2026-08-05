using System.Text.Json;

namespace DesktopTool.Features.Fences;

public sealed class FenceStore
{
    // Kept as "FenceTool", not renamed alongside the app/assembly - this is where existing
    // users' real fence data already lives; changing it would make every current fence (and the
    // real desktop files it's hiding, see DesktopIconHider) silently "disappear" until someone
    // migrates the AppData folder by hand.
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FenceTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "fences.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public List<FenceModel> Load()
    {
        if (!File.Exists(FilePath))
            return new List<FenceModel>();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<FenceModel>>(json, SerializerOptions) ?? new List<FenceModel>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable config: start fresh rather than crash the app.
            return new List<FenceModel>();
        }
    }

    public void Save(IReadOnlyList<FenceModel> models)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(models, SerializerOptions);
        File.WriteAllText(FilePath, json);
    }
}
