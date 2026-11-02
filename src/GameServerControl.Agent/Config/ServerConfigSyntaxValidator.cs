using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Conservative syntax checks for editable text configuration files. The validator rejects
/// malformed structured documents and obvious line-oriented syntax errors without trying to
/// reinterpret game-specific semantics.
/// </summary>
public static class ServerConfigSyntaxValidator
{
    public static void Validate(string path, string content)
    {
        content ??= string.Empty;
        if (content.IndexOf('\0') >= 0)
            throw new InvalidDataException("Config text contains a NUL character.");

        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            switch (extension)
            {
                case ".json":
                    using (JsonDocument.Parse(content)) { }
                    break;
                case ".jsonc":
                    using (JsonDocument.Parse(content, new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    })) { }
                    break;
                case ".xml":
                case ".config":
                    XDocument.Parse(content, LoadOptions.PreserveWhitespace);
                    break;
                case ".toml":
                    ValidateToml(content);
                    break;
                case ".yaml":
                case ".yml":
                    ValidateYaml(content);
                    break;
                case ".ini":
                case ".cfg":
                case ".conf":
                    ValidateIniLike(content);
                    break;
                case ".properties":
                    ValidateProperties(content);
                    break;
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or XmlException)
        {
            throw new InvalidDataException($"Invalid {DisplayFormat(extension)} syntax: {ex.Message}");
        }
    }

    private static void ValidateIniLike(string content)
    {
        var lineNumber = 0;
        foreach (var raw in Lines(content))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && !line.EndsWith(']'))
                Fail("INI", lineNumber, "section header is missing a closing ']'.");
        }
    }

    private static void ValidateProperties(string content)
    {
        var continuation = false;
        var lineNumber = 0;
        foreach (var raw in Lines(content))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!'))
            {
                continuation = false;
                continue;
            }

            if (!continuation && FindUnescaped(line, '=') < 0 && FindUnescaped(line, ':') < 0 && !ContainsUnescapedWhitespace(line))
                Fail("PROPERTIES", lineNumber, "expected a key/value separator (=, : or whitespace).");

            continuation = EndsWithOddBackslash(line);
        }
    }

    private static void ValidateYaml(string content)
    {
        var lineNumber = 0;
        foreach (var raw in Lines(content))
        {
            lineNumber++;
            var prefixLength = raw.TakeWhile(char.IsWhiteSpace).Count();
            if (raw[..prefixLength].Contains('\t'))
                Fail("YAML", lineNumber, "tabs are not allowed for indentation.");

            var line = StripComment(raw).Trim();
            if (line.Length == 0 || line is "---" or "...") continue;
            if (HasUnclosedSimpleQuote(line, '\''))
                Fail("YAML", lineNumber, "single-quoted scalar is not closed.");
            if (HasUnclosedDoubleQuote(line))
                Fail("YAML", lineNumber, "double-quoted scalar is not closed.");
        }
    }

    private static void ValidateToml(string content)
    {
        var inBasicMultiline = false;
        var inLiteralMultiline = false;
        var aggregateDepth = 0;
        var lineNumber = 0;

        foreach (var raw in Lines(content))
        {
            lineNumber++;
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            var basicMarkers = CountToken(line, "\"\"\"");
            var literalMarkers = CountToken(line, "'''");
            var wasMultiline = inBasicMultiline || inLiteralMultiline;
            if ((basicMarkers & 1) == 1) inBasicMultiline = !inBasicMultiline;
            if ((literalMarkers & 1) == 1) inLiteralMultiline = !inLiteralMultiline;
            if (wasMultiline || inBasicMultiline || inLiteralMultiline) continue;

            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                if (!line.EndsWith("]]", StringComparison.Ordinal) || line.Length <= 4)
                    Fail("TOML", lineNumber, "array-table header is malformed.");
                continue;
            }
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                if (!line.EndsWith(']') || line.Length <= 2)
                    Fail("TOML", lineNumber, "table header is malformed.");
                continue;
            }

            if (aggregateDepth == 0 && FindOutsideQuotes(line, '=') < 0)
                Fail("TOML", lineNumber, "expected a key/value assignment.");

            aggregateDepth += DelimiterDelta(line, '[', ']') + DelimiterDelta(line, '{', '}');
            if (aggregateDepth < 0)
                Fail("TOML", lineNumber, "closing delimiter has no matching opening delimiter.");
        }

        if (inBasicMultiline || inLiteralMultiline)
            throw new InvalidDataException("Invalid TOML syntax: multiline string is not closed.");
        if (aggregateDepth != 0)
            throw new InvalidDataException("Invalid TOML syntax: array/inline-table delimiters are not balanced.");
    }

    private static IEnumerable<string> Lines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string StripComment(string line)
    {
        var single = false;
        var dbl = false;
        var escaped = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (dbl && ch == '\\' && !escaped) { escaped = true; continue; }
            if (ch == '\'' && !dbl) single = !single;
            else if (ch == '"' && !single && !escaped) dbl = !dbl;
            else if (ch == '#' && !single && !dbl) return line[..i];
            escaped = false;
        }
        return line;
    }

    private static bool HasUnclosedSimpleQuote(string line, char quote)
    {
        var count = 0;
        foreach (var ch in line) if (ch == quote) count++;
        return (count & 1) == 1;
    }

    private static bool HasUnclosedDoubleQuote(string line)
    {
        var open = false;
        var escaped = false;
        foreach (var ch in line)
        {
            if (ch == '\\' && !escaped) { escaped = true; continue; }
            if (ch == '"' && !escaped) open = !open;
            escaped = false;
        }
        return open;
    }

    private static int FindUnescaped(string value, char needle)
    {
        var escaped = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == needle && !escaped) return i;
            escaped = value[i] == '\\' && !escaped;
            if (value[i] != '\\') escaped = false;
        }
        return -1;
    }

    private static int FindOutsideQuotes(string value, char needle)
    {
        var single = false;
        var dbl = false;
        var escaped = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (dbl && ch == '\\' && !escaped) { escaped = true; continue; }
            if (ch == '\'' && !dbl) single = !single;
            else if (ch == '"' && !single && !escaped) dbl = !dbl;
            else if (ch == needle && !single && !dbl) return i;
            escaped = false;
        }
        return -1;
    }

    private static bool ContainsUnescapedWhitespace(string value)
    {
        var escaped = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) && !escaped) return true;
            escaped = ch == '\\' && !escaped;
            if (ch != '\\') escaped = false;
        }
        return false;
    }

    private static bool EndsWithOddBackslash(string value)
    {
        var count = 0;
        for (var i = value.Length - 1; i >= 0 && value[i] == '\\'; i--) count++;
        return (count & 1) == 1;
    }

    private static int CountToken(string value, string token)
    {
        var count = 0;
        for (var at = 0; (at = value.IndexOf(token, at, StringComparison.Ordinal)) >= 0; at += token.Length) count++;
        return count;
    }

    private static int DelimiterDelta(string value, char open, char close)
    {
        var delta = 0;
        var single = false;
        var dbl = false;
        var escaped = false;
        foreach (var ch in value)
        {
            if (dbl && ch == '\\' && !escaped) { escaped = true; continue; }
            if (ch == '\'' && !dbl) single = !single;
            else if (ch == '"' && !single && !escaped) dbl = !dbl;
            else if (!single && !dbl)
            {
                if (ch == open) delta++;
                else if (ch == close) delta--;
            }
            escaped = false;
        }
        return delta;
    }

    private static string DisplayFormat(string extension)
        => extension.TrimStart('.').ToUpperInvariant();

    private static void Fail(string format, int line, string detail)
        => throw new InvalidDataException($"Invalid {format} syntax at line {line}: {detail}");
}
