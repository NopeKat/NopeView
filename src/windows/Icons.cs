// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace NopeView.App;

/*
 * Icons that are swapped from code need a small factory instead of a resource
 * lookup. WinUI rejects a Geometry that was created from markup once it is
 * bound to a PathIcon, so each state gets its own freshly parsed icon.
 * Both drawings are the official Fluent UI System Icons (regular, 16 px).
 */
internal static class Icons
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private const string PlayData =
        "F1 M5.74514 3.06445C5.41183 2.87696 5 3.11781 5 3.50023V12.5005C5 12.8829 5.41182 13.1238 5.74512 12.9363L13.7454 8.43631C14.0852 8.24517 14.0852 7.75589 13.7454 7.56474L5.74514 3.06445ZM4 3.50023C4 2.35298 5.2355 1.63041 6.23541 2.19288L14.2357 6.69317C15.2551 7.26664 15.2551 8.73446 14.2356 9.3079L6.23537 13.8079C5.23546 14.3703 4 13.6477 4 12.5005V3.50023Z";

    private const string PauseData =
        "F1 M3.75 2C2.7835 2 2 2.7835 2 3.75V12.25C2 13.2165 2.7835 14 3.75 14H5.25C6.2165 14 7 13.2165 7 12.25V3.75C7 2.7835 6.2165 2 5.25 2H3.75ZM3 3.75C3 3.33579 3.33579 3 3.75 3H5.25C5.66421 3 6 3.33579 6 3.75V12.25C6 12.6642 5.66421 13 5.25 13H3.75C3.33579 13 3 12.6642 3 12.25V3.75ZM10.75 2C9.7835 2 9 2.7835 9 3.75V12.25C9 13.2165 9.7835 14 10.75 14H12.25C13.2165 14 14 13.2165 14 12.25V3.75C14 2.7835 13.2165 2 12.25 2H10.75ZM10 3.75C10 3.33579 10.3358 3 10.75 3H12.25C12.6642 3 13 3.33579 13 3.75V12.25C13 12.6642 12.6642 13 12.25 13H10.75C10.3358 13 10 12.6642 10 12.25V3.75Z";

    public static PathIcon CreatePlayPauseIcon(bool isPlaying)
    {
        var data = isPlaying ? PauseData : PlayData;
        var markup = string.Concat(
            "<PathIcon xmlns=\"", XamlNamespace,
            "\" Width=\"16\" Height=\"16\" Data=\"", data, "\" />");
        return (PathIcon)XamlReader.Load(markup);
    }
}
