using System.Windows.Input;

namespace OpenGepa;

/// <summary>修飾クリックによるランチャー項目の誤起動を防ぐ規則です。</summary>
public static class LauncherClickRules
{
    public static bool BlocksMouseAction(ModifierKeys modifiers) => (modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != ModifierKeys.None;
}
