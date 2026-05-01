using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UsbDeviceBridge.App.Services;

public sealed partial class SshConfigParser
{
    private readonly string _rootConfigPath;

    public SshConfigParser(string? rootConfigPath = null)
    {
        _rootConfigPath = rootConfigPath ?? BuildDefaultConfigPath();
    }

    public string RootConfigPath => _rootConfigPath;

    public IReadOnlyList<string> GetHostAliases()
    {
        var discovered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var host in ParseFile(_rootConfigPath, visited))
        {
            if (seen.Add(host))
                discovered.Add(host);
        }

        return discovered;
    }

    public static bool IsValidAdHocHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();

        if (normalized.Contains(' '))
            return false;

        if (normalized.StartsWith("-", StringComparison.Ordinal))
            return false;

        return HostPattern().IsMatch(normalized);
    }

    private IEnumerable<string> ParseFile(string path, HashSet<string> visited)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            yield break;

        var fullPath = Path.GetFullPath(path);
        if (!visited.Add(fullPath))
            yield break;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(fullPath, Encoding.UTF8);
        }
        catch
        {
            yield break;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = StripComments(lines[index]);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var tokens = Tokenize(line);
            if (tokens.Count == 0)
                continue;

            if (tokens[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var host in ParseHostLine(tokens))
                    yield return host;

                continue;
            }

            if (tokens[0].Equals("Include", StringComparison.OrdinalIgnoreCase))
            {
                var includes = ExpandIncludes(tokens.Skip(1), Path.GetDirectoryName(fullPath) ?? string.Empty);
                foreach (var include in includes)
                {
                    foreach (var nestedHost in ParseFile(include, visited))
                        yield return nestedHost;
                }
            }
        }
    }

    private static IEnumerable<string> ParseHostLine(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2)
            yield break;

        var aliases = tokens
            .Skip(1)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToArray();

        if (aliases.Length == 0)
            yield break;

        // Do not surface wildcard-only entries in the selection UI.
        if (aliases.All(IsWildcardToken))
            yield break;

        foreach (var alias in aliases)
        {
            if (IsWildcardToken(alias))
                continue;

            yield return alias;
        }
    }

    private static IEnumerable<string> ExpandIncludes(IEnumerable<string> includeTokens, string baseDir)
    {
        foreach (var token in includeTokens)
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;

            var expanded = ExpandPath(token.Trim(), baseDir);
            var fileName = Path.GetFileName(expanded);
            var dirName = Path.GetDirectoryName(expanded);
            if (string.IsNullOrWhiteSpace(dirName) || string.IsNullOrWhiteSpace(fileName))
            {
                if (File.Exists(expanded))
                    yield return expanded;

                continue;
            }

            if (fileName.Contains('*') || fileName.Contains('?'))
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(dirName, fileName, SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    yield return file;
            }
            else if (File.Exists(expanded))
            {
                yield return expanded;
            }
        }
    }

    private static string ExpandPath(string value, string baseDir)
    {
        var path = Environment.ExpandEnvironmentVariables(value);

        if (path.StartsWith("~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, path.TrimStart('~', '/', '\\'));
        }

        if (!Path.IsPathRooted(path))
            path = Path.Combine(baseDir, path);

        return Path.GetFullPath(path);
    }

    private static string StripComments(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        var index = line.IndexOf('#');
        if (index < 0)
            return line.Trim();

        return line[..index].Trim();
    }

    private static List<string> Tokenize(string line)
        => line
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();

    private static bool IsWildcardToken(string token)
        => token.Contains('*') || token.Contains('?');

    private static string BuildDefaultConfigPath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".ssh", "config");
    }

    [GeneratedRegex("^[A-Za-z0-9._:@-]+$")]
    private static partial Regex HostPattern();
}
