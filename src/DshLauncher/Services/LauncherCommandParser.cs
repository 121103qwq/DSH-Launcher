using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Pure parsing for Launcher command-line arguments and the dsh-launcher URI scheme.
/// It validates syntax only; it does not inspect the file system or start anything.
/// </summary>
public static class LauncherCommandParser
{
    public const string Scheme = "dsh-launcher";

    private const int MaximumIdentifierLength = 256;
    private const int MaximumPathLength = 32_767;

    private static readonly char[] InvalidPathCharacters = Path.GetInvalidPathChars();
    private static readonly char[] InvalidFileNameCharacters = Path.GetInvalidFileNameChars();

    public static bool TryParse(
        IReadOnlyList<string>? arguments,
        out LauncherCommand? command)
    {
        command = null;
        if (arguments is null || arguments.Count == 0 || arguments.Any(argument => argument is null))
        {
            return false;
        }

        if (arguments.Count == 1 && LooksLikeUri(arguments[0]))
        {
            return TryParseUrl(arguments[0], out command);
        }

        return TryParseCommandLine(arguments, out command);
    }

    public static LauncherCommand? Parse(IReadOnlyList<string>? arguments) =>
        TryParse(arguments, out var command) ? command : null;

    public static bool TryParse(string? value, out LauncherCommand? command)
    {
        command = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return LooksLikeUri(value)
            ? TryParseUrl(value, out command)
            : TryParse(new[] { value }, out command);
    }

    public static bool TryParseUrl(string? value, out LauncherCommand? command)
    {
        command = null;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return TryParseUrl(uri, out command);
    }

    public static LauncherCommand? ParseUrl(string? value) =>
        TryParseUrl(value, out var command) ? command : null;

    public static bool TryParseUrl(Uri? uri, out LauncherCommand? command)
    {
        command = null;
        if (uri is null
            || !uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length != 0
            || uri.Port != -1
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var actionName = ReadUrlAction(uri);
        if (!TryParseAction(actionName, out var action)
            || !TryParseQuery(uri.Query, out var instanceId, out var sessionId, out var path))
        {
            return false;
        }

        return TryCreateCommand(action, instanceId, sessionId, path, out command);
    }

    private static bool TryParseCommandLine(
        IReadOnlyList<string> arguments,
        out LauncherCommand? command)
    {
        command = null;
        string? actionName = null;
        string? instanceId = null;
        string? sessionId = null;
        string? path = null;
        var index = 0;

        if (!arguments[0].StartsWith("--", StringComparison.Ordinal))
        {
            actionName = arguments[0];
            index = 1;
        }

        while (index < arguments.Count)
        {
            var argument = arguments[index];
            if (!TryReadOption(argument, out var optionName, out var inlineValue))
            {
                return false;
            }

            string? optionValue;
            if (inlineValue is not null)
            {
                optionValue = inlineValue;
            }
            else
            {
                if (++index >= arguments.Count)
                {
                    return false;
                }

                optionValue = arguments[index];
            }

            if (optionValue is null || string.IsNullOrEmpty(optionValue))
            {
                return false;
            }

            switch (optionName)
            {
                case "action":
                    if (actionName is not null)
                    {
                        return false;
                    }

                    actionName = optionValue;
                    break;
                case "instanceId":
                    if (instanceId is not null)
                    {
                        return false;
                    }

                    instanceId = optionValue;
                    break;
                case "sessionId":
                    if (sessionId is not null)
                    {
                        return false;
                    }

                    sessionId = optionValue;
                    break;
                case "path":
                    if (path is not null)
                    {
                        return false;
                    }

                    path = optionValue;
                    break;
                default:
                    return false;
            }

            index++;
        }

        return TryParseAction(actionName, out var action)
            && TryCreateCommand(action, instanceId, sessionId, path, out command);
    }

    private static bool TryParseQuery(
        string query,
        out string? instanceId,
        out string? sessionId,
        out string? path)
    {
        instanceId = null;
        sessionId = null;
        path = null;

        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        var text = query[0] == '?' ? query[1..] : query;
        if (text.Length == 0)
        {
            return true;
        }

        foreach (var pair in text.Split('&', StringSplitOptions.None))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0
                || separator == pair.Length - 1
                || !TryDecodeQueryComponent(pair[..separator], out var key)
                || !TryDecodeQueryComponent(pair[(separator + 1)..], out var value))
            {
                return false;
            }

            switch (key)
            {
                case "instanceId":
                    if (instanceId is not null)
                    {
                        return false;
                    }

                    instanceId = value;
                    break;
                case "sessionId":
                    if (sessionId is not null)
                    {
                        return false;
                    }

                    sessionId = value;
                    break;
                case "path":
                    if (path is not null)
                    {
                        return false;
                    }

                    path = value;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool TryDecodeQueryComponent(string value, out string? decoded)
    {
        decoded = null;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length
                || !IsHex(value[index + 1])
                || !IsHex(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        decoded = Uri.UnescapeDataString(value.Replace('+', ' '));
        return true;
    }

    private static string? ReadUrlAction(Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.Host))
        {
            return string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/"
                ? uri.Host
                : null;
        }

        var path = uri.AbsolutePath.Trim('/');
        return path.Length == 0 || path.Contains('/') ? null : path;
    }

    private static bool TryReadOption(
        string argument,
        out string? optionName,
        out string? inlineValue)
    {
        optionName = null;
        inlineValue = null;
        if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length <= 2)
        {
            return false;
        }

        var body = argument[2..];
        var separator = body.IndexOf('=');
        var name = separator < 0 ? body : body[..separator];
        if (!TryNormalizeOptionName(name, out optionName))
        {
            return false;
        }

        if (separator >= 0)
        {
            inlineValue = body[(separator + 1)..];
        }

        return true;
    }

