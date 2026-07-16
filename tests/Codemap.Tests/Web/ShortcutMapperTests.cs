using Codemap.Web.Services;

namespace Codemap.Tests.Web;

public class ShortcutMapperTests
{
    [Theory]
    [InlineData("k", true)]
    [InlineData("K", true)]
    public void CtrlK_opens_quick_jump_even_inside_text_inputs(string key, bool inInput)
    {
        var action = ShortcutMapper.Map(key, ctrlOrMeta: true, shift: false, inTextInput: inInput);
        Assert.Equal(ShortcutActionKind.QuickJump, action?.Kind);
    }

    [Fact]
    public void CtrlEnter_triggers_analyze_from_anywhere()
    {
        var action = ShortcutMapper.Map("Enter", ctrlOrMeta: true, shift: false, inTextInput: true);
        Assert.Equal(ShortcutActionKind.Analyze, action?.Kind);
    }

    [Theory]
    [InlineData("1", ShortcutActionKind.FilterAll)]
    [InlineData("2", ShortcutActionKind.FilterCSharp)]
    [InlineData("3", ShortcutActionKind.FilterJs)]
    [InlineData("f", ShortcutActionKind.ZoomFit)]
    [InlineData("+", ShortcutActionKind.ZoomIn)]
    [InlineData("-", ShortcutActionKind.ZoomOut)]
    [InlineData("?", ShortcutActionKind.ShowCheatSheet)]
    [InlineData("Delete", ShortcutActionKind.RemoveAnnotation)]
    public void Bare_keys_map_to_their_actions(string key, ShortcutActionKind expected)
    {
        var action = ShortcutMapper.Map(key, ctrlOrMeta: false, shift: false, inTextInput: false);
        Assert.Equal(expected, action?.Kind);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("f")]
    [InlineData("ArrowLeft")]
    [InlineData("Delete")]
    public void Bare_keys_are_suppressed_while_typing_in_an_input(string key) =>
        Assert.Null(ShortcutMapper.Map(key, ctrlOrMeta: false, shift: false, inTextInput: true));

    [Fact]
    public void Escape_works_even_inside_inputs()
    {
        var action = ShortcutMapper.Map("Escape", ctrlOrMeta: false, shift: false, inTextInput: true);
        Assert.Equal(ShortcutActionKind.Escape, action?.Kind);
    }

    [Fact]
    public void Arrow_nudges_are_1px_and_10px_with_shift()
    {
        Assert.Equal(1, ShortcutMapper.Map("ArrowRight", false, shift: false, false)?.Magnitude);
        Assert.Equal(10, ShortcutMapper.Map("ArrowRight", false, shift: true, false)?.Magnitude);
    }
}
