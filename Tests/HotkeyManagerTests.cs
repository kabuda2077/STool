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
    [InlineData(" F1 ", "F1")]
    [InlineData("f12", "F12")]
    [InlineData("F24", "F24")]
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
    [InlineData("F0")]
    [InlineData("F25")]
    [InlineData("Right")]
    [InlineData("9")]
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

    [Theory]
    [InlineData("F1", 0x70u)]
    [InlineData("F24", 0x87u)]
    public void TryParseHotkey_FunctionKeyCanRegisterWithoutModifiers(string input, uint virtualKey)
    {
        Assert.True(HotkeyManager.TryParseHotkey(input, out var parsed));
        Assert.Equal(0u, parsed.Modifiers);
        Assert.Equal(virtualKey, parsed.VirtualKey);
    }
}
