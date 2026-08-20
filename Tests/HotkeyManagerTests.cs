using STool.Core;
using Xunit;

namespace STool.Tests;

public class HotkeyManagerTests
{
    [Theory]
    [InlineData("ctrl+alt+a", "Ctrl+Alt+A")]
    [InlineData("Shift+Ctrl+F24", "Ctrl+Shift+F24")]
    [InlineData("win+pgdn", "Win+PageDown")]
    [InlineData("Alt+Delete", "Alt+Delete")]
    [InlineData("Ctrl+0", "Ctrl+0")]
    public void NormalizeHotkey_ValidInput_ReturnsCanonicalText(string input, string expected)
    {
        Assert.Equal(expected, HotkeyManager.NormalizeHotkey(input));
        Assert.True(HotkeyManager.IsValidHotkey(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Ctrl+Unknown")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl++A")]
    [InlineData("Nope+A")]
    public void NormalizeHotkey_InvalidInput_ReturnsNull(string input)
    {
        Assert.Null(HotkeyManager.NormalizeHotkey(input));
        Assert.False(HotkeyManager.IsValidHotkey(input));
    }

    [Fact]
    public void TryParseHotkey_ExposesExpectedVirtualKeyAndModifiers()
    {
        Assert.True(HotkeyManager.TryParseHotkey("Ctrl+Alt+Right", out var parsed));

        Assert.Equal(0x0003u, parsed.Modifiers);
        Assert.Equal(0x27u, parsed.VirtualKey);
        Assert.Equal("Ctrl+Alt+Right", parsed.NormalizedText);
    }
}
