namespace Codemap.Web.Services;

public enum ShortcutActionKind
{
    QuickJump,
    Analyze,
    FilterAll,
    FilterCSharp,
    FilterJs,
    ZoomFit,
    ZoomIn,
    ZoomOut,
    Escape,
    NudgeLeft,
    NudgeRight,
    NudgeUp,
    NudgeDown,
    RemoveAnnotation,
    FocusSidebarFilter,
    ShowCheatSheet,
}

public readonly record struct ShortcutAction(ShortcutActionKind Kind, int Magnitude = 1);

/// <summary>
/// The single source of truth for keyboard shortcuts (spec §11.1). Pure function so the mapping is
/// testable without components or JS. `inTextInput` disables everything except the combos the spec
/// explicitly allows while an input has focus (Ctrl/Cmd+K, Ctrl/Cmd+Enter, Ctrl/Cmd+F, Esc).
/// </summary>
public static class ShortcutMapper
{
    public static ShortcutAction? Map(string key, bool ctrlOrMeta, bool shift, bool inTextInput)
    {
        if (ctrlOrMeta)
        {
            return key.ToLowerInvariant() switch
            {
                "k" => new ShortcutAction(ShortcutActionKind.QuickJump),
                "enter" => new ShortcutAction(ShortcutActionKind.Analyze),
                "f" => new ShortcutAction(ShortcutActionKind.FocusSidebarFilter),
                _ => null,
            };
        }

        if (key == "Escape") return new ShortcutAction(ShortcutActionKind.Escape);
        if (inTextInput) return null;

        var nudge = shift ? 10 : 1;
        return key switch
        {
            "1" => new ShortcutAction(ShortcutActionKind.FilterAll),
            "2" => new ShortcutAction(ShortcutActionKind.FilterCSharp),
            "3" => new ShortcutAction(ShortcutActionKind.FilterJs),
            "f" or "F" => new ShortcutAction(ShortcutActionKind.ZoomFit),
            "+" or "=" => new ShortcutAction(ShortcutActionKind.ZoomIn),
            "-" or "_" => new ShortcutAction(ShortcutActionKind.ZoomOut),
            "ArrowLeft" => new ShortcutAction(ShortcutActionKind.NudgeLeft, nudge),
            "ArrowRight" => new ShortcutAction(ShortcutActionKind.NudgeRight, nudge),
            "ArrowUp" => new ShortcutAction(ShortcutActionKind.NudgeUp, nudge),
            "ArrowDown" => new ShortcutAction(ShortcutActionKind.NudgeDown, nudge),
            "Delete" or "Backspace" => new ShortcutAction(ShortcutActionKind.RemoveAnnotation),
            "?" => new ShortcutAction(ShortcutActionKind.ShowCheatSheet),
            _ => null,
        };
    }
}
