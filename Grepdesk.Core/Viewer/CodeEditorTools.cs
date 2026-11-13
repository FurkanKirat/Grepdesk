using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Grepdesk.Core.Viewer;

public readonly record struct TextSpan(int Offset, int Length);

public readonly record struct FoldSpan(int StartOffset, int EndOffset, string Title);

public sealed record CodeSymbol(string Name, string Kind, int Line, int Offset);

public sealed record FormatResult(bool Success, string? FormattedText, string? ErrorMessage, int ErrorLine);

/// <summary>
/// Search, replace, and Go-to-Line helpers for the code/text viewer.
/// </summary>
public static class CodeEditorSearch
{
    public const int DefaultMaxMatches = 10_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public static IReadOnlyList<TextSpan> FindMatches(
        string text,
        string query,
        bool matchCase,
        bool wholeWord,
        bool useRegex,
        out bool validRegex) =>
        FindMatches(text, query, matchCase, wholeWord, useRegex, DefaultMaxMatches, out validRegex);

    internal static IReadOnlyList<TextSpan> FindMatches(
        string text,
        string query,
        bool matchCase,
        bool wholeWord,
        bool useRegex,
        int maxMatches,
        out bool validRegex)
    {
        validRegex = true;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
            return [];

        if (!useRegex)
            return FindLiteralMatches(text, query, matchCase, wholeWord, maxMatches);

        var pattern = wholeWord ? $"(?<!\\w)(?:{query})(?!\\w)" : query;
        var options = matchCase ? RegexOptions.Multiline : RegexOptions.IgnoreCase | RegexOptions.Multiline;

        Regex regex;
        try
        {
            regex = new Regex(pattern, options, RegexTimeout);
        }
        catch (ArgumentException)
        {
            validRegex = false;
            return [];
        }

        var results = new List<TextSpan>();
        try
        {
            foreach (Match match in regex.Matches(text))
            {
                if (match.Length == 0) continue;
                results.Add(new TextSpan(match.Index, match.Length));
                if (results.Count >= maxMatches) break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Return whatever matched before timeout so large regexes stay responsive.
        }

        return results;
    }

    private static List<TextSpan> FindLiteralMatches(
        string text,
        string query,
        bool matchCase,
        bool wholeWord,
        int maxMatches)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var results = new List<TextSpan>();
        var index = 0;

        while (index <= text.Length - query.Length)
        {
            var found = text.IndexOf(query, index, comparison);
            if (found < 0) break;

            if (!wholeWord || IsWholeWord(text, found, query.Length))
            {
                results.Add(new TextSpan(found, query.Length));
                if (results.Count >= maxMatches) break;
            }

            index = found + Math.Max(1, query.Length);
        }

        return results;
    }

    private static bool IsWholeWord(string text, int start, int length)
    {
        var leftOk = start == 0 || !IsWordChar(text[start - 1]);
        var right = start + length;
        var rightOk = right >= text.Length || !IsWordChar(text[right]);
        return leftOk && rightOk;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Returns the index of the next (or previous) match relative to <paramref name="caretOffset"/>,
    /// wrapping around at the ends of the document. Returns -1 when there are no matches.
    /// </summary>
    public static int FindMatchIndex(IReadOnlyList<TextSpan> matches, int caretOffset, bool backward)
    {
        if (matches.Count == 0) return -1;

        if (!backward)
        {
            for (var i = 0; i < matches.Count; i++)
            {
                if (matches[i].Offset >= caretOffset) return i;
            }
            return 0;
        }

        for (var i = matches.Count - 1; i >= 0; i--)
        {
            if (matches[i].Offset < caretOffset) return i;
        }
        return matches.Count - 1;
    }

    public static string ReplaceSingle(
        string matchText,
        string query,
        string replacement,
        bool matchCase,
        bool useRegex)
    {
        if (!useRegex || string.IsNullOrEmpty(query)) return replacement;

        var options = matchCase ? RegexOptions.Multiline : RegexOptions.IgnoreCase | RegexOptions.Multiline;
        try
        {
            var regex = new Regex(query, options, RegexTimeout);
            return regex.Replace(matchText, replacement, 1);
        }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
        {
            return replacement;
        }
    }

    public static string ReplaceAll(
        string text,
        string query,
        string replacement,
        bool matchCase,
        bool wholeWord,
        bool useRegex,
        out int count)
    {
        var matches = FindMatches(text, query, matchCase, wholeWord, useRegex, int.MaxValue, out var validRegex);
        if (!validRegex || matches.Count == 0)
        {
            count = 0;
            return text;
        }

        Regex? regex = null;
        if (useRegex)
        {
            var options = matchCase ? RegexOptions.Multiline : RegexOptions.IgnoreCase | RegexOptions.Multiline;
            regex = new Regex(query, options, RegexTimeout);
        }

        var sb = new StringBuilder(text.Length);
        var pos = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            var span = matches[i];
            sb.Append(text, pos, span.Offset - pos);
            if (regex is not null)
            {
                var slice = text.Substring(span.Offset, span.Length);
                try
                {
                    sb.Append(regex.Replace(slice, replacement, 1));
                }
                catch (RegexMatchTimeoutException)
                {
                    sb.Append(replacement);
                }
            }
            else
            {
                sb.Append(replacement);
            }
            pos = span.Offset + span.Length;
        }
        sb.Append(text, pos, text.Length - pos);
        count = matches.Count;
        return sb.ToString();
    }

