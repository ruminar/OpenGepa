using System.Collections.ObjectModel;
using OpenGepa;
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
    ("Directory candidate defaults", TestDirectoryCandidateDefaults),
    ("File dialog filter", TestFileDialogFilter),
    ("Small icon size is preserved", TestSmallIconSizeIsPreserved),
    ("Directory scan root group", TestDirectoryScanRootGroup),
    ("Destination choices", TestDestinationChoices),
    ("Editor expansion persistence", TestEditorExpansionPersistence),
    ("Tree range selection", TestTreeRangeSelection),
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

static void TestDirectoryCandidateDefaults()
{
    True(!DirectoryCandidateRules.IsInitiallySelected("C:\\Tools\\setup.exe"));
    True(!DirectoryCandidateRules.IsInitiallySelected("C:\\Tools\\DiskSpd32.exe"));
    True(!DirectoryCandidateRules.IsInitiallySelected("C:\\Tools\\DiskSpd32L.exe"));
    True(!DirectoryCandidateRules.IsInitiallySelected("C:\\Tools\\x86\\Tool.exe"));
    True(!DirectoryCandidateRules.IsInitiallySelected("C:\\Tools\\helper.ps1"));
    True(DirectoryCandidateRules.IsInitiallySelected("C:\\Tools\\DiskSpd64.exe"));
    True(DirectoryCandidateRules.IsInitiallySelected("C:\\Tools\\Tool.lnk"));
    Equal("Tool.exe", DirectoryCandidateRules.DefaultDisplayName("C:\\Tools\\Tool.exe"));
}

static void TestFileDialogFilter()
{
    True(DirectoryCandidateRules.FileItemDialogFilter.Contains("*.exe", StringComparison.Ordinal));
    True(DirectoryCandidateRules.FileItemDialogFilter.Contains("*.lnk", StringComparison.Ordinal));
    True(DirectoryCandidateRules.FileItemDialogFilter.Contains("*.pdf", StringComparison.Ordinal));
    True(!DirectoryCandidateRules.FileItemDialogFilter.Contains("*.*", StringComparison.Ordinal));
}

static void TestSmallIconSizeIsPreserved()
{
    var path = Path.Combine(Path.GetTempPath(), "OpenGepa.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
    try
    {
        var source = Path.Combine(path, "small.png"); WritePng(source, System.Drawing.Color.Red);
        var paths = new AppPaths(path); paths.EnsureWritable(); var icon = new IconService(paths).ImportImage(source, "small");
        using var image = new System.Drawing.Bitmap(Path.Combine(path, icon.Replace('/', Path.DirectorySeparatorChar))); Equal(2, image.Width); Equal(2, image.Height);
    }
    finally { Directory.Delete(path, true); }
}

static void TestDirectoryScanRootGroup()
{
    var destination = new ObservableCollection<LauncherNode>();
    var first = DirectoryScanRootRules.GetOrCreateRootGroup(destination, "D:\\wintools\\");
    Equal(1, destination.Count); True(destination[0] is GroupNode { Name: "wintools" });
    first.Add(new FileItem { Name = "tool.exe", Target = "D:\\wintools\\tool.exe" });
    var merged = DirectoryScanRootRules.GetOrCreateRootGroup(destination, "D:\\WINTOOLS");
    True(ReferenceEquals(first, merged)); Equal(1, merged.Count);
    destination.Add(new FileItem { Name = "other", Target = "D:\\other.exe" });
    Throws<InvalidDataException>(() => DirectoryScanRootRules.GetOrCreateRootGroup(destination, "D:\\other"));
}

static void TestDestinationChoices()
{
    var child = new GroupNode { Name = "Child", Order = 0 };
    var parent = new GroupNode { Name = "Parent", Order = 1, Children = new ObservableCollection<LauncherNode> { child } };
    var other = new GroupNode { Name = "Other", Order = 0 };
    var tab = new LauncherTab { Name = "Main", Children = new ObservableCollection<LauncherNode> { parent, other } };
    var choices = DestinationOptions.Build(tab);
    Equal(4, choices.Count);
    Equal("root", choices[0].DisplayPath); Equal(null, choices[0].GroupId);
    Equal(other.Id, choices[1].GroupId); Equal("root / Other", choices[1].DisplayPath);
    Equal(parent.Id, choices[2].GroupId); Equal("root / Parent", choices[2].DisplayPath);
    Equal(child.Id, choices[3].GroupId); Equal("root / Parent / Child", choices[3].DisplayPath);
}

static void TestEditorExpansionPersistence()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "OpenGepa.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var application = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            var app = AppService.Create(path); app.Initialize();
            var group = new GroupNode { Name = "Open" }; group.Children.Add(new FileItem { Name = "Tool.exe", Target = "C:\\Tools\\Tool.exe" });
            var tab = new LauncherTab { Name = "Editor", Children = new ObservableCollection<LauncherNode> { group } }; app.ReplaceData(Data(tab));
            var window = new EditorWindow(app) { ShowInTaskbar = false, Left = -10000, Top = -10000, Opacity = 0 }; window.Show(); window.RefreshData(tab.Id); window.UpdateLayout();
            var tree = (System.Windows.Controls.TreeView)window.FindName("EditorTree"); tree.UpdateLayout();
            var root = (System.Windows.Controls.TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(tree.Items[0]); True(root.DataContext is EditorRootNode); root.IsExpanded = true; tree.UpdateLayout();
            var before = (System.Windows.Controls.TreeViewItem)root.ItemContainerGenerator.ContainerFromItem(root.Items[0]); before.IsExpanded = true; tree.UpdateLayout();
            True(app.TryCommit(data => ((GroupNode)data.Tabs[0].Children[0]).Children[0].Name = "Renamed.exe", out var error), error);
            tree.UpdateLayout(); var refreshedRoot = (System.Windows.Controls.TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(tree.Items[0]); var after = (System.Windows.Controls.TreeViewItem)refreshedRoot.ItemContainerGenerator.ContainerFromItem(refreshedRoot.Items[0]); True(refreshedRoot.IsExpanded); True(after.IsExpanded);
            window.Hide(); application.Shutdown();
        }
        catch (Exception ex) { failure = ex; }
        finally { try { Directory.Delete(path, true); } catch { } }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure is not null) throw failure;
}

static void TestTreeRangeSelection()
{
    var visible = new[] { "a", "b", "c", "d" };
    var first = TreeSelectionLogic.Apply([], visible, null, "b", false, false); Equal("b", first.AnchorId); Equal(1, first.Selected.Count);
    var range = TreeSelectionLogic.Apply(first.Selected, visible, first.AnchorId, "d", true, false); True(range.Selected.SetEquals(["b", "c", "d"])); Equal("b", range.AnchorId); Equal("d", range.PrimaryId);
    var toggled = TreeSelectionLogic.Apply(range.Selected, visible, range.AnchorId, "c", false, true); True(toggled.Selected.SetEquals(["b", "d"]));
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

static void True(bool value, string? message = null) { if (!value) throw new InvalidOperationException(message ?? "Expected true."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
