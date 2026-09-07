namespace OpenGepa.Services;

public static class TabStripLayoutRules
{
    public const double NormalColumnWidth = 88;
    public const double ScrollableColumnWidth = 98;

    public static double GetColumnWidth(bool requiresVerticalScroll)
    {
        return requiresVerticalScroll ? ScrollableColumnWidth : NormalColumnWidth;
    }
}
