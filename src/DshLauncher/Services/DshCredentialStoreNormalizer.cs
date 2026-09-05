using System.Text.RegularExpressions;

namespace DshLauncher.Services;

internal static partial class DshCredentialStoreNormalizer
{
    public static string NormalizeForCurrentDsh(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewline = text.EndsWith('\n');
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new List<string>(lines.Length);
        var sawVersion = false;
        var sawRefs = false;
        var sawCredential = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimStart('\uFEFF');
            if (!sawRefs)
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                {
                    output.Add(line);
                    continue;
                }

                if (!sawVersion && LegacyVersionLine().IsMatch(line))
                {
                    sawVersion = true;
                    continue;
                }

                if (sawVersion && LegacyRefsLine().IsMatch(line))
                {
                    sawRefs = true;
                    continue;
                }

                return text;
            }

            if (line.Length == 0)
            {
                output.Add(line);
                continue;
            }

            if (!line.StartsWith("  ", StringComparison.Ordinal))
            {
                return text;
            }

            var flattened = line[2..];
            if (CredentialLine().IsMatch(flattened))
            {
                sawCredential = true;
            }

            output.Add(flattened);
        }

        if (!sawVersion || !sawRefs || !sawCredential)
        {
            return text;
        }

        while (output.Count > 0 && output[^1].Length == 0)
        {
            output.RemoveAt(output.Count - 1);
        }

        var normalized = string.Join(newline, output);
        return endsWithNewline ? normalized + newline : normalized;
    }

    [GeneratedRegex("""^version\s*:\s*['"]?1['"]?\s*(?:#.*)?$""", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyVersionLine();

    [GeneratedRegex(@"^refs\s*:\s*(?:#.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyRefsLine();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]*\s*:\s*\S", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialLine();
}
