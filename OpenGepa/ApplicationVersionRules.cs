namespace OpenGepa;

public static class ApplicationVersionRules
{
    public static string Display(Version? version) => version is null || version.Build < 0
        ? "不明"
        : $"{version.Major}.{version.Minor}.{version.Build}";
}
