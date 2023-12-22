using System.Text.RegularExpressions;
using CommunityToolkit.HighPerformance;

namespace Markdig.BetterHighlight;

public static partial class CodeBlockInfoParser
{
    [GeneratedRegex(
        @"(?<language>[a-zA-Z]+)(?::(?<lineNum>(?:no-)?line-numbers(?:=(?<startingLine>\d+))?))?(?:\{(?<highlights>\d+(?:-\d+)?(?:,\d+(?:-\d+)?)*)\})?(?:\[(?<codegroup>\w+)\])?",
        RegexOptions.IgnoreCase)]
    private static partial Regex InfoRegex();

    [GeneratedRegex(@"//\s+\[!code(?:\s+(?<directive>\S+))+\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CommentRegex();

    public static void ParseAndRemoveDirectives(ref string line, out string[] directives) {
        Match match = CommentRegex().Match(line);
        if (!match.Success) {
            directives = Array.Empty<string>();
        }

        directives = match.Groups["directive"].Captures.Select(c => c.Value).ToArray();
        line = CommentRegex().Replace(line, "");
    }

    public static bool TryParseBlockInfo(string str, SyntaxHighlightingOptions options, out string language,
        out Range[] highlights, out int? startingLineNumber, out ReadOnlySpan<char> codeGroup) {
        Match match = InfoRegex().Match(str);

        if (!match.Success) {
            language = string.Empty;
            highlights = Array.Empty<Range>();
            startingLineNumber = null;
            codeGroup = default;
            return false;
        }

        language = match.Groups["language"].Value;
        codeGroup = match.Groups["codegroup"].ValueSpan;

        ParseLineNumbersOption(options, match, out startingLineNumber);

        ParseHighlights(out highlights, match);

        return true;

        void ParseLineNumbersOption(SyntaxHighlightingOptions syntaxHighlightingOptions, Match match1, out int? i) {
            if (match1.Groups["lineNum"] is { Success: true, ValueSpan: var ln, }) {
                if (ln.StartsWith("no-")) {
                    i = null;
                } else if (match1.Groups["startingLine"] is { Success: true, ValueSpan: var sln, }) {
                    i = int.Parse(sln);
                } else {
                    i = 0;
                }
            } else {
                i = syntaxHighlightingOptions.LineNumbers ? 0 : null;
            }
        }

        static void ParseHighlights(out Range[] highlights1, Match match1) {
            List<Range> ranges = [];
            if (match1.Groups["highlights"] is { Success: true, ValueSpan: var hl, }) {
                foreach (ReadOnlySpan<char> span in hl.Tokenize(',')) {
                    if (span.IndexOf('-') is >= 0 and var idx) {
                        int start = int.Parse(span[..(idx - 1)]);
                        int end = int.Parse(span[(idx + 1)..]);
                        ranges.Add(start..end);
                    } else {
                        int num = int.Parse(span);
                        ranges.Add(num..num);
                    }
                }
            }

            highlights1 = ranges.ToArray();
        }
    }
}