using System.Collections.ObjectModel;
using OpenGepa;
using OpenGepa.Models;
using OpenGepa.Services;

var tests = new (string Name, Action Run)[]
{
    ("Name normalization", TestNameNormalization),
    ("Sibling duplicate rejection", TestDuplicateNames),
    ("Empty launcher state", TestEmptyLauncherState),
    ("Icon-set app icon cycle", TestIconSetAppIconCycle),
    ("Tray icon set uses ICO", TestTrayIconSetUsesIco),
    ("Default node icons use iconSet", TestDefaultNodeIconsUseIconSet),
    ("Polymorphic round trip", TestRoundTrip),
    ("DirectoryItem has no name field", TestDirectoryItemHasNoNameField),
    ("Backup recovery", TestBackupRecovery),
    ("Last-good recovery", TestLastGoodRecovery),
    ("Profile round trip and icon collision", TestProfileRoundTrip),
    ("Directory candidate defaults", TestDirectoryCandidateDefaults),
    ("File dialog filter", TestFileDialogFilter),
    ("Appearance settings", TestAppearanceSettings),
    ("Item launch click defaults", TestItemLaunchClickDefaults),
    ("Launcher tab duplication", TestLauncherTabDuplication),
    ("Cross-launcher move", TestCrossLauncherMove),
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

static void TestEmptyLauncherState()
{
    new DataValidator().Validate(new OpenGepaData());
    var hidden = new LauncherTab { Name = "Hidden", IsVisible = false };
    new DataValidator().Validate(new OpenGepaData { Tabs = new ObservableCollection<LauncherTab> { hidden } });
    var data = Data(new LauncherTab { Name = "Pinned" }); data.IsLauncherPinned = true;
    var restored = new DataStore(new AppPaths(Path.GetTempPath()), new DataValidator()).Deserialize(new DataStore(new AppPaths(Path.GetTempPath()), new DataValidator()).Serialize(data));
    True(restored.IsLauncherPinned);
}

static void TestIconSetAppIconCycle()
{
    WithStore((paths, _) =>
    {
        File.WriteAllText(Path.Combine(paths.IconSetDirectory, "appIcon4.png"), "x");
        File.WriteAllText(Path.Combine(paths.IconSetDirectory, "appIcon1.png"), "x");
        File.WriteAllText(Path.Combine(paths.IconSetDirectory, "appIcon2.png"), "x");
        var tabs = new[] { new LauncherTab { Name = "A", Order = 0 }, new LauncherTab { Name = "B", Order = 1 }, new LauncherTab { Name = "C", Order = 2 }, new LauncherTab { Name = "D", Order = 3 } };
        var icons = new IconSetService(paths, new IconService(paths));
        Equal("iconSet/appIcon1.png", icons.GetAppIcon(tabs[0], tabs));
        Equal("iconSet/appIcon2.png", icons.GetAppIcon(tabs[1], tabs));
        Equal("iconSet/appIcon4.png", icons.GetAppIcon(tabs[2], tabs));
        Equal("iconSet/appIcon1.png", icons.GetAppIcon(tabs[3], tabs));
    });
}

static void TestTrayIconSetUsesIco()
{
    WithStore((paths, _) =>
    {
        var source = Path.Combine(paths.BaseDirectory, "source.png");
        using (var image = new System.Drawing.Bitmap(48, 48)) { using var graphics = System.Drawing.Graphics.FromImage(image); graphics.Clear(System.Drawing.Color.CornflowerBlue); image.Save(source, System.Drawing.Imaging.ImageFormat.Png); }
        var iconService = new IconService(paths); var iconSet = new IconSetService(paths, iconService); iconSet.SetOpenGepaIcon(source);
        True(File.Exists(Path.Combine(paths.IconSetDirectory, "OpenGepa.ico"))); Equal("iconSet/OpenGepa.ico", iconSet.GetOpenGepaIcon());
        using var icon = iconService.TryLoadIcon(iconSet.GetOpenGepaIcon(), 16); True(icon is not null); Equal(16, icon!.Size.Width); Equal(16, icon.Size.Height);
    });
}

static void TestDefaultNodeIconsUseIconSet()
{
    WithStore((paths, _) =>
    {
        var source = Path.Combine(paths.BaseDirectory, "source.png"); WritePng(source, System.Drawing.Color.Goldenrod);
        var iconSet = new IconSetService(paths, new IconService(paths)); iconSet.SetDefaultIcon("group", source);
        Equal("iconSet/group_default.png", iconSet.GetDefaultIcon("group"));
        iconSet.DeleteDefaultIcon("group"); True(iconSet.GetDefaultIcon("group") is null);
    });
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

static void TestDirectoryItemHasNoNameField()
{
    WithStore((_, store) =>
    {
        var data = Data(new LauncherTab { Name = "Main", Children = new ObservableCollection<LauncherNode> { new DirectoryItem { Target = "C:\\Tools" } } });
        var json = store.Serialize(data);
        True(!json.Contains("\"name\": \"C:\\\\Tools\"", StringComparison.Ordinal));
        var restored = store.Deserialize(json);
        Equal("C:\\Tools", ((DirectoryItem)restored.Tabs[0].Children[0]).Target);
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
        var tab = new LauncherTab { Name = "Profile" }; tab.Children.Add(new FileItem { Name = "Tool", Target = "C:\\Tools\\Tool.exe", Icon = "icon/sample.png" }); var data = Data(tab); data.Appearance = new AppearanceSettings { Theme = "custom", GroupBackgroundColor = "#112233", GroupForegroundColor = "#445566", LauncherItemBackgroundColor = "#778899", LauncherItemForegroundColor = "#AABBCC" }; app.ReplaceData(data);
        WritePng(Path.Combine(app.Paths.IconSetDirectory, "group_default.png"), System.Drawing.Color.Goldenrod);
        var profile = Path.Combine(path, "profile.ogp"); app.ProfileService.Save(profile);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(profile))
        {
            True(archive.GetEntry("manifest.json") is not null); True(archive.GetEntry($"menus/{tab.Id}.json") is not null); True(archive.GetEntry("icons/sample.png") is not null); True(archive.GetEntry("iconSet/group_default.png") is not null);
            using var reader = new StreamReader(archive.GetEntry($"menus/{tab.Id}.json")!.Open()); True(reader.ReadToEnd().Contains("icons/sample.png", StringComparison.Ordinal));
        }
        WritePng(iconPath, System.Drawing.Color.Blue);
        File.Delete(Path.Combine(app.Paths.IconSetDirectory, "group_default.png"));
        var loaded = app.ProfileService.Load(profile); var item = (FileItem)loaded.Tabs[0].Children[0];
        Equal("icon/sample_2.png", item.Icon); True(File.Exists(Path.Combine(path, "icon", "sample_2.png"))); True(File.Exists(Path.Combine(app.Paths.IconSetDirectory, "group_default.png"))); Equal("custom", loaded.Appearance.Theme); Equal("#112233", loaded.Appearance.GroupBackgroundColor);
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

static void TestAppearanceSettings()
{
    var custom = new AppearanceSettings { Theme = "CUSTOM", GroupBackgroundColor = "#112233", GroupForegroundColor = "#aabbcc", LauncherItemBackgroundColor = "#445566", LauncherItemForegroundColor = "#778899" };
    AppearanceRules.Validate(custom); Equal("custom", custom.Theme); Equal("#AABBCC", custom.GroupForegroundColor);
    var colors = ThemePalette.Resolve(custom); Equal("#112233", colors.GroupBackground); Equal("#778899", colors.ItemForeground);
    Throws<InvalidDataException>(() => AppearanceRules.Validate(new AppearanceSettings { GroupBackgroundColor = "blue" }));
}

static void TestItemLaunchClickDefaults()
{
    var settings = new ItemLaunchSettings(); Equal(1, settings.FileItemClickCount); Equal(2, settings.DirectoryItemClickCount); Equal(2, settings.UrlItemClickCount);
    Throws<InvalidDataException>(() => new DataValidator().Validate(new OpenGepaData { ItemLaunch = new ItemLaunchSettings { UrlItemClickCount = 3 } }));
    WithStore((_, store) =>
    {
        var data = Data(new LauncherTab { Name = "Main" }); data.ItemLaunch.DirectoryItemClickCount = 1;
        Equal(1, store.Deserialize(store.Serialize(data)).ItemLaunch.DirectoryItemClickCount);
    });
}

static void TestLauncherTabDuplication()
{
    var path = Path.Combine(Path.GetTempPath(), "OpenGepa.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
    try
    {
        var app = AppService.Create(path); app.Initialize();
        var group = new GroupNode { Name = "Tools", Icon = "icon/tools.png" };
        var file = new FileItem { Name = "Tool.exe", Target = "C:\\Tools\\Tool.exe", Icon = "icon/tool.png" }; group.Children.Add(file);
        var tab = new LauncherTab { Name = "Launcher", Icon = "icon/tab.png", Children = new ObservableCollection<LauncherNode> { group } };
        app.ReplaceData(Data(tab));
        True(app.TryDuplicateTab(tab.Id, out var newId, out var error), error);
        var clone = app.Data.Tabs.Single(x => x.Id == newId); Equal("Launcher (2)", clone.Name); Equal(tab.Icon, clone.Icon);
        var cloneGroup = (GroupNode)clone.Children[0]; var cloneFile = (FileItem)cloneGroup.Children[0]; Equal(group.Icon, cloneGroup.Icon); Equal(file.Icon, cloneFile.Icon); Equal(file.Target, cloneFile.Target);
        True(clone.Id != tab.Id && cloneGroup.Id != group.Id && cloneFile.Id != file.Id);
        True(app.TryDuplicateTab(clone.Id, out var thirdId, out error), error);
        Equal("Launcher (3)", app.Data.Tabs.Single(x => x.Id == thirdId).Name);
    }
    finally { Directory.Delete(path, true); }
}

static void TestCrossLauncherMove()
{
    var sourceItem = new FileItem { Name = "Tool.exe", Target = "C:\\Tools\\Tool.exe", Order = 0 };
    var source = new LauncherTab { Name = "Source", Order = 0, Children = new ObservableCollection<LauncherNode> { sourceItem } };
    var destinationGroup = new GroupNode { Name = "Destination", Order = 0 };
    var destination = new LauncherTab { Name = "Target", Order = 1, Children = new ObservableCollection<LauncherNode> { destinationGroup } };
    var data = new OpenGepaData { SelectedTabId = source.Id, Tabs = new ObservableCollection<LauncherTab> { source, destination } };
    EditorWindow.MoveNodes(data, source.Id, destination.Id, [sourceItem.Id], destinationGroup.Id, null, false);
    Equal(0, source.Children.Count); Equal(1, destinationGroup.Children.Count); True(ReferenceEquals(sourceItem, destinationGroup.Children[0])); Equal(0, destinationGroup.Children[0].Order);
    new DataValidator().Validate(data);
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
            var window = new EditorWindow(app, tab.Id) { ShowInTaskbar = false, Left = -10000, Top = -10000, Opacity = 0 }; window.Show(); window.RefreshData(); window.UpdateLayout();
            var tree = (System.Windows.Controls.TreeView)window.FindName("EditorTree"); tree.UpdateLayout();
            var root = (System.Windows.Controls.TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(tree.Items[0]); True(root.DataContext is EditorRootNode); root.IsExpanded = true; tree.UpdateLayout();
            var before = (System.Windows.Controls.TreeViewItem)root.ItemContainerGenerator.ContainerFromItem(root.Items[0]); before.IsExpanded = true; tree.UpdateLayout();
            True(app.TryCommit(data => ((FileItem)((GroupNode)data.Tabs[0].Children[0]).Children[0]).Name = "Renamed.exe", out var error), error);
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
