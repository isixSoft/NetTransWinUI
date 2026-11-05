using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Globalization;

namespace NetTrans.Services;

/// <summary>
/// Thin wrapper over MRT Core (Windows App SDK's decoupled resource system).
/// Callers pull strings through Get(key) or Get(key, args...) for composite
/// values. The underlying ResourceManager follows ApplicationLanguages;
/// App.xaml.cs sets PrimaryLanguageOverride at boot so a single manager is
/// enough for the whole process.
/// </summary>
public static class L10n
{
    // "Resources" matches the resw file name under Strings/<lang>/Resources.resw.
    private static readonly ResourceManager _manager = new();
    private static readonly ResourceContext _context = _manager.CreateResourceContext();

    /// <summary>Resolve a resource by its resw data name; returns the key itself
    /// as fallback so a missing entry never crashes a UI element.</summary>
    public static string Get(string key)
    {
        try
        {
            var candidate = _manager.MainResourceMap
                .TryGetValue("Resources/" + key, _context);
            return candidate?.ValueAsString ?? key;
        }
        catch
        {
            return key;
        }
    }

    /// <summary>Composite values like "Remove {0} items" — passes args through
    /// string.Format so the resw entry owns the shape of the message.</summary>
    public static string Get(string key, params object[] args)
    {
        var template = Get(key);
        try { return string.Format(template, args); }
        catch { return template; }
    }

    /// <summary>Primary-language picker ("auto" resets to the system default).
    /// Called once at app boot before any XAML is loaded, so the rest of the UI
    /// resolves strings against the chosen language from day one.</summary>
    public static void Apply(string? lang)
    {
        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = lang switch
            {
                "en" => "en-US",
                "zh-CN" => "zh-Hans-CN",
                _ => "", // empty = follow system
            };
        }
        catch
        {
            // Unpackaged apps on older builds may surface here; the fallback is
            // benign — the UI will just stay on the system locale.
        }
    }
}
