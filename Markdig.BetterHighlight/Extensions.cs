using System.Buffers;
using TextMateSharp.Grammars;

namespace Markdig.BetterHighlight;

internal static class Extensions
{
    public static IToken[] TokenizeLine(this IGrammar grammar, string line, ref IStateStack? stack, TimeSpan timeout) {
        ITokenizeLineResult result = grammar.TokenizeLine(line, stack, timeout);
        stack = result.RuleStack;
        return result.Tokens;
    }

    public static void WriteFormatted<T>(this IBufferWriter<char> writeHead, T i, ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null)
        where T : ISpanFormattable, IFormattable {
        Span<char> span = writeHead.GetSpan(6);
        if (i.TryFormat(span, out int charsWritten, format, provider)) {
            writeHead.Advance(charsWritten);
            return;
        }

        Fallback(writeHead, i, format, provider);

        return;

        // if you are writing out line numbers more than 6 digits long you can live with the unpooled allocations
        static void Fallback(IBufferWriter<char> writeHead, T i, ReadOnlySpan<char> readOnlySpan,
            IFormatProvider? formatProvider) => writeHead.Write(i.ToString(new string(readOnlySpan), formatProvider));
    }
}