    /// <summary>
    /// Parses line or line:column specifications (e.g. "42" or "42:10"),
    /// clamping the line into 1..<paramref name="totalLines"/>.
    /// </summary>
    public static bool TryParseGoToLine(string input, int totalLines, out int line, out int column)
    {
        line = 1;
        column = 1;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        var colon = trimmed.IndexOf(':');
        if (colon < 0)
        {
            if (!int.TryParse(trimmed, out var parsedLine)) return false;
            line = Math.Clamp(parsedLine, 1, Math.Max(1, totalLines));
            return true;
        }

        var linePart = trimmed[..colon].Trim();
        var colPart = trimmed[(colon + 1)..].Trim();
        if (!int.TryParse(linePart, out var l)) return false;
        if (colPart.Length > 0)
        {
            if (!int.TryParse(colPart, out var c)) return false;
            column = Math.Max(1, c);
        }

        line = Math.Clamp(l, 1, Math.Max(1, totalLines));
        return true;
    }
}

/// <summary>
/// Finds matching (), {}, [] pairs around the caret while ignoring strings and comments.
/// </summary>
public static class BracketMatcher
{
    private const int MaxScanChars = 50_000;

    public static (int OpenOffset, int CloseOffset)? FindMatchingBracket(string text, int caretOffset)
    {
        if (string.IsNullOrEmpty(text)) return null;

        if (caretOffset >= 0 && caretOffset < text.Length && IsBracket(text[caretOffset]))
            return MatchAt(text, caretOffset);

        if (caretOffset > 0 && caretOffset - 1 < text.Length && IsBracket(text[caretOffset - 1]))
            return MatchAt(text, caretOffset - 1);

        return null;
    }

    private static (int OpenOffset, int CloseOffset)? MatchAt(string text, int pos)
    {
        var ch = text[pos];
        if (IsOpen(ch))
        {
            var close = ClosingOf(ch);
            var endLimit = Math.Min(text.Length, pos + MaxScanChars);
            var depth = 1;

            for (var i = pos + 1; i < endLimit; i++)
            {
                var c = text[i];
                if (c is '"' or '\'')
                {
                    i = SkipQuoted(text, i, c, endLimit);
                    continue;
                }
                if (c == '/' && i + 1 < endLimit)
                {
                    if (text[i + 1] == '/')
                    {
                        i = SkipLineComment(text, i + 2, endLimit);
                        continue;
                    }
                    if (text[i + 1] == '*')
                    {
                        i = SkipBlockComment(text, i + 2, endLimit);
                        continue;
                    }
                }

                if (c == ch) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return (pos, i);
                }
            }
            return null;
        }
        else
        {
            var open = OpeningOf(ch);
            var start = Math.Max(0, pos - MaxScanChars);
            var stack = new Stack<int>();

            for (var i = start; i < pos; i++)
            {
                var c = text[i];
                if (c is '"' or '\'')
                {
                    i = SkipQuoted(text, i, c, pos);
                    continue;
                }
                if (c == '/' && i + 1 < pos)
                {
                    if (text[i + 1] == '/')
                    {
                        i = SkipLineComment(text, i + 2, pos);
                        continue;
                    }
                    if (text[i + 1] == '*')
                    {
                        i = SkipBlockComment(text, i + 2, pos);
                        continue;
                    }
                }

                if (c == open) stack.Push(i);
                else if (c == ch && stack.Count > 0) stack.Pop();
            }

            return stack.Count > 0 ? (stack.Pop(), pos) : null;
        }
    }

    private static int SkipQuoted(string text, int start, char quote, int limit)
    {
        for (var i = start + 1; i < limit; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (c == quote) return i;
            if (c == '\n') return i;
        }
        return limit - 1;
    }

    private static int SkipLineComment(string text, int start, int limit)
    {
        for (var i = start; i < limit; i++)
        {
            if (text[i] == '\n') return i;
        }
        return limit - 1;
    }

    private static int SkipBlockComment(string text, int start, int limit)
    {
        for (var i = start; i + 1 < limit; i++)
        {
            if (text[i] == '*' && text[i + 1] == '/') return i + 1;
        }
        return limit - 1;
    }

    private static bool IsBracket(char c) => c is '(' or ')' or '{' or '}' or '[' or ']';
    private static bool IsOpen(char c) => c is '(' or '{' or '[';
    private static char ClosingOf(char open) => open switch { '(' => ')', '{' => '}', _ => ']' };
    private static char OpeningOf(char close) => close switch { ')' => '(', '}' => '{', _ => '[' };
}

