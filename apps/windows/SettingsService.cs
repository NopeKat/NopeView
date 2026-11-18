using System.Text.Json;

namespace NopeView.App;

/// <summary>
/// Theme choice stored in the user settings file.
/// </summary>
internal enum AppTheme
{
    System,
    Light,
    Dark
}

/// <summary>
/// Persists user preferences as JSON under the local app data folder.
/// A missing or unreadable file falls back to defaults instead of failing.
/// </summary>
internal sealed class SettingsService
{
    private const string FolderName = "NopeView";
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    internal SettingsService()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _path = Path.Combine(root, FolderName, FileName);
    }

    internal AppTheme Theme { get; private set; } = AppTheme.System;

    internal void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("theme", out var theme) &&
                theme.ValueKind == JsonValueKind.String &&
                Enum.TryParse(theme.GetString(), ignoreCase: true, out AppTheme parsed) &&
                Enum.IsDefined(parsed))
            {
                Theme = parsed;
            }
        }
        catch
        {
            // A corrupt or inaccessible settings file must not block startup.
        }
    }

    internal void SetTheme(AppTheme theme)
    {
        Theme = theme;
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(new { theme = theme.ToString() }, s_options);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Keep the in-memory choice even if writing the file fails.
        }
    }
}
