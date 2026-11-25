using System.Diagnostics;
using System.Text.Json;

namespace NopeView.App;

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

    // A zero or absurd rectangle is treated like no stored value, so a
    // hand-edited or truncated file cannot open a window nothing can show.
    private const int MinWindowEdge = 200;
    private const int MaxWindowEdge = 4000;

    private readonly string _path;

    // Writes are disk work, so the UI thread only hands over the document that
    // should be on disk next. One background writer drains that slot, and a
    // newer document replaces a pending older one instead of queueing behind it.
    private readonly object _pendingGate = new();
    private SettingsDocument? _pending;
    private bool _writerRunning;

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
    /// has been closed once. The settings window is its own top level window,
    /// so it remembers where the user left it instead of always opening centred.
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

            if (document?.SettingsWindow is { } bounds &&
                bounds.Width >= MinWindowEdge && bounds.Height >= MinWindowEdge &&
                bounds.Width <= MaxWindowEdge && bounds.Height <= MaxWindowEdge)
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
        Update(() => Theme = theme);
    }

    internal void SetLanguage(string language)
    {
        Update(() => Language = language);
    }

    internal void SetImageFrame(ImageFrameStyle frame)
    {
        Update(() => ImageFrame = frame);
    }

    internal void SetSettingsWindowBounds(WindowBounds bounds)
    {
        Update(() => SettingsWindowBounds = bounds);
    }

    private void Update(Action update)
    {
        update();
        Save();
    }

    /// <summary>
    /// Schedules a write of the current values and returns at once. The caller
    /// runs on the UI thread, so nothing here touches the disk.
    /// </summary>
    private void Save()
    {
        var document = new SettingsDocument(
            Theme.ToString(), Language, ImageFrame.ToString(), SettingsWindowBounds);

        lock (_pendingGate)
        {
            _pending = document;
            if (_writerRunning)
            {
                return;
            }

            _writerRunning = true;
        }

        _ = Task.Run(WritePending);
    }

    /// <summary>
    /// Waits up to the given time for the pending write to reach the disk. Used
    /// while the window closes, where blocking is acceptable and losing the
    /// last choice is not.
    /// </summary>
    internal void Flush(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            lock (_pendingGate)
            {
                if (_pending is null && !_writerRunning)
                {
                    return;
                }
            }

            Thread.Sleep(1);
        }
    }

    private void WritePending()
    {
        while (true)
        {
            SettingsDocument document;
            lock (_pendingGate)
            {
                if (_pending is null)
                {
                    _writerRunning = false;
                    return;
                }

                document = _pending;
                _pending = null;
            }

            WriteDocument(document);
        }
    }

    private void WriteDocument(SettingsDocument document)
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
                document, SettingsJsonContext.Default.SettingsDocument);

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