/// <summary>
/// Computes collapsible regions for brace-based code, #region blocks, XML/AXAML tags, and Markdown headings.
/// </summary>
public static class CodeFolder
{
    private static readonly HashSet<string> XmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".axaml", ".xaml", ".csproj", ".props", ".targets", ".config", ".html", ".htm", ".svg", ".iss"
    };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd"
    };

    public static IReadOnlyList<FoldSpan> GetFoldings(string text, string? extension)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var spans = new List<FoldSpan>();
        if (MarkdownExtensions.Contains(extension ?? ""))
            CollectMarkdownFoldings(text, spans);
        else if (XmlExtensions.Contains(extension ?? "") && !string.Equals(extension, ".iss", StringComparison.OrdinalIgnoreCase))
            CollectXmlFoldings(text, spans);
        else
            CollectBraceAndRegionFoldings(text, spans);

        if (spans.Count == 0) return [];

        spans.Sort((a, b) =>
        {
            var cmp = a.StartOffset.CompareTo(b.StartOffset);
            return cmp != 0 ? cmp : b.EndOffset.CompareTo(a.EndOffset);
        });

        var deduped = new List<FoldSpan>(spans.Count);
        for (var i = 0; i < spans.Count; i++)
        {
            var cur = spans[i];
            if (cur.EndOffset <= cur.StartOffset || cur.EndOffset > text.Length) continue;
            if (deduped.Count > 0 && deduped[^1].StartOffset == cur.StartOffset && deduped[^1].EndOffset == cur.EndOffset)
                continue;
            deduped.Add(cur);
        }

        return deduped;
    }

    private static void CollectBraceAndRegionFoldings(string text, List<FoldSpan> spans)
    {
        var braceStack = new Stack<(char Kind, int Offset, int Line)>();
        var regionStack = new Stack<(int Offset, string Title)>();

        var line = 1;
        var lineStart = 0;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (i == lineStart)
            {
                var lineEnd = FindLineEnd(text, i);
                var trimmed = text.AsSpan(i, lineEnd - i).TrimStart();
                if (trimmed.StartsWith("#region", StringComparison.OrdinalIgnoreCase)
                    && (trimmed.Length == 7 || char.IsWhiteSpace(trimmed[7])))
                {
                    var title = trimmed[7..].Trim().ToString();
                    regionStack.Push((i, title.Length > 0 ? title : "#region"));
                }
                else if (trimmed.StartsWith("#endregion", StringComparison.OrdinalIgnoreCase)
                         && (trimmed.Length == 10 || char.IsWhiteSpace(trimmed[10]))
                         && regionStack.Count > 0)
                {
                    var (start, title) = regionStack.Pop();
                    if (lineEnd > start)
                        spans.Add(new FoldSpan(start, lineEnd, title));
                }
            }

            if (c == '\n')
            {
                line++;
                lineStart = i + 1;
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                i = SkipString(text, i, c, ref line, ref lineStart);
                continue;
            }

            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    i = FindLineEnd(text, i + 2);
                    continue;
                }
                if (text[i + 1] == '*')
                {
                    i = SkipBlockComment(text, i + 2, ref line, ref lineStart);
                    continue;
                }
            }

            if (c is '{' or '[')
            {
                braceStack.Push((c, i, line));
            }
            else if (c is '}' or ']')
            {
                var expected = c == '}' ? '{' : '[';
                if (braceStack.Count > 0 && braceStack.Peek().Kind == expected)
                {
                    var (_, openOffset, openLine) = braceStack.Pop();
                    if (line > openLine)
                        spans.Add(new FoldSpan(openOffset, i + 1, expected == '{' ? "{...}" : "[...]"));
                }
            }

            i++;
        }
    }

    private static void CollectXmlFoldings(string text, List<FoldSpan> spans)
    {
        var tagStack = new Stack<(string Name, int StartOffset, int StartLine)>();
        var line = 1;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            if (c != '<')
            {
                i++;
                continue;
            }

            // XML comment <!-- ... -->
            if (i + 3 < text.Length && text.AsSpan(i, 4).SequenceEqual("<!--"))
            {
                var commentStart = i;
                var commentLine = line;
                var closeIdx = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (closeIdx < 0) break;

                for (var k = i + 4; k < closeIdx; k++)
                {
                    if (text[k] == '\n') line++;
                }
                var end = closeIdx + 3;
                if (line > commentLine)
                    spans.Add(new FoldSpan(commentStart, end, "<!-- ... -->"));
                i = end;
                continue;
            }

            // Processing instruction / CDATA / DOCTYPE
            if (i + 1 < text.Length && text[i + 1] is '?' or '!')
            {
                var gt = text.IndexOf('>', i + 2);
                if (gt < 0) break;
                for (var k = i + 2; k < gt; k++)
                {
                    if (text[k] == '\n') line++;
                }
                i = gt + 1;
                continue;
            }

            var tagStart = i;
            var tagStartLine = line;
            var isClosing = i + 1 < text.Length && text[i + 1] == '/';
            var nameStart = isClosing ? i + 2 : i + 1;
            var nameEnd = nameStart;
            while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] is not '>' and not '/')
                nameEnd++;

            if (nameEnd == nameStart)
            {
                i++;
                continue;
            }

            var tagName = text[nameStart..nameEnd];
            var selfClosing = false;
            var quote = '\0';
            var j = nameEnd;
            while (j < text.Length)
            {
                var ch = text[j];
                if (ch == '\n') line++;
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                }
                else
                {
                    if (ch is '"' or '\'') quote = ch;
                    else if (ch == '>')
                    {
                        selfClosing = j > nameEnd && text[j - 1] == '/';
                        break;
                    }
                }
                j++;
            }

            if (j >= text.Length) break;

            if (isClosing)
            {
                while (tagStack.Count > 0)
                {
                    var top = tagStack.Pop();
                    if (string.Equals(top.Name, tagName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (line > top.StartLine)
                            spans.Add(new FoldSpan(top.StartOffset, j + 1, $"<{top.Name}>"));
                        break;
                    }
                }
            }
            else if (!selfClosing)
            {
                tagStack.Push((tagName, tagStart, tagStartLine));
            }

            i = j + 1;
        }
    }

    private static void CollectMarkdownFoldings(string text, List<FoldSpan> spans)
    {
        var openHeadings = new Stack<(int Level, int HeaderEndOffset, string Title)>();
        var inFence = false;
        var fenceChar = '\0';
        var prevLineEnd = 0;

        var pos = 0;
        while (pos < text.Length)
        {
            var lineEnd = FindLineEnd(text, pos);
            var span = text.AsSpan(pos, lineEnd - pos).TrimEnd('\r');
            var trimmed = span.TrimStart();

            if (trimmed.Length >= 3 && ((trimmed[0] == '`' && trimmed[1] == '`' && trimmed[2] == '`')
                                        || (trimmed[0] == '~' && trimmed[1] == '~' && trimmed[2] == '~')))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = trimmed[0];
                }
                else if (trimmed[0] == fenceChar)
                {
                    inFence = false;
                }
            }
            else if (!inFence && trimmed.Length > 0 && trimmed[0] == '#')
            {
                var level = 0;
                while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
                if (level > 0 && level < trimmed.Length && char.IsWhiteSpace(trimmed[level]))
                {
                    var title = trimmed[level..].Trim().TrimEnd('#').Trim().ToString();
                    while (openHeadings.Count > 0 && openHeadings.Peek().Level >= level)
                    {
                        var closed = openHeadings.Pop();
                        if (prevLineEnd > closed.HeaderEndOffset)
                            spans.Add(new FoldSpan(closed.HeaderEndOffset, prevLineEnd, closed.Title));
                    }
                    openHeadings.Push((level, lineEnd, title));
                }
            }

            prevLineEnd = lineEnd;
            pos = lineEnd < text.Length && text[lineEnd] == '\n' ? lineEnd + 1 : lineEnd;
        }

        while (openHeadings.Count > 0)
        {
            var closed = openHeadings.Pop();
            if (text.Length > closed.HeaderEndOffset)
                spans.Add(new FoldSpan(closed.HeaderEndOffset, text.Length, closed.Title));
        }
    }

    private static int FindLineEnd(string text, int start)
    {
        var nl = text.IndexOf('\n', start);
        return nl < 0 ? text.Length : nl;
    }

    private static int SkipString(string text, int start, char quote, ref int line, ref int lineStart)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (c == '\n')
            {
                line++;
                lineStart = i + 1;
                return i + 1;
            }
            if (c == quote) return i + 1;
        }
        return text.Length;
    }

    private static int SkipBlockComment(string text, int start, ref int line, ref int lineStart)
    {
        for (var i = start; i + 1 < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
            else if (text[i] == '*' && text[i + 1] == '/')
            {
                return i + 2;
            }
        }
        return text.Length;
    }
}

