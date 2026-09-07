using System.Windows.Input;

namespace OpenGepa.Services;

public static class SeparatorInteractionRules
{
    public static bool AllowsLauncherSelection(ModifierKeys modifiers)
    {
        return (modifiers & ModifierKeys.Control) != ModifierKeys.None;
    }
}
