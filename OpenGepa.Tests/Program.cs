using System.Collections.ObjectModel;
using OpenGepa.Models;
using OpenGepa.Services;

var tests = new (string Name, Action Run)[]
{
    ("Name normalization", TestNameNormalization),
    ("Sibling duplicate rejection", TestDuplicateNames),
    ("Polymorphic round trip", TestRoundTrip),
    ("Backup recovery", TestBackupRecovery),
    ("Last-good recovery", TestLastGoodRecovery),
    ("Profile round trip and icon collision", TestProfileRoundTrip),
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed");
return failed == 0 ? 0 : 1;

static void TestNameNormalization()
{
    Equal("Éclair", NameRules.Normalize("  E\u0301clair  "));
    True(NameRules.IsValid("テスト", out _));
    True(!NameRules.IsValid(" \r\n ", out _));
}

static void TestDuplicateNames()
{
    var tab = new LauncherTab { Name = "Tools" };
    tab.Children.Add(new GroupNode { Name = " Chrome " });
    tab.Children.Add(new FileItem { Name = "chrome", Target = "C:\\Apps\\Chrome.exe" });
    Throws<InvalidDataException>(() => new DataValidator().Validate(Data(tab)));
}

static void TestRoundTrip()
{
    WithStore((_, store) =>
    {
        var group = new GroupNode { Name = "Group" };
        group.Children.Add(new UrlItem { Name = "Web", Target = "https://example.com" });
        var restored = store.Deserialize(store.Serialize(Data(new LauncherTab { Name = "Main", Children = new ObservableCollection<LauncherNode> { group } })));
        True(restored.Tabs[0].Children[0] is GroupNode { Children.Count: 1 });
        True(((GroupNode)restored.Tabs[0].Children[0]).Children[0] is UrlItem);
    });
}

static void TestBackupRecovery()
{
    WithStore((paths, store) =>
    {
        store.Save(Data(new LauncherTab { Name = "First" }));
        store.Save(Data(new LauncherTab { Name = "Second" }));
        File.WriteAllText(paths.DataFile, "broken");
        var loaded = store.Load();
        Equal(DataSource.Backup, loaded.Source); Equal("First", loaded.Data.Tabs[0].Name);
    });
}

static void TestLastGoodRecovery()
{
    WithStore((paths, store) =>
    {
        var stable = Data(new LauncherTab { Name = "Stable" }); store.Save(stable); store.MarkLastGood(stable);
        store.Save(Data(new LauncherTab { Name = "Later" }));
        File.WriteAllText(paths.DataFile, "broken"); File.WriteAllText(paths.BackupFile, "broken too");
        var loaded = store.Load();
        Equal(DataSource.LastGood, loaded.Source); Equal("Stable", loaded.Data.Tabs[0].Name);
    });
}

static void TestProfileRoundTrip()
{
    var path = Path.Combine(Path.GetTempPath(), "OpenGepa.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
    try
    {
        var app = AppService.Create(path); app.Initialize();
        var iconPath = Path.Combine(app.Paths.IconDirectory, "sample.png"); WritePng(iconPath, System.Drawing.Color.Red);
        var tab = new LauncherTab { Name = "Profile" }; tab.Children.Add(new FileItem { Name = "Tool", Target = "C:\\Tools\\Tool.exe", Icon = "icon/sample.png" }); app.ReplaceData(Data(tab));
        var profile = Path.Combine(path, "profile.ogp"); app.ProfileService.Save(profile);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(profile))
        {
            True(archive.GetEntry("manifest.json") is not null); True(archive.GetEntry($"menus/{tab.Id}.json") is not null); True(archive.GetEntry("icons/sample.png") is not null);
            using var reader = new StreamReader(archive.GetEntry($"menus/{tab.Id}.json")!.Open()); True(reader.ReadToEnd().Contains("icons/sample.png", StringComparison.Ordinal));
        }
        WritePng(iconPath, System.Drawing.Color.Blue);
        var loaded = app.ProfileService.Load(profile); var item = (FileItem)loaded.Tabs[0].Children[0];
        Equal("icon/sample_2.png", item.Icon); True(File.Exists(Path.Combine(path, "icon", "sample_2.png")));
    }
    finally { Directory.Delete(path, true); }
}

static void WritePng(string path, System.Drawing.Color color)
{
    using var image = new System.Drawing.Bitmap(2, 2); using var graphics = System.Drawing.Graphics.FromImage(image); graphics.Clear(color); image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
}

static OpenGepaData Data(LauncherTab tab) => new() { SelectedTabId = tab.Id, Tabs = new ObservableCollection<LauncherTab> { tab } };

static void WithStore(Action<AppPaths, DataStore> action)
{
    var path = Path.Combine(Path.GetTempPath(), "OpenGepa.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    try { var paths = new AppPaths(path); paths.EnsureWritable(); action(paths, new DataStore(paths, new DataValidator())); }
    finally { Directory.Delete(path, true); }
}

static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