/// <summary>
/// Extracts outline symbols (types, methods, headings, keys, sections) and resolves the enclosing symbol at a line.
/// </summary>
public static class CodeSymbolExtractor
{
    public const int MaxSymbols = 500;
    public const int MaxTextLength = 4_000_000;

    private static readonly Regex CStyleTypeRegex = new(
        @"^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|ref|unsafe|export|default|declare)\s+)*(?<kind>namespace|class|struct|interface|enum|record|trait|module)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex CStyleMethodRegex = new(
        @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|unsafe|new|partial|readonly|export|inline|constexpr|suspend)\s+)*(?:function\s+|fn\s+|func\s+(?:\([^)]*\)\s+)?|(?:[A-Za-z_][A-Za-z0-9_<>\[\],\?\.\*\s&]*?)\s+)(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]+>)?\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex PythonRegex = new(
        @"^\s*(?:async\s+)?(?<kind>class|def)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex SqlRegex = new(
        @"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?<kind>TABLE|VIEW|PROCEDURE|PROC|FUNCTION|INDEX|TRIGGER)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>[A-Za-z_\[""`][A-Za-z0-9_\.\[\]""`]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex JsonKeyRegex = new(
        @"^(?<indent>\s*)""(?<name>[^""\\]{1,80})""\s*:",
        RegexOptions.Compiled);

    private static readonly Regex IniSectionRegex = new(
        @"^\s*\[(?<name>[^\]\r\n]{1,80})\]\s*(?:[#;].*)?$",
        RegexOptions.Compiled);

    private static readonly Regex XmlNamedElementRegex = new(
        @"^\s*<(?<tag>[A-Za-z_][A-Za-z0-9_:\.-]*)\b[^>]*(?:x:Name|Name|id)\s*=\s*[""'](?<name>[^""']+)[""']",
        RegexOptions.Compiled);

    private static readonly Regex XmlMajorElementRegex = new(
        @"^\s*<(?<tag>Project|PropertyGroup|ItemGroup|Target|Window|UserControl|Grid|DockPanel|StackPanel)\b(?:\s+[^>]*?Name\s*=\s*[""'](?<name>[^""']+)[""'])?",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "catch", "using",
        "lock", "fixed", "return", "throw", "sizeof", "typeof", "nameof", "new", "when", "where"
    };

    public static IReadOnlyList<CodeSymbol> ExtractSymbols(string text, string? extension)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength) return [];

        var ext = (extension ?? "").ToLowerInvariant();
        var symbols = new List<CodeSymbol>();

        if (ext is ".md" or ".markdown" or ".mdown" or ".mkd")
            ExtractMarkdown(text, symbols);
        else if (ext is ".json" or ".jsonc")
            ExtractJson(text, symbols);
        else if (ext is ".xml" or ".axaml" or ".xaml" or ".csproj" or ".props" or ".targets" or ".config" or ".svg" or ".html" or ".htm")
            ExtractXml(text, symbols);
        else if (ext is ".ini" or ".cfg" or ".conf" or ".toml")
            ExtractIni(text, symbols);
        else if (ext is ".py")
            ExtractPython(text, symbols);
        else if (ext is ".sql")
            ExtractSql(text, symbols);
        else
            ExtractCode(text, symbols);

        return symbols;
    }

    public static CodeSymbol? FindEnclosingSymbol(IReadOnlyList<CodeSymbol> symbols, int line)
    {
        if (symbols.Count == 0) return null;

        CodeSymbol? match = null;
        for (var i = 0; i < symbols.Count; i++)
        {
            if (symbols[i].Line > line) break;
            match = symbols[i];
        }
        return match;
    }

    private static void ExtractMarkdown(string text, List<CodeSymbol> symbols)
    {
        var inFence = false;
        var fenceChar = '\0';
        ForEachLine(text, (lineText, lineNumber, offset) =>
        {
            var trimmed = lineText.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                if (!inFence) { inFence = true; fenceChar = trimmed[0]; }
                else if (trimmed[0] == fenceChar) inFence = false;
                return true;
            }
            if (inFence || !trimmed.StartsWith('#')) return true;

            var level = 0;
            while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
            if (level > 0 && level < trimmed.Length && char.IsWhiteSpace(trimmed[level]))
            {
                var title = trimmed[level..].Trim().TrimEnd('#').Trim();
                if (title.Length > 0)
                    symbols.Add(new CodeSymbol(title, $"H{level}", lineNumber, offset));
            }
            return symbols.Count < MaxSymbols;
        });
    }

    private static void ExtractJson(string text, List<CodeSymbol> symbols)
    {
        ForEachLine(text, (lineText, lineNumber, offset) =>
        {
            var m = JsonKeyRegex.Match(lineText);
            if (!m.Success) return true;
            var indent = m.Groups["indent"].Value.Length;
            if (indent <= 4)
            {
                symbols.Add(new CodeSymbol(m.Groups["name"].Value, "Key", lineNumber, offset));
            }
            return symbols.Count < MaxSymbols;
        });
    }

    private static void ExtractXml(string text, List<CodeSymbol> symbols)
    {
        ForEachLine(text, (lineText, lineNumber, offset) =>
        {
            var m = XmlNamedElementRegex.Match(lineText);
            if (m.Success)
            {
                symbols.Add(new CodeSymbol(m.Groups["name"].Value, m.Groups["tag"].Value, lineNumber, offset));
                return symbols.Count < MaxSymbols;
            }

            var major = XmlMajorElementRegex.Match(lineText);
            if (major.Success)
            {
                var tag = major.Groups["tag"].Value;
                var name = major.Groups["name"].Success ? major.Groups["name"].Value : tag;
                symbols.Add(new CodeSymbol(name, tag, lineNumber, offset));
            }
            return symbols.Count < MaxSymbols;
        });
    }

    private static void ExtractIni(string text, List<CodeSymbol> symbols)
    {
        ForEachLine(text, (lineText, lineNumber, offset) =>
        {
            var m = IniSectionRegex.Match(lineText);
            if (m.Success)
                symbols.Add(new CodeSymbol(m.Groups["name"].Value, "Section", lineNumber, offset));
            return symbols.Count < MaxSymbols;
        });
    }

    private static void ExtractPython(string text, List<CodeSymbol> symbols)
    {
        ForEachLine(text, (lineText, lineNumber, offset) =>
        {
            var m = PythonRegex.Match(lineText);
            if (m.Success)
            {
                var kind = m.Groups["kind"].Value == "class" ? "class" : "def";
                symbols.Add(new CodeSymbol(m.Groups["name"].Value, kind, lineNumber, offset));
            }
            return symbols.Count < MaxSymbols;
        });
    }

    private static void ExtractSql(string text, List<CodeSymbol> symbols)
    {
        ForEachLine(text, (lineText, lineNumber, offset) =>
        {
            var m = SqlRegex.Match(lineText);
            if (m.Success)
            {
                symbols.Add(new CodeSymbol(
                    m.Groups["name"].Value,
                    m.Groups["kind"].Value.ToLowerInvariant(),
                    lineNumber,
                    offset));
            }
            return symbols.Count < MaxSymbols;
        });
    }

    private static void ExtractCode(string text, List<CodeSymbol> symbols)
    {
        var inBlockComment = false;
        ForEachLine(text, (lineText, lineNumber, offset) =>
        {
            var trimmed = lineText.TrimStart();
            if (inBlockComment)
            {
                if (trimmed.Contains("*/", StringComparison.Ordinal)) inBlockComment = false;
                return true;
            }
            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                if (!trimmed.Contains("*/", StringComparison.Ordinal)) inBlockComment = true;
                return true;
            }
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('#'))
                return true;

            var typeMatch = CStyleTypeRegex.Match(lineText);
            if (typeMatch.Success)
            {
                symbols.Add(new CodeSymbol(
                    typeMatch.Groups["name"].Value,
                    typeMatch.Groups["kind"].Value,
                    lineNumber,
                    offset));
                return symbols.Count < MaxSymbols;
            }

            if (trimmed.EndsWith(';')) return true;

            var methodMatch = CStyleMethodRegex.Match(lineText);
            if (methodMatch.Success)
            {
                var name = methodMatch.Groups["name"].Value;
                if (!ControlKeywords.Contains(name))
                    symbols.Add(new CodeSymbol(name, "fn", lineNumber, offset));
            }

            return symbols.Count < MaxSymbols;
        });
    }

    private static void ForEachLine(string text, Func<string, int, int, bool> visitor)
    {
        var line = 1;
        var pos = 0;
        while (pos <= text.Length)
        {
            var nl = text.IndexOf('\n', pos);
            var end = nl < 0 ? text.Length : nl;
            var len = end - pos;
            if (len > 0 && text[pos + len - 1] == '\r') len--;
            if (!visitor(text.Substring(pos, len), line, pos)) break;
            if (nl < 0) break;
            pos = nl + 1;
            line++;
        }
    }
}

