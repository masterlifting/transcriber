using System.Text.Json;

namespace Transcriber.Core.Settings;

/// <summary>User preferences persisted in %LOCALAPPDATA%\Transcriber\settings.json.</summary>
public sealed class AppSettings
{
    public string? LastApplication { get; set; }
    public string? LastMicrophone { get; set; } // stable endpoint id or "default"

    /// <summary>Optional override for the calls directory (defaults to Documents\Transcriber\Calls in portable mode).</summary>
    public string? CallsDirectory { get; set; }
}

/// <summary>
/// Lightweight JSON settings store. Tolerant of missing or corrupt files:
/// never crashes on stale settings.
/// </summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Transcriber",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var parsed = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (parsed is not null)
                {
                    return parsed;
                }
            }
        }
        catch (Exception)
        {
            // corrupt settings: fall back to defaults
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception)
        {
            // settings persistence is best-effort; never block the app
        }
    }
}
