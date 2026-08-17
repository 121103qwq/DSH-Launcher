namespace DshLauncher.Models;

public enum NodeRuntimeCompatibility
{
    Missing,
    Compatible,
    Incompatible,
    Unknown
}

public sealed record NodeRuntimeInfo(
    bool IsAvailable,
    string? ExecutablePath,
    string? Version,
    string? Error)
{
    public static NodeRuntimeInfo Missing(string? error = null) =>
        new(false, null, null, error);

    public string VersionText => IsAvailable && !string.IsNullOrWhiteSpace(Version)
        ? $"v{Version}"
        : "未安装";

    public bool IsCompatibleWithDshSource => GetCompatibility(null) == NodeRuntimeCompatibility.Compatible;

    public NodeRuntimeCompatibility GetCompatibility(string? requiredEngine)
    {
        if (!IsAvailable)
        {
            return NodeRuntimeCompatibility.Missing;
        }

        if (!TryParseVersion(Version, out var parsed))
        {
            return NodeRuntimeCompatibility.Unknown;
        }

        if (string.IsNullOrWhiteSpace(requiredEngine))
        {
            // The installed package currently does not declare engines.node.
            // Do not invent a permanent version rule when the metadata is absent.
            return NodeRuntimeCompatibility.Compatible;
        }

        return SatisfiesEngine(parsed, requiredEngine)
            ? NodeRuntimeCompatibility.Compatible
            : NodeRuntimeCompatibility.Incompatible;
    }

    public static NodeRuntimeCompatibility EvaluateCompatibility(string? version, string? requiredEngine)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return NodeRuntimeCompatibility.Missing;
        }

        if (!TryParseVersion(version, out var parsed))
        {
            return NodeRuntimeCompatibility.Unknown;
        }

        if (string.IsNullOrWhiteSpace(requiredEngine))
        {
            return NodeRuntimeCompatibility.Compatible;
        }

        return SatisfiesEngine(parsed, requiredEngine)
            ? NodeRuntimeCompatibility.Compatible
            : NodeRuntimeCompatibility.Incompatible;
    }

    internal static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var numeric = new string(value.Trim().TrimStart('v', 'V')
            .TakeWhile(character => char.IsDigit(character) || character == '.')
            .ToArray());
        if (System.Version.TryParse(numeric, out var parsed))
        {
            version = parsed;
            return true;
        }

        return false;
    }

    private static bool SatisfiesEngine(Version actual, string engine)
    {
        foreach (var alternative in engine.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (SatisfiesAlternative(actual, alternative))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SatisfiesAlternative(Version actual, string alternative)
    {
        if (alternative.Trim() is "*" or "x" or "X")
        {
            return true;
        }

        var tokens = alternative
            .Replace(',', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var constraints = new List<string>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token is ">" or ">=" or "<" or "<=" or "=" or "^" or "~")
            {
                if (++index >= tokens.Count)
                {
                    return false;
                }

                token += tokens[index];
            }

            constraints.Add(token);
        }

        return constraints.Count > 0 && constraints.All(constraint => SatisfiesConstraint(actual, constraint));
    }

    private static bool SatisfiesConstraint(Version actual, string constraint)
    {
        var operation = string.Empty;
        foreach (var candidate in new[] { ">=", "<=", ">", "<", "^", "~", "=" })
        {
            if (constraint.StartsWith(candidate, StringComparison.Ordinal))
            {
                operation = candidate;
                constraint = constraint[candidate.Length..];
                break;
            }
        }

        constraint = constraint.Trim().TrimStart('v', 'V');
        var parts = constraint.Split('.', StringSplitOptions.None);
        if (parts.Length == 0 || !TryParsePart(parts[0], out var major))
        {
            return false;
        }

        var minor = 0;
        var patch = 0;
        var hasMinor = parts.Length > 1
            && !IsWildcard(parts[1])
            && TryParsePart(parts[1], out minor);
        var hasPatch = parts.Length > 2
            && !IsWildcard(parts[2])
            && TryParsePart(parts[2], out patch);
        var wildcard = parts.Any(static part => part is "*" or "x" or "X");
        var lower = new Version(major, hasMinor ? minor : 0, hasPatch ? patch : 0);

        if (string.IsNullOrEmpty(operation) && wildcard)
        {
            return actual.Major == major
                && (!hasMinor || actual.Minor == minor)
                && (!hasPatch || actual.Build == patch);
        }

        if (string.IsNullOrEmpty(operation))
        {
            return actual == lower;
        }

        return operation switch
        {
            ">=" => actual >= lower,
            "<=" => actual <= lower,
            ">" => actual > lower,
            "<" => actual < lower,
            "=" => actual == lower,
            "^" => actual >= lower && actual < GetCaretUpperBound(lower, hasMinor, hasPatch),
            "~" => actual >= lower && actual < GetTildeUpperBound(lower, hasMinor),
            _ => false
        };
    }

    private static bool TryParsePart(string value, out int number)
    {
        if (value is "*" or "x" or "X")
        {
            number = 0;
            return true;
        }

        return int.TryParse(value, out number) && number >= 0;
    }

    private static bool IsWildcard(string value) => value is "*" or "x" or "X";

    private static Version GetCaretUpperBound(Version lower, bool hasMinor, bool hasPatch)
    {
        if (lower.Major > 0)
        {
            return new Version(lower.Major + 1, 0);
        }

        if (hasMinor && lower.Minor > 0)
        {
            return new Version(0, lower.Minor + 1);
        }

        return new Version(0, lower.Minor, lower.Build + 1);
    }

    private static Version GetTildeUpperBound(Version lower, bool hasMinor)
    {
        return hasMinor
            ? new Version(lower.Major, lower.Minor + 1)
            : new Version(lower.Major + 1, 0);
    }
}
