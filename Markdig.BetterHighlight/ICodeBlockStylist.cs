using TextMateSharp.Themes;

namespace Markdig.BetterHighlight;

public interface ICodeBlockStylist
{
    public IRawTheme GetLightTheme();
    public IRawTheme GetDarkTheme();
}