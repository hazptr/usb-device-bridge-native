using System.Text.RegularExpressions;

namespace UsbDeviceBridge.Service.Interop;

public static partial class VersionTextParser
{
    private const string UnknownVersion = "Unknown";

    public static string ParseWslVersion(string? output)
    {
        var firstLine = FirstNonEmptyLine(output);
        if (firstLine.Length == 0)
            return UnknownVersion;

        var match = SemanticVersionRegex().Match(firstLine);
        if (match.Success)
            return match.Value;

        var colonIndex = firstLine.IndexOf(':');
        if (colonIndex >= 0 && colonIndex + 1 < firstLine.Length)
        {
            var candidate = firstLine[(colonIndex + 1)..].Trim();
            if (candidate.Length > 0)
                return candidate;
        }

        return firstLine;
    }

    public static string ParseUsbIpdVersion(string? output)
    {
        var firstLine = FirstNonEmptyLine(output);
        if (firstLine.Length == 0)
            return UnknownVersion;

        var match = SemanticVersionRegex().Match(firstLine);
        return match.Success ? match.Value : firstLine;
    }

    private static string FirstNonEmptyLine(string? output)
        => output?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0)
            ?? string.Empty;

    [GeneratedRegex(@"\d+(?:\.\d+){1,4}")]
    private static partial Regex SemanticVersionRegex();
}