/// <summary>
/// Formatting, syntax validation, line-ending/indentation helpers, and line commenting.
/// </summary>
public static class CodeFormatter
{
    private static readonly HashSet<string> JsonExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonc"
    };

    private static readonly HashSet<string> XmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".axaml", ".xaml", ".csproj", ".props", ".targets", ".config", ".svg"
    };

    private static readonly HashSet<string> HashCommentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".psd1", ".rb", ".yml", ".yaml",
        ".toml", ".ini", ".cfg", ".conf", ".r", ".pl", ".tf", ".hcl", ".mk", ".cmake",
        ".gitignore", ".gitattributes", ".editorconfig", ".dockerignore", ".env"
    };

    private static readonly HashSet<string> DashCommentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sql", ".lua"
    };

    private static readonly HashSet<string> BlockCommentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".axaml", ".xaml", ".csproj", ".props", ".targets", ".config", ".html", ".htm", ".svg", ".md", ".markdown"
    };

    public static bool CanFormat(string? path)
    {
        var ext = Path.GetExtension(path ?? "");
        return JsonExtensions.Contains(ext) || XmlExtensions.Contains(ext);
    }

    public static FormatResult TryFormat(string text, string? path)
    {
        var ext = Path.GetExtension(path ?? "");
        var useCrlf = DetectLineEnding(text) == "CRLF";

        if (JsonExtensions.Contains(ext))
        {
            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
                {
                    Indented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }))
                {
                    doc.WriteTo(writer);
                }
                var formatted = Encoding.UTF8.GetString(ms.ToArray());
                return new FormatResult(true, ConvertLineEndings(formatted, useCrlf), null, 0);
            }
            catch (JsonException ex)
            {
                return new FormatResult(false, null, ex.Message, (int)(ex.LineNumber ?? 0) + 1);
            }
        }

        if (XmlExtensions.Contains(ext))
        {
            try
            {
                var doc = XDocument.Parse(text);
                var sb = new StringBuilder();
                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    OmitXmlDeclaration = doc.Declaration is null,
                    NewLineChars = useCrlf ? "\r\n" : "\n",
                };
                using (var writer = XmlWriter.Create(sb, settings))
                {
                    doc.Save(writer);
                }
                return new FormatResult(true, ConvertLineEndings(sb.ToString(), useCrlf), null, 0);
            }
            catch (XmlException ex)
            {
                return new FormatResult(false, null, ex.Message, Math.Max(1, ex.LineNumber));
            }
        }

        return new FormatResult(false, null, "Unsupported file type", 0);
    }

    public static string DetectLineEnding(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Environment.NewLine == "\r\n" ? "CRLF" : "LF";

        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++;
            else lf++;
            if (crlf + lf >= 200) break;
        }

        if (crlf == 0 && lf == 0)
            return Environment.NewLine == "\r\n" ? "CRLF" : "LF";

        return crlf >= lf ? "CRLF" : "LF";
    }

    public static string ConvertLineEndings(string text, bool toCrlf)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return toCrlf ? normalized.Replace("\n", "\r\n") : normalized;
    }

    public static string DetectIndentation(string text)
    {
        if (string.IsNullOrEmpty(text)) return "Spaces: 4";

        var tabLines = 0;
        var spaceLines = 0;
        var twoSpaceHints = 0;
        var fourSpaceHints = 0;
        var checkedLines = 0;

        var pos = 0;
        while (pos < text.Length && checkedLines < 200)
        {
            var nl = text.IndexOf('\n', pos);
            var end = nl < 0 ? text.Length : nl;
            var lineLen = end - pos;
            if (lineLen > 0 && text[pos + lineLen - 1] == '\r') lineLen--;

            if (lineLen > 0)
            {
                if (text[pos] == '\t')
                {
                    tabLines++;
                    checkedLines++;
                }
                else if (text[pos] == ' ')
                {
                    var spaces = 0;
                    while (spaces < lineLen && text[pos + spaces] == ' ') spaces++;
                    if (spaces < lineLen)
                    {
                        spaceLines++;
                        checkedLines++;
                        if (spaces % 4 == 0) fourSpaceHints++;
                        else if (spaces % 2 == 0) twoSpaceHints++;
                    }
                }
            }

            if (nl < 0) break;
            pos = nl + 1;
        }

        if (tabLines > spaceLines) return "Tabs";
        if (twoSpaceHints > 0 && twoSpaceHints >= fourSpaceHints) return "Spaces: 2";
        return "Spaces: 4";
    }

    /// <summary>
    /// Toggles line (or XML block) comments across the selected line range, returning the
    /// replacement segment and updated selection bounds for a single undoable document edit.
    /// </summary>
    public static (string ReplacedSegment, int SegmentStart, int SegmentLength, int NewSelStart, int NewSelLength) ToggleLineComment(
        string text,
        int selStart,
        int selLength,
        string? path)
    {
        selStart = Math.Clamp(selStart, 0, text.Length);
        var selEnd = Math.Clamp(selStart + Math.Max(0, selLength), 0, text.Length);

        var fileName = Path.GetFileName(path ?? "");
        var ext = Path.GetExtension(path ?? "");

        if (BlockCommentExtensions.Contains(ext))
            return ToggleBlockComment(text, selStart, selEnd);

        var prefix = HashCommentExtensions.Contains(ext) || fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase)
            ? "#"
            : DashCommentExtensions.Contains(ext)
                ? "--"
                : "//";

        return TogglePrefixComment(text, selStart, selEnd, prefix);
    }

    private static (string ReplacedSegment, int SegmentStart, int SegmentLength, int NewSelStart, int NewSelLength) TogglePrefixComment(
        string text,
        int selStart,
        int selEnd,
        string prefix)
    {
        var segStart = LineStartAt(text, selStart);
        var effectiveEnd = selEnd > selStart && selEnd > 0 && text[selEnd - 1] == '\n' ? selEnd - 1 : selEnd;
        var segEnd = LineEndAt(text, effectiveEnd);
        var segLen = segEnd - segStart;
        var segment = text.Substring(segStart, segLen);

        var eol = DetectLineEnding(text) == "CRLF" ? "\r\n" : "\n";
        var rawLines = segment.Split('\n');
        var lines = new string[rawLines.Length];
        for (var i = 0; i < rawLines.Length; i++)
            lines[i] = rawLines[i].TrimEnd('\r');

        var allCommented = true;
        var hasNonBlank = false;
        var minIndent = int.MaxValue;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            hasNonBlank = true;
            var indent = 0;
            while (indent < line.Length && char.IsWhiteSpace(line[indent])) indent++;
            if (indent < minIndent) minIndent = indent;
            if (!line.AsSpan(indent).StartsWith(prefix, StringComparison.Ordinal))
                allCommented = false;
        }

        if (!hasNonBlank)
            return (segment, segStart, segLen, selStart, selEnd - selStart);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (allCommented)
            {
                var indent = 0;
                while (indent < line.Length && char.IsWhiteSpace(line[indent])) indent++;
                var removeLen = prefix.Length;
                if (indent + removeLen < line.Length && line[indent + removeLen] == ' ')
                    removeLen++;
                lines[i] = line.Remove(indent, removeLen);
            }
            else
            {
                lines[i] = line.Insert(minIndent, prefix + " ");
            }
        }

        var replaced = string.Join(eol, lines);
        var newSelStart = selStart;
        var newSelEnd = segStart + replaced.Length;
        if (selEnd == selStart)
        {
            var delta = replaced.Length - segLen;
            newSelStart = Math.Max(segStart, selStart + delta);
            newSelEnd = newSelStart;
        }
        return (replaced, segStart, segLen, newSelStart, Math.Max(0, newSelEnd - newSelStart));
    }

    private static (string ReplacedSegment, int SegmentStart, int SegmentLength, int NewSelStart, int NewSelLength) ToggleBlockComment(
        string text,
        int selStart,
        int selEnd)
    {
        var segStart = selStart == selEnd ? LineStartAt(text, selStart) : selStart;
        var segEnd = selStart == selEnd ? LineEndAt(text, selStart) : selEnd;
        var segLen = segEnd - segStart;
        var segment = text.Substring(segStart, segLen);
        var trimmed = segment.Trim();

        string replaced;
        if (trimmed.StartsWith("<!--", StringComparison.Ordinal) && trimmed.EndsWith("-->", StringComparison.Ordinal) && trimmed.Length >= 7)
        {
            var openIdx = segment.IndexOf("<!--", StringComparison.Ordinal);
            var closeIdx = segment.LastIndexOf("-->", StringComparison.Ordinal);
            var inner = segment[(openIdx + 4)..closeIdx];
            if (inner.StartsWith(' ') && inner.EndsWith(' ') && inner.Length >= 2)
                inner = inner[1..^1];
            replaced = segment[..openIdx] + inner + segment[(closeIdx + 3)..];
        }
        else
        {
            var indent = 0;
            if (selStart == selEnd)
            {
                while (indent < segment.Length && char.IsWhiteSpace(segment[indent])) indent++;
            }
            replaced = segment[..indent] + "<!-- " + segment[indent..] + " -->";
        }

        return (replaced, segStart, segLen, segStart, replaced.Length);
    }

    private static int LineStartAt(string text, int offset)
    {
        var i = Math.Clamp(offset, 0, text.Length);
        while (i > 0 && text[i - 1] != '\n') i--;
        return i;
    }

    private static int LineEndAt(string text, int offset)
    {
        var i = Math.Clamp(offset, 0, text.Length);
        while (i < text.Length && text[i] != '\n') i++;
        if (i > 0 && text[i - 1] == '\r') i--;
        return i;
    }
}
