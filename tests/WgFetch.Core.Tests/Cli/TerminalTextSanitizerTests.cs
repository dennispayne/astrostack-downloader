using WgFetch.Core.Cli;

namespace WgFetch.Core.Tests.Cli;

public sealed class TerminalTextSanitizerTests
{
    [Theory]
    [InlineData("/source\u001b[31mred", "/source?[31mred")]
    [InlineData("ab\u0000cd", "ab?cd")]
    [InlineData("line1\r\nline2", "line1??line2")]
    public void Replaces_control_characters(string input, string expected)
    {
        Assert.Equal(expected, TerminalTextSanitizer.Sanitize(input));
    }

    [Fact]
    public void Returns_same_instance_when_no_control_characters_exist()
    {
        var clean = string.Concat("clean", "-value");

        var sanitized = TerminalTextSanitizer.Sanitize(clean);

        Assert.Same(clean, sanitized);
    }

    [Fact]
    public void Handles_empty_input()
    {
        Assert.Equal(string.Empty, TerminalTextSanitizer.Sanitize(string.Empty));
    }
}
