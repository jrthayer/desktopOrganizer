using System.Text.Json;

namespace DesktopTool.UI;

/// <summary>Same shape as WidgetManagerStore (plain JSON file under %AppData%\DesktopTool, corrupt-
/// or-missing-file-starts-fresh, no debouncing) - for a single CopySettingsPickerModel instead of a
/// list, since there's only ever one group picker open at a time. Loaded fresh each time a pick
/// starts rather than held onto by a long-lived owner, since CopySettingsGroupPicker itself is
/// created and disposed per pick instead of kept alive for the app's whole lifetime.</summary>
public sealed class CopySettingsPickerStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "copy-settings-picker.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public CopySettingsPickerModel Load()
    {
        if (!File.Exists(FilePath))
            return new CopySettingsPickerModel();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<CopySettingsPickerModel>(json, SerializerOptions) ?? new CopySettingsPickerModel();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new CopySettingsPickerModel();
        }
    }

    public void Save(CopySettingsPickerModel model)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(model, SerializerOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }
}
