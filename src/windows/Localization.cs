using Microsoft.Windows.ApplicationModel.Resources;
using System.Diagnostics;
using System.Globalization;

namespace NopeView.App;

/// <summary>
/// One place for the app language and for resource lookups. The language
/// follows the stored choice, or the Windows language list when the choice is
/// "system". Resources live in Strings/&lt;language&gt;/Resources.resw and are
/// indexed into the app PRI file at build time.
/// </summary>
internal static class Localization
{
    internal const string SystemPreference = "";
    internal const string FallbackLanguage = "en-US";
    internal const string SimplifiedChinese = "zh-Hans";

    private static ResourceManager? _resources;
    private static ResourceContext? _context;
    private static string _language = FallbackLanguage;

    /// <summary>
    /// Applies a stored preference. An empty preference means "follow Windows";
    /// anything unsupported also falls back to Windows, then to English.
    /// </summary>
    internal static void Apply(string? preference)
    {
        var language = Resolve(preference);
        _language = language;
        _context = null;

        // The WinUI framework reads this override for strings it ships itself,
        // such as picker buttons and the InfoBar close command.
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Localization: could not set the framework language: {ex.Message}");
        }
    }

    internal static string Get(string key)
    {
        try
        {
            // The resource index stores a dotted key as nested subtrees, so
            // "Status.ViewFit" is reached as "Resources/Status/ViewFit". The
            // dotted form is tried first for file-level keys that have no dot
            // split.
            var map = Resources.MainResourceMap;
            var value = map.TryGetValue("Resources/" + key, Context);
            if (value is null && key.Contains('.'))
            {
                value = map.TryGetValue(
                    "Resources/" + key.Replace('.', '/'), Context);
            }

            var text = value?.ValueAsString;
            return string.IsNullOrEmpty(text) ? key : text;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Localization: could not read {key}: {ex.Message}");
            return key;
        }
    }

    internal static string Format(string key, params object[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
    }

    private static ResourceManager Resources
    {
        get
        {
            if (_resources is null)
            {
                var pri = Path.Combine(AppContext.BaseDirectory, "NopeView.App.pri");
                _resources = File.Exists(pri)
                    ? new ResourceManager(pri)
                    : new ResourceManager();
            }

            return _resources;
        }
    }

    private static ResourceContext Context =>
        _context ??= CreateContext();

    private static ResourceContext CreateContext()
    {
        var context = Resources.CreateResourceContext();
        // An explicit context language makes a switch at runtime reliable; the
        // process-wide framework context may already be cached.
        context.QualifierValues["Language"] = _language;
        return context;
    }

    private static string Resolve(string? preference)
    {
        if (string.Equals(preference, SimplifiedChinese, StringComparison.OrdinalIgnoreCase))
        {
            return SimplifiedChinese;
        }

        if (string.Equals(preference, FallbackLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return FallbackLanguage;
        }

        foreach (var tag in Windows.Globalization.ApplicationLanguages.Languages)
        {
            if (IsSimplifiedChinese(tag))
            {
                return SimplifiedChinese;
            }

            if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                return FallbackLanguage;
            }
        }

        return FallbackLanguage;
    }

    private static bool IsSimplifiedChinese(string tag)
    {
        if (!tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Traditional Chinese is a different writing system and needs its own
        // resources; it must not silently fall into this bundle.
        if (tag.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
            tag.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
            tag.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
            tag.EndsWith("-MO", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
