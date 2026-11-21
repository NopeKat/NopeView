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
/// How the image edge is separated from the viewer background.
/// </summary>
internal enum ImageFrameStyle
{
    None,
    Stroke,
    Shadow
}

/// <summary>
/// The on-disk shape of the settings file. Keeping it a plain record makes
/// the source-generated serializer able to write it without reflection.
/// </summary>
internal sealed record SettingsDocument(
    [property: JsonPropertyName("theme")] string Theme,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("imageFrame")] string? ImageFrame,
    [property: JsonPropertyName("settingsWindow")] WindowBounds? SettingsWindow);

/// <summary>
/// Pixel rectangle of the settings window, in the same units the windowing
/// APIs report. The values are stored as they were measured so the window
/// reopens at the size and place the user left it.
/// </summary>
internal sealed record WindowBounds(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);


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
    /// Stored image edge treatment. None keeps the image flush with the canvas,
    /// which is the neutral default.
    /// </summary>
    internal ImageFrameStyle ImageFrame { get; private set; } = ImageFrameStyle.None;

    /// <summary>
    /// Stored language preference, or an empty string to follow Windows.
    /// </summary>
    internal string Language { get; private set; } = Localization.SystemPreference;

    /// <summary>
    /// Last size and position of the settings window, or null before the window
    /// has been closed once. The settings window is its own top level window, so
    /// it remembers where the user left it instead of always opening centred.
    /// </summary>
    internal WindowBounds? SettingsWindowBounds { get; private set; }

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

            if (document?.ImageFrame is { } frameName &&
                Enum.TryParse(frameName, ignoreCase: true, out ImageFrameStyle frame) &&
                Enum.IsDefined(frame))
            {
                ImageFrame = frame;
            }

            // A zero or absurd rectangle is treated like no stored value, so a
            // hand-edited or truncated file cannot open a window nothing can show.
            if (document?.SettingsWindow is { } bounds &&
                bounds.Width >= 200 && bounds.Height >= 200 &&
                bounds.Width <= 4000 && bounds.Height <= 4000)
            {
                SettingsWindowBounds = bounds;
            }
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

    internal void SetImageFrame(ImageFrameStyle frame)
    {
        ImageFrame = frame;
        Save();
    }

    internal void SetSettingsWindowBounds(WindowBounds bounds)
    {
        SettingsWindowBounds = bounds;
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
                new SettingsDocument(
                    Theme.ToString(), Language, ImageFrame.ToString(), SettingsWindowBounds),
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
