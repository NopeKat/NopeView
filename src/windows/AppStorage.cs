// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using System.Diagnostics;

namespace NopeView.App;

/// <summary>
/// Folder that holds the files the app writes at run time: the folder beside
/// the executable, so a portable copy keeps its settings with it and leaves
/// nothing behind. A folder that cannot be written to is reported as a failed
/// write; the app never moves its files somewhere else on its own.
/// </summary>
internal static class AppStorage
{
    private const string FolderName = "NopeView";

    /// <summary>Absolute folder the app writes its files to.</summary>
    internal static string Root => AppContext.BaseDirectory;

    /// <summary>
    /// Folder the app used for its files before it wrote beside the executable.
    /// Only read while adopting a file from it.
    /// </summary>
    private static string LegacyFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FolderName);

    /// <summary>
    /// Takes over a file the app kept in the legacy folder. The new copy is
    /// written first and the old one is removed after it lands, so the folder
    /// the app no longer writes to stops holding files. A file already in the
    /// new location always wins, so the adoption runs at most once.
    /// </summary>
    internal static void AdoptLegacyFile(string fileName, string target)
    {
        if (File.Exists(target))
        {
            return;
        }

        var legacy = Path.Combine(LegacyFolder, fileName);
        if (!File.Exists(legacy))
        {
            return;
        }

        try
        {
            File.Copy(legacy, target, overwrite: false);
            File.Delete(legacy);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Storage: could not adopt {legacy}: {ex.Message}");
        }
    }
}
