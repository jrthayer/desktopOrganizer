using System.Text.Json;

namespace DesktopTool.Features.Layouts;

/// <summary>Same shape as LayoutStore/FenceStore (plain JSON file under %AppData%\DesktopTool,
/// corrupt-or-missing-file-starts-fresh, no debouncing - every setter on the widget just calls
/// Save() directly), but for a single LayoutLauncherModel instead of a list - there's only ever one
/// launcher widget.</summary>
public sealed class LayoutLauncherStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "layout-launcher.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public LayoutLauncherModel Load()
    {
        if (!File.Exists(FilePath))
            return new LayoutLauncherModel();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<LayoutLauncherModel>(json, SerializerOptions) ?? new LayoutLauncherModel();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new LayoutLauncherModel();
        }
    }

    public void Save(LayoutLauncherModel model)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(model, SerializerOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }
}
