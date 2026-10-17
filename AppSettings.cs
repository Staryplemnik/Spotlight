using System.IO;
using System.Text.Json;
using System.Windows;

namespace Spotlight;

public sealed record AppSettings(
    bool StartWithWindows = false,
    bool SearchFiles = true)
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spotlight",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Windows.MessageBox.Show(
                $"Spotlight could not load its settings. Default settings will be used.\n\n{ex.Message}",
                "Spotlight settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return new AppSettings();
        }
    }

    public void Save()
    {
        string directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("The settings folder could not be determined.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
    }
}
