using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

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
/// The on-disk shape of the settings file. Keeping it a plain record makes
/// the source-generated serializer able to write it without reflection.
/// </summary>
internal sealed record SettingsDocument(
    [property: JsonPropertyName("theme")] string Theme,
    [property: JsonPropertyName("language")] string? Language);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SettingsDocument))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Persists user preferences as JSON under the local app data folder.
/// A missing or unreadable file falls back to defaults instead of failing.
/// Writes go through a temporary file so a crash mid-write cannot leave a
/// truncated file behind.
/// </summary>
internal sealed class SettingsService
{
    private const string FolderName = "NopeView";
    private const string FileName = "settings.json";

    private readonly string _path;

    internal SettingsService()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _path = Path.Combine(root, FolderName, FileName);
    }

    internal AppTheme Theme { get; private set; } = AppTheme.System;

    /// <summary>
    /// Stored language preference, or an empty string to follow Windows.
    /// </summary>
    internal string Language { get; private set; } = Localization.SystemPreference;

    internal void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            var document = JsonSerializer.Deserialize(
                json, SettingsJsonContext.Default.SettingsDocument);
            if (document?.Theme is { } name &&
                Enum.TryParse(name, ignoreCase: true, out AppTheme parsed) &&
                Enum.IsDefined(parsed))
            {
                Theme = parsed;
            }

            Language = document?.Language switch
            {
                Localization.SimplifiedChinese => Localization.SimplifiedChinese,
                Localization.FallbackLanguage => Localization.FallbackLanguage,
                _ => Localization.SystemPreference
            };
        }
        catch (Exception ex)
        {
            // A corrupt or inaccessible settings file must not block startup,
            // but the reason should be visible in a debug session.
            Debug.WriteLine($"Settings: could not read {_path}: {ex.Message}");
        }
    }

    internal void SetTheme(AppTheme theme)
    {
        Theme = theme;
        Save();
    }

    internal void SetLanguage(string language)
    {
        Language = language;
        Save();
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(
                new SettingsDocument(Theme.ToString(), Language),
                SettingsJsonContext.Default.SettingsDocument);

            // Write beside the target and replace in one step. Readers either
            // see the previous file or the new one, never a partial write.
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Keep the in-memory choice even if writing the file fails.
            Debug.WriteLine($"Settings: could not write {_path}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Settings: could not clean up {temporary}: {ex.Message}");
            }
        }
    }
}
