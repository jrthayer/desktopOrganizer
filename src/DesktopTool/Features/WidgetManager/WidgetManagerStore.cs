using System.Text.Json;

namespace DesktopTool.Features.WidgetManager;

/// <summary>Same shape as LayoutLauncherStore (plain JSON file under %AppData%\DesktopTool,
/// corrupt-or-missing-file-starts-fresh, no debouncing - every setter on the widget just calls
/// Save() directly), but for a single WidgetManagerModel instead of a list - there's only ever one
/// Widget Manager widget.</summary>
public sealed class WidgetManagerStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "widget-manager.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public WidgetManagerModel Load()
    {
        if (!File.Exists(FilePath))
            return new WidgetManagerModel();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<WidgetManagerModel>(json, SerializerOptions) ?? new WidgetManagerModel();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new WidgetManagerModel();
        }
    }

    public void Save(WidgetManagerModel model)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(model, SerializerOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }
}
