// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

namespace NopeView.App;

/// <summary>
/// The command line this launch was started with. One shape is accepted:
/// <c>--open &lt;path&gt;</c>, which is also what a shell handover or a later
/// instance forwards. A bare path is not treated as an image, because the
/// launcher decides the form and the viewer only reads its own.
/// </summary>
internal static class LaunchArguments
{
    /// <summary>
    /// The image path this launch asked to open, or null when it asked for
    /// nothing more than a window.
    /// </summary>
    internal static string? OpenPath()
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 1; index + 1 < args.Length; index++)
        {
            if (args[index] == "--open")
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
