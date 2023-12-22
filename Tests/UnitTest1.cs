using FluentAssertions;
using Markdig.BetterHighlight;

namespace Tests;

public class UnitTest1
{
    [Fact]
    public void DirectiveTesting() {
        string baseLine = "some text blah blah ";
        string line = baseLine + "// [!code focus -- error]";
        CodeBlockInfoParser.ParseAndRemoveDirectives(ref line, out string[] dirs);
        dirs.Should().BeEquivalentTo("focus", "--", "error");
        line.Should().Be(baseLine);
    }
}