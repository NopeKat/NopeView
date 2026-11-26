// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

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
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("theme")] string Theme,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("imageFrame")] string? ImageFrame,
    [property: JsonPropertyName("settingsWindow")] WindowBounds? SettingsWindow);

/// <summary>
/// Pixel rectangle of the settings window, in the same units the windowing
/// APIs report. The values are stored as they were measured so the window
/// reopens at the size and place the user left it. The flags say whether the
/// window was maximized on top of that rectangle, because a maximized window
/// has no rectangle of its own worth remembering.
/// </summary>
internal sealed record WindowBounds(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("maximized")] bool Maximized = false);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SettingsDocument))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
