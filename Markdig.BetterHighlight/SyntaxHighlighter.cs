using System.Buffers;
using System.Text;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nerdbank.Streams;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Markdig.BetterHighlight;

public sealed class SyntaxHighlighter
    : HtmlObjectRenderer<CodeBlock>, IDisposable
{
    private readonly CodeBlockRenderer                   _fallback;
    private readonly ILogger<SyntaxHighlighter>          _logger;
    private readonly IOptions<SyntaxHighlightingOptions> _options;
    private readonly Registry                            _grammarRegistry;
    private readonly Sequence<char>                      _writeHead;
    private readonly CharBufferTextWriter                _writer;
    private readonly ICodeBlockStylist                   _stylist;
    private readonly StringBuilder                       _styleBuilder = new();

    public SyntaxHighlighter(CodeBlockRenderer fallback,
        ILogger<SyntaxHighlighter> logger,
        IOptions<SyntaxHighlightingOptions> options,
        Registry grammarRegistry,
        ICodeBlockStylist stylist) {
        _fallback = fallback;
        _logger = logger;
        _options = options;
        _writeHead = new Sequence<char>();
        _writer = new CharBufferTextWriter(_writeHead);
        _grammarRegistry = grammarRegistry;
        _stylist = stylist;
    }

    protected override void Write(HtmlRenderer renderer, CodeBlock obj) {
        if (obj is not FencedCodeBlock { Parser: FencedCodeBlockParser fencedCodeBlockParser, } fencedCodeBlock) {
            _logger.LogDebug("Falling back to default CodeBlockRenderer for non-fenced code block");
            _fallback.Write(renderer, obj);
            return;
        }

        int blobLength = 0, infoLength = 0;
        if (fencedCodeBlock.Info is { Length: var infoLen, }) {
            blobLength += infoLen;
            infoLength = infoLen;
        }

        if (fencedCodeBlock.Arguments is { Length: var argLen, })
            blobLength += argLen;
        Span<char> buf = stackalloc char[blobLength];
        fencedCodeBlock.Info?.CopyTo(buf);
        fencedCodeBlock.Arguments?.CopyTo(buf[infoLength..]);

        string s = new string(buf).Replace(" ", string.Empty);
        if (fencedCodeBlockParser.InfoPrefix != null) s = s.Replace(fencedCodeBlockParser.InfoPrefix, string.Empty);
        if (!CodeBlockInfoParser.TryParseBlockInfo(s, _options.Value, out string lang,
                out Range[] highlights, out int? sln, out ReadOnlySpan<char> group)) {
            _logger.LogDebug(
                "Failed to parse info block for fenced code block, falling back to default CodeBlockRenderer");
            _fallback.Write(renderer, obj);
            return;
        }

        string groupString = new(group);
        fencedCodeBlock.SetData("code-group-block-name", groupString);

        if (_grammarRegistry.GetLocator() is not RegistryOptions o ||
            o.GetScopeByLanguageId(lang) is not { } scope ||
            _grammarRegistry.LoadGrammar(scope) is not { } grammar) {
            _logger.LogDebug(
                "Cannot get grammar for language {Lang}, falling back to default CodeBlockRenderer", lang);
            _fallback.Write(renderer, obj);
            return;
        }

        StringLine[] lines = fencedCodeBlock.Lines.Lines;
        LineStatusHighlight[] highlightStatuses = new LineStatusHighlight[lines.Length];

        InitializeHighlights(highlights, highlightStatuses);
        WriteLines(renderer, sln, lines, grammar, highlightStatuses, groupString);
        return;

        void InitializeHighlights(Range[] ranges, LineStatusHighlight[] lineStatusHighlights) {
            lineStatusHighlights.AsSpan().Clear();
            foreach (Range range in ranges) {
                lineStatusHighlights.AsSpan(range).Fill(LineStatusHighlight.Simple);
            }
        }
    }

    private void WriteLines(HtmlRenderer renderer, int? sln, StringLine[] lines, IGrammar grammar,
        LineStatusHighlight[] highlightStatuses, string codeGroup) {
        IStateStack? stack = null;
        List<int?> lineNumberStack = [];
        bool anyLinesFocused = false;
        bool anyLinesNumbered = sln is not null;

        int start = sln ?? 0;

        bool tempUseLineNumbers = sln.HasValue;

        _writeHead.Reset();

        Theme lightTheme = Theme.CreateFromRawTheme(_stylist.GetLightTheme(), _grammarRegistry.GetLocator());
        Theme darkTheme = Theme.CreateFromRawTheme(_stylist.GetDarkTheme(), _grammarRegistry.GetLocator());

        // clear the inline css stringbuilder defensively
        _styleBuilder.Clear();

        for (int index = 0; index < lines.Length; index++) {
            StringLine line = lines[index];
            string text = new(line.Slice.AsSpan());

            bool focus = false;
            LineStatusHighlight highlight = highlightStatuses[index];

            CodeBlockInfoParser.ParseAndRemoveDirectives(ref text, out string[] directives);
            ProcessDirectives(directives, ref highlight, index, ref focus, ref anyLinesFocused, ref tempUseLineNumbers,
                ref start, ref anyLinesNumbered, lineNumberStack, _writeHead);

            lineNumberStack.Add(tempUseLineNumbers ? index + start : null);

            IToken[] tokens = grammar.TokenizeLine(text, ref stack, TimeSpan.MaxValue);

            WriteLineStartTag(highlight, focus, _writer);

            foreach (IToken token in tokens) {
                WriteToken(lightTheme, token, _styleBuilder, darkTheme, line, _writer);
            }

            _writer.Write("</span>");
        }


        StringBuilder lineNumberBlockBuilder = new();
        if (anyLinesNumbered) {
            WriteLineNumberBlock(lineNumberBlockBuilder, lineNumberStack);
        }
    }

    internal static void WriteLineNumberBlock(StringBuilder lineNumberBlockBuilder, List<int?> lineNumberStack) {
        lineNumberBlockBuilder.Append("<div class=\"line-numbers\" aria-hidden=\"true\">");

        foreach (int? i in lineNumberStack) {
            if (i is { } n) { // null = skip line
                if (n >= 0) // >= 0 -> actual line number
                    lineNumberBlockBuilder.Append($"<span>{n}</span>");
                else { // < 0 -> line skip bit so put in a highlight section
                    lineNumberBlockBuilder.Append(
                        "<span style=\"display:inline-block;background-color: var(--kh-code-line-highlighted-color);\"></span>");
                }
            }

            lineNumberBlockBuilder.Append("<br/>");
        }

        lineNumberBlockBuilder.AppendLine("</div>");
    }

    internal static void WriteToken(Theme lightTheme, IToken token, StringBuilder styleBuilder, Theme darkTheme,
        StringLine line, TextWriter writer) {
        // light and dark themes get different css variables and the css file swaps em out depending
        // on whether a high level element has the .dark class
        EmitThemeCssVariables(lightTheme, token, styleBuilder, "light", out int lightFontStyle);
        EmitThemeCssVariables(darkTheme, token, styleBuilder, "dark", out int darkFontStyle);

        // font style stuff im too lazy to figure out how to swap so just only including if both
        // light and dark themes agree on font style
        if (lightFontStyle == darkFontStyle)
            EmitFontStyle(styleBuilder, lightFontStyle);

        writer.Write("<span");

        // if we have no inline css to include then dont bother writing the attribute
        if (styleBuilder.Length > 0) {
            writer.Write(" style=\"");
            writer.Write(styleBuilder);
            writer.Write('"');
        }

        writer.Write(">");

        // token includes leading whitespace and we can use the indices to slice the span and dodge
        // extra allocations which is nice
        writer.Write(line.Slice.AsSpan()[token.StartIndex..token.EndIndex]);

        writer.Write("</span>");

        // clear the stringbuilder for the next token
        styleBuilder.Clear();
    }

    internal static void EmitThemeCssVariables(Theme lightTheme, IToken token, StringBuilder styleBuilder,
        string themeCssVarInfix, out int fontStyle) {
        if (GetColors(lightTheme, token, out int fgColor, out int bgColor, out fontStyle)) {
            if (lightTheme.GetColor(fgColor) is var f && !string.IsNullOrWhiteSpace(f))
                styleBuilder.Append($"--kh-code-color-{themeCssVarInfix}:{f};");
            if (lightTheme.GetColor(bgColor) is var g && !string.IsNullOrWhiteSpace(g))
                styleBuilder.Append($"--kh-code-bg-{themeCssVarInfix}:{f};");
            // EmitFontStyle(styleBuilder, fontStyle);
        }
    }

    internal static void EmitFontStyle(StringBuilder styleBuilder, int i) {
        if (i == FontStyle.NotSet)
            return;
        if ((i & FontStyle.Italic) != 0) styleBuilder.Append("font-style:italic;");
        if ((i & FontStyle.Bold) != 0) styleBuilder.Append("font-weight:700;");
        if ((i & (FontStyle.Underline | FontStyle.Strikethrough)) != 0) {
            styleBuilder.Append("text-decoration-line:");
            if ((i & FontStyle.Underline) != 0) styleBuilder.Append(" underline");
            if ((i & FontStyle.Strikethrough) != 0) styleBuilder.Append(" line-through");
            styleBuilder.Append(';');
        }
    }

    internal static void WriteLineStartTag(LineStatusHighlight highlight, bool focus, CharBufferTextWriter writer) {
        writer.Write("<span class=\"line");
        if (highlight is not LineStatusHighlight.None)
            writer.Write(" highlighted");
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (highlight) {
            case LineStatusHighlight.DiffPlus:
                writer.Write(" diff add");
                break;
            case LineStatusHighlight.DiffMinus:
                writer.Write(" diff remove");
                break;
            case LineStatusHighlight.Warning:
                writer.Write(" warning");
                break;
            case LineStatusHighlight.Error:
                writer.Write(" error");
                break;
        }

        if (focus)
            writer.Write(" has-focus");

        writer.Write("\">");
    }

    internal static void ProcessDirectives(string[] directives, ref LineStatusHighlight highlight, int index,
        ref bool focus, ref bool anyLinesFocused, ref bool tempUseLineNumbers, ref int start, ref bool anyLinesNumbered,
        List<int?> lineNumberStack, IBufferWriter<char> writeHead) {
        foreach (string directive in directives) {
            ProcessDirective(ref highlight, index, out focus, ref anyLinesFocused, ref tempUseLineNumbers, ref start,
                ref anyLinesNumbered, lineNumberStack, writeHead, directive);
        }
    }

    internal static void ProcessDirective(ref LineStatusHighlight highlight, int index, out bool focus,
        ref bool anyLinesFocused, ref bool tempUseLineNumbers, ref int start, ref bool anyLinesNumbered,
        List<int?> lineNumberStack, IBufferWriter<char> writeHead, string directive) {
        focus = false;
        switch (directive) {
            case "error"
                when highlight is not LineStatusHighlight.DiffPlus
                    and not LineStatusHighlight.DiffMinus:
                highlight = LineStatusHighlight.Error;
                break;
            case "warning" when highlight is LineStatusHighlight.None
                or LineStatusHighlight.Simple:
                highlight = LineStatusHighlight.Warning;
                break;
            case "--" when highlight is not LineStatusHighlight.DiffPlus:
                highlight = LineStatusHighlight.DiffMinus;
                break;
            case "++" when highlight is not LineStatusHighlight.DiffMinus:
                highlight = LineStatusHighlight.DiffPlus;
                break;
            case "highlight" when highlight is LineStatusHighlight.None:
                highlight = LineStatusHighlight.Simple;
                break;
            case "focus":
                focus = true;
                anyLinesFocused = true;
                break;
            case "no-line-numbers":
                tempUseLineNumbers = false;
                break;
            case var _ when directive.StartsWith("line-numbers"):
                ReadOnlySpan<char> readOnlySpan = directive.AsSpan(12);
                tempUseLineNumbers = true;
                anyLinesNumbered = true;

                if (readOnlySpan[0] == '=') {
                    lineNumberStack.Add(-1);
                    int newLineNumber = int.Parse(readOnlySpan[1..]);
                    EmitLinesSkipMarker(start + index, newLineNumber, writeHead);
                    start = newLineNumber - index;
                }

                break;
            default:
                throw new InvalidOperationException($"Unknown code line directive {directive}");
        }
    }

    internal static void EmitLinesSkipMarker(int start, int newLineNumber, IBufferWriter<char> writeHead) {
        writeHead.Write(
            "<span class=\"line highlighted\" style=\"color:var(--kh-code-line-number-color);\">Skipped lines ");
        writeHead.WriteFormatted(start + 1);
        writeHead.Write(" through ");
        writeHead.WriteFormatted(newLineNumber - 1);
        writeHead.Write(" <span class=\\\"skipped-lines-icon\\\"></span></span>");
    }

    internal static bool GetColors(Theme theme, IToken token, out int foregroundColor, out int backgroundColor,
        out int fontStyle) {
        bool ret = false;
        foregroundColor = -1;
        backgroundColor = -1;
        fontStyle = FontStyle.NotSet;

        foreach (ThemeTrieElementRule rule in theme.Match(token.Scopes)) {
            if (foregroundColor <= 0 && rule.foreground > 0) {
                foregroundColor = rule.foreground;
                ret = true;
            }

            if (backgroundColor <= 0 && rule.background > 0) {
                backgroundColor = rule.background;
                ret = true;
            }

            if (fontStyle == FontStyle.NotSet && rule.fontStyle != FontStyle.NotSet) {
                fontStyle = rule.fontStyle;
                ret = true;
            }
        }

        return ret;
    }


    public void Dispose() {
        _writeHead.Dispose();
    }
}