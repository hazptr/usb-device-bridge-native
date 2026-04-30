using System.Text.RegularExpressions;

namespace UsbDeviceBridge.Service.Interop;

public enum WslDistroRuntimeState
{
    Unknown,
    Running,
    Stopped,
    Installing,
    Uninstalling,
}

public readonly record struct WslDistroListEntry(string Name, WslDistroRuntimeState RuntimeState);

public static partial class WslDistroParser
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public static IReadOnlyList<WslDistroListEntry> ParseVerbose(string stdout)
    {
        // wsl.exe --list outputs UTF-16 LE with a BOM. When read without BOM detection
        // the U+FEFF character lands at the start of the decoded string. Strip it once.
        stdout = stdout.TrimStart('\uFEFF');

        var entries = new List<WslDistroListEntry>();

        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase))
                continue;

            line = NormalizeLeadingDefaultMarker(line);
            var columns = MultiSpaceSeparatorRegex().Split(line);
            if (columns.Length == 0)
                continue;

            var name = columns[0].Trim();
            if (name.Length == 0)
                continue;

            var state = columns.Length > 1 ? ParseState(columns[1]) : WslDistroRuntimeState.Unknown;
            entries.Add(new WslDistroListEntry(name, state));
        }

        return entries;
    }

    public static IReadOnlyList<string> ParseQuiet(string stdout)
    {
        // wsl.exe --list outputs UTF-16 LE with a BOM. When read without BOM detection
        // the U+FEFF character lands at the start of the decoded string. Strip it once.
        stdout = stdout.TrimStart('\uFEFF');

        var names = new List<string>();

        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = NormalizeLeadingDefaultMarker(rawLine.Trim());
            if (name.Length > 0)
                names.Add(name);
        }

        return names;
    }

    public static IReadOnlyList<string> BuildSelectableDistros(
        IReadOnlyList<string> validInstalledDistros,
        IReadOnlyList<string> runningDistros
    )
    {
        var result = new List<string>();
        var seen = new HashSet<string>(NameComparer);

        foreach (var distro in runningDistros)
        {
            if (seen.Add(distro))
                result.Add(distro);
        }

        foreach (var distro in validInstalledDistros)
        {
            if (seen.Add(distro))
                result.Add(distro);
        }

        return result;
    }

    private static string NormalizeLeadingDefaultMarker(string value)
    {
        if (value.StartsWith("*", StringComparison.Ordinal))
            return value[1..].TrimStart();

        return value;
    }

    private static WslDistroRuntimeState ParseState(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "running" => WslDistroRuntimeState.Running,
            "stopped" => WslDistroRuntimeState.Stopped,
            "installing" => WslDistroRuntimeState.Installing,
            "uninstalling" => WslDistroRuntimeState.Uninstalling,
            _ => WslDistroRuntimeState.Unknown,
        };
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceSeparatorRegex();
}