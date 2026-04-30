using System.IO;
using System.Windows;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;

namespace UsbDeviceBridge.App.Theming;

/// <summary>
/// Key colors extracted from a theme file, used for preview cards
/// without applying the theme globally.
/// </summary>
public record ThemePreview(
    WpfColor CardBackground,
    WpfColor TextPrimary,
    WpfColor TextMuted,
    WpfColor Accent,
    WpfColor Success,
    WpfColor Border);

/// <summary>
/// A discovered theme entry: display name + relative XAML source URI.
/// </summary>
public record ThemeEntry(string Name, Uri Source);

public static class ThemeManager
{
    // Keyed by display name (value of ThemeName resource in each XAML file).
    private static readonly Dictionary<string, ThemeEntry> _registry = [];
    private static readonly Dictionary<string, ThemePreview> _previewCache = [];
    private static bool _initialized;

    /// <summary>
    /// Display names of all discovered themes, in the order found in themes.manifest.
    /// </summary>
    public static IReadOnlyList<string> AvailableThemes { get; private set; } = ["Dark", "Light"];

    /// <summary>
    /// Scans <c>Themes/themes.manifest</c> (generated at build time from every
    /// <c>Themes/*Theme.xaml</c> file) and registers each XAML that contains a
    /// <c>ThemeName</c> string resource.  Safe to call multiple times.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        var manifestPath = Path.Combine(AppContext.BaseDirectory, "Themes", "themes.manifest");
        if (!File.Exists(manifestPath))
        {
            return;
        }

        var discovered = new List<ThemeEntry>();
        foreach (var line in File.ReadLines(manifestPath))
        {
            var filename = line.Trim();
            if (filename.Length == 0)
            {
                continue;
            }

            var relativeUri = new Uri($"Themes/{filename}", UriKind.Relative);
            try
            {
                var dict = new ResourceDictionary { Source = relativeUri };
                if (dict["ThemeName"] is not string name || name.Length == 0)
                {
                    continue;
                }

                var entry = new ThemeEntry(name, relativeUri);
                _registry[name] = entry;
                discovered.Add(entry);
            }
            catch
            {
                // Malformed or missing XAML — skip silently.
            }
        }

        if (discovered.Count > 0)
        {
            AvailableThemes = discovered.Select(e => e.Name).ToList();
        }
    }

    /// <summary>
    /// Loads a theme's ResourceDictionary without applying it and
    /// returns a <see cref="ThemePreview"/> with the key palette colors.
    /// Results are cached; the cache entry is invalidated by <see cref="ApplyTheme"/>.
    /// </summary>
    public static ThemePreview GetPreview(string themeName)
    {
        if (_previewCache.TryGetValue(themeName, out var cached))
        {
            return cached;
        }

        var source = ResolveSource(themeName);
        var dict = new ResourceDictionary { Source = source };

        WpfColor Read(string key) =>
            dict[key] is SolidColorBrush b ? b.Color : Colors.Gray;

        var preview = new ThemePreview(
            CardBackground: Read("SurfaceBg"),
            TextPrimary:    Read("TextPrimary"),
            TextMuted:      Read("TextMuted"),
            Accent:         Read("AccentBrush"),
            Success:        Read("SuccessBrush"),
            Border:         Read("BorderBrush"));

        _previewCache[themeName] = preview;
        return preview;
    }

    public static void ApplyTheme(string themeName)
    {
        var app = App.Current;
        if (app is null)
        {
            return;
        }

        // Invalidate cache so a live-edited theme file is re-read on next preview request.
        _previewCache.Remove(themeName);

        var source = ResolveSource(themeName);

        // Add the new dict BEFORE removing the old one.
        // If we remove first, there is a brief window where every DynamicResource
        // key is unresolvable and WPF emits a warning per binding.
        var incoming = new ResourceDictionary { Source = source };
        app.Resources.MergedDictionaries.Add(incoming);

        var outgoing = app.Resources.MergedDictionaries
            .Where(d => IsThemeDictionary(d) && !ReferenceEquals(d, incoming))
            .ToList();

        foreach (var d in outgoing)
        {
            app.Resources.MergedDictionaries.Remove(d);
        }
    }

    /// <summary>
    /// Returns a normalized theme name that is guaranteed to exist in the registry.
    /// Falls back to the first available theme, then "Dark".
    /// </summary>
    public static string NormalizeTheme(string? themeName)
    {
        if (themeName is not null && _registry.ContainsKey(themeName))
        {
            return themeName;
        }
        return AvailableThemes.Count > 0 ? AvailableThemes[0] : "Dark";
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static Uri ResolveSource(string themeName)
    {
        if (_registry.TryGetValue(themeName, out var entry))
        {
            return entry.Source;
        }

        // Legacy fallback: derive filename from name (e.g. "Dark" → "DarkTheme.xaml").
        return new Uri($"Themes/{themeName}Theme.xaml", UriKind.Relative);
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        if (dictionary.Source is null)
        {
            return false;
        }

        // Any file in Themes/ that ends with Theme.xaml is considered a theme dictionary.
        var src = dictionary.Source.OriginalString;
        return src.Contains("Themes/", StringComparison.OrdinalIgnoreCase)
            && src.EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase);
    }
}