    private static bool TryNormalizeOptionName(string value, out string? normalized)
    {
        normalized = value.ToLowerInvariant() switch
        {
            "action" => "action",
            "instanceid" or "instance-id" or "instance_id" => "instanceId",
            "sessionid" or "session-id" or "session_id" => "sessionId",
            "path" => "path",
            _ => null
        };

        return normalized is not null;
    }

    private static bool TryParseAction(
        string? value,
        out LauncherCommandAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        action = value.ToLowerInvariant() switch
        {
            "open" => LauncherCommandAction.Open,
            "start" => LauncherCommandAction.Start,
            "stop" => LauncherCommandAction.Stop,
            "restart" => LauncherCommandAction.Restart,
            "chat" => LauncherCommandAction.Chat,
            "version-settings" => LauncherCommandAction.VersionSettings,
            "plugins" => LauncherCommandAction.Plugins,
            "conversations" => LauncherCommandAction.Conversations,
            _ => default
        };

        return value.Equals("open", StringComparison.OrdinalIgnoreCase)
            || value.Equals("start", StringComparison.OrdinalIgnoreCase)
            || value.Equals("stop", StringComparison.OrdinalIgnoreCase)
            || value.Equals("restart", StringComparison.OrdinalIgnoreCase)
            || value.Equals("chat", StringComparison.OrdinalIgnoreCase)
            || value.Equals("version-settings", StringComparison.OrdinalIgnoreCase)
            || value.Equals("plugins", StringComparison.OrdinalIgnoreCase)
            || value.Equals("conversations", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateCommand(
        LauncherCommandAction action,
        string? instanceId,
        string? sessionId,
        string? path,
        out LauncherCommand? command)
    {
        command = null;
        if ((instanceId is not null && !IsSafeIdentifier(instanceId))
            || (sessionId is not null && !IsSafeIdentifier(sessionId))
            || (path is not null && !IsSafePath(path)))
        {
            return false;
        }

        command = new LauncherCommand(action, instanceId, sessionId, path);
        return true;
    }

    private static bool IsSafeIdentifier(string value)
    {
        if (value.Length == 0
            || value.Length > MaximumIdentifierLength
            || value.Any(char.IsWhiteSpace)
            || value.Any(char.IsControl))
        {
            return false;
        }

        return value.All(character => character is not ('/' or '\\' or ':' or '?' or '#'
            or '&' or '=' or '*' or '"' or '<' or '>' or '|'));
    }

    private static bool IsSafePath(string value)
    {
        if (value.Length == 0
            || value.Length > MaximumPathLength
            || string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || value.Any(character => InvalidPathCharacters.Contains(character)))
        {
            return false;
        }

        var normalized = value.Replace('/', '\\');
        if (normalized.StartsWith("\\\\", StringComparison.Ordinal)
            || normalized.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || normalized.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || (normalized.Length > 0 && normalized[0] == '\\'))
        {
            return false;
        }

        var hasDrive = normalized.Length >= 3
            && char.IsAsciiLetter(normalized[0])
            && normalized[1] == ':'
            && normalized[2] == '\\';
        if (normalized.Contains(':') && !hasDrive)
        {
            return false;
        }

        var segments = normalized.Split('\\', StringSplitOptions.None);
        var segmentStart = hasDrive ? 1 : 0;
        var lastSegment = segments.Length - 1;
        if (normalized.EndsWith('\\'))
        {
            lastSegment--;
        }

        if (lastSegment < segmentStart)
        {
            return false;
        }

        for (var index = segmentStart; index <= lastSegment; index++)
        {
            var segment = segments[index];
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.EndsWith(' ') || segment.EndsWith('.')
                || segment.Any(character => InvalidFileNameCharacters.Contains(character))
                || IsReservedDeviceName(segment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var name = segment.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (name.Length == 4
                && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && name[3] is >= '1' and <= '9');
    }

    private static bool LooksLikeUri(string value) =>
        value.Contains("://", StringComparison.Ordinal)
        || value.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)
        || (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme.Length > 0);

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';
}
