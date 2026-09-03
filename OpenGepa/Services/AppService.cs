using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using OpenGepa.Models;

namespace OpenGepa.Services;

public sealed class AppService
{
    private MainWindow? _launcher;
    private readonly Dictionary<string, EditorWindow> _editors = new(StringComparer.OrdinalIgnoreCase);
    private SettingsWindow? _settings;
    private long _persistenceVersion;

    private AppService(AppPaths paths, DataStore store)
    {
        Paths = paths; Store = store; DataSaveQueue = new DataSaveQueue(store);
        IconService = new IconService(paths);
        IconSetService = new IconSetService(paths, IconService);
        SiteIconService = new SiteIconService(IconService);
        BookmarkIconQueue = new BookmarkIconQueue(IconService, SiteIconService, ApplyBookmarkIcons);
        WindowsMenuService = new WindowsMenuService(paths);
        StoreAppsService = new StoreAppsService();
        PresetService = new PresetService(StoreAppsService);
        ManagedShortcutService = new ManagedShortcutService(paths);
        WebBookmarkService = new WebBookmarkService();
        LaunchService = new LaunchService(this);
        StartupService = new StartupService();
        ProfileService = new ProfileService(this);
    }

    public AppPaths Paths { get; }
    public DataStore Store { get; }
    public DataSaveQueue DataSaveQueue { get; }
    public IconService IconService { get; }
    public IconSetService IconSetService { get; }
    public SiteIconService SiteIconService { get; }
    public BookmarkIconQueue BookmarkIconQueue { get; }
    public WindowsMenuService WindowsMenuService { get; }
    public StoreAppsService StoreAppsService { get; }
    public PresetService PresetService { get; }
    public ManagedShortcutService ManagedShortcutService { get; }
    public WebBookmarkService WebBookmarkService { get; }
    public LaunchService LaunchService { get; }
    public StartupService StartupService { get; }
    public ProfileService ProfileService { get; }
    public OpenGepaData Data { get; private set; } = null!;
    public event EventHandler? DataChanged;

    public static AppService Create(string? baseDirectory = null)
    {
        var paths = new AppPaths(baseDirectory);
        return new AppService(paths, new DataStore(paths, new DataValidator()));
    }

    public void Initialize()
    {
        Paths.EnsureWritable();
        var result = Store.Load(); Data = result.Data; ThemePalette.Apply(Data.Appearance);
        if (result.Source is DataSource.Backup or DataSource.LastGood)
            MessageBox.Show($"{result.Source}から設定を復旧しました。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public IReadOnlyList<LauncherTab> VisibleTabs => Data.Tabs.Where(x => x.IsVisible).OrderBy(x => x.Order).ToList();
    public LauncherTab? SelectedTab => VisibleTabs.FirstOrDefault(x => x.Id == Data.SelectedTabId) ?? VisibleTabs.FirstOrDefault();

    public ObservableCollection<LauncherNode> GetDisplayChildren(LauncherTab tab, bool refresh = false)
    {
        if (!tab.IsSystemTab) return tab.Children;
        if (!refresh && tab.RuntimeChildren is not null) return tab.RuntimeChildren;
        tab.RuntimeChildren = tab.Kind switch
        {
            LauncherTabKinds.WindowsMenu => WindowsMenuService.Load(Data.WindowsMenu),
            LauncherTabKinds.StoreApps => StoreAppsService.Load(refresh),
            LauncherTabKinds.Presets => PresetService.Load(Data.Presets),
            _ => tab.RuntimeChildren
        };
        return tab.DisplayChildren;
    }

    public bool TryCommit(Action<OpenGepaData> change, out string error)
    {
        try
        {
            var candidate = Store.Clone(Data); change(candidate); DataSaveQueue.SaveNowAsync(candidate, NextPersistenceVersion()).GetAwaiter().GetResult(); Data = candidate; ThemePalette.Apply(Data.Appearance);
            DataChanged?.Invoke(this, EventArgs.Empty); error = ""; return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public bool TrySetLauncherPinned(bool pinned, out string error)
    {
        if (Data.IsLauncherPinned == pinned) { error = string.Empty; return true; }
        try
        {
            Data.IsLauncherPinned = pinned;
            RequestDeferredSave();
            DataChanged?.Invoke(this, EventArgs.Empty);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void ReplaceData(OpenGepaData data)
    {
        DataSaveQueue.SaveNowAsync(data, NextPersistenceVersion()).GetAwaiter().GetResult(); Data = data; ThemePalette.Apply(Data.Appearance); DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectTab(string id)
    {
        if (Data.SelectedTabId == id) return;
        Data.SelectedTabId = id;
        RequestDeferredSave();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>アプリ終了前に、選択タブなどの遅延保存を確実に完了します。</summary>
    public void FlushPersistence() => DataSaveQueue.Flush();

    private long NextPersistenceVersion() => ++_persistenceVersion;

    private void RequestDeferredSave() => DataSaveQueue.RequestDeferredSave(Store.Clone(Data), NextPersistenceVersion());

    public void QueueBookmarkIcons(string tabId, IEnumerable<BookmarkIconCandidate> candidates) => BookmarkIconQueue.Enqueue(tabId, candidates);
    public bool QueueSpecifiedBookmarkIcon(string tabId, UrlItem item, string iconAddress)
    {
        if (!TryResolveBookmarkIconUrl(item.Target, iconAddress, out var iconUrl)) return false;
        BookmarkIconQueue.Enqueue(tabId, [new BookmarkIconCandidate(item.Id, item.Name, item.Target, null, iconUrl, ReplaceExisting: true, DirectOnly: true)], webLimit: 1);
        return true;
    }
    public static bool TryResolveBookmarkIconUrl(string pageUrl, string iconAddress, out string iconUrl)
    {
        iconUrl = string.Empty;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps) || !Uri.TryCreate(page, iconAddress.Trim(), out var icon) || (icon.Scheme != Uri.UriSchemeHttp && icon.Scheme != Uri.UriSchemeHttps)) return false;
        iconUrl = icon.AbsoluteUri;
        return true;
    }
    public void QueueMissingGroupIcons(string tabId, string groupId)
    {
        var tab = Data.Tabs.FirstOrDefault(item => item.Id == tabId);
        if (tab?.IsWebTab != true || FindNode(tab.Children, groupId) is not GroupNode group) return;
        BookmarkIconQueue.EnqueueMissing(tabId, Walk(group.Children).OfType<UrlItem>());
    }
    private void ApplyBookmarkIcons(IReadOnlyList<BookmarkIconResult> results)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() => TryCommit(data =>
        {
            foreach (var result in results)
            {
                var tab = data.Tabs.FirstOrDefault(item => item.Id == result.TabId);
                if (tab is not null && FindNode(tab.Children, result.ItemId) is UrlItem item && (result.ReplaceExisting || item.Icon is null)) item.Icon = result.IconPath;
            }
        }, out _));
    }
    private static LauncherNode? FindNode(IEnumerable<LauncherNode> nodes, string id) { foreach (var node in nodes) { if (node.Id == id) return node; if (node is GroupNode group && FindNode(group.Children, id) is LauncherNode found) return found; } return null; }
    private static IEnumerable<LauncherNode> Walk(IEnumerable<LauncherNode> nodes) { foreach (var node in nodes) { yield return node; if (node is GroupNode group) foreach (var child in Walk(group.Children)) yield return child; } }

    public void PrepareLauncher()
    {
        _launcher ??= new MainWindow(this);
        _launcher.RefreshData(true);
    }

    public void ShowLauncher()
    {
        PrepareLauncher();
        if (_launcher!.IsVisible)
        {
            if (Data.IsLauncherPinned && !_launcher.IsActive) { if (_launcher.WindowState == WindowState.Minimized) _launcher.WindowState = WindowState.Normal; _launcher.Activate(); return; }
            _launcher.Hide(); return;
        }
        _launcher.PositionNearCursor(); _launcher.Show(); _launcher.Activate();
    }

    public void HideLauncher() => _launcher?.Hide();
    public void ShowEditor(string? tabId = null)
    {
        var id = tabId ?? SelectedTab?.Id;
        if (id is null) return;
        if (Data.Tabs.FirstOrDefault(tab => tab.Id == id)?.IsSystemTab == true) return;
        if (!_editors.TryGetValue(id, out var editor))
        {
            editor = new EditorWindow(this, id);
            _editors.Add(id, editor);
        }
        editor.RefreshData(); editor.Show(); editor.Activate();
    }

    public bool TryDuplicateTab(string sourceId, out string newTabId, out string error)
    {
        var createdId = "";
        var succeeded = TryCommit(data =>
        {
            var source = data.Tabs.FirstOrDefault(x => x.Id == sourceId) ?? throw new InvalidDataException("複製元のApp Launcherが見つかりません。");
            if (source.IsSystemTab) throw new InvalidDataException("特殊タブは複製できません。");
            var baseName = CopyBaseName(source.Name);
            var index = 2;
            var name = $"{baseName} ({index})";
            var existing = data.Tabs.Select(x => NameRules.Normalize(x.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            while (existing.Contains(NameRules.Normalize(name))) name = $"{baseName} ({++index})";
            var nextOrder = data.Tabs.Select(tab => tab.Order).DefaultIfEmpty(-1).Max() + 1;
            var clone = LauncherTabCopy.Create(source, name, nextOrder);
            data.Tabs.Add(clone);
            BuiltInTabs.Ensure(data);
            createdId = clone.Id;
        }, out error);
        newTabId = createdId;
        return succeeded;
    }

    private static string CopyBaseName(string name)
    {
        var normalized = NameRules.Normalize(name);
        var start = normalized.LastIndexOf(" (", StringComparison.Ordinal);
        if (start <= 0 || !normalized.EndsWith(')') || !int.TryParse(normalized[(start + 2)..^1], out var index) || index < 2) return normalized;
        return normalized[..start];
    }
    public void ShowSettings()
    {
        _settings ??= new SettingsWindow(this);
        _settings.RefreshData(); _settings.Show(); _settings.Activate();
    }
}

public sealed class TrayService : IDisposable
{
    private readonly AppService _app;
    private readonly System.Windows.Forms.NotifyIcon _icon = new();
    public TrayService(AppService app)
    {
        _app = app; _icon.Text = "OpenGepa"; RefreshIcon(); _app.DataChanged += (_, _) => RefreshIcon();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("設定", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(_app.ShowSettings));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(() => ((App)System.Windows.Application.Current).ExitApplication()));
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) System.Windows.Application.Current.Dispatcher.Invoke(_app.ShowLauncher); };
    }
    public void Show() => _icon.Visible = true;
    private void RefreshIcon()
    {
        var size = Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Width); var custom = _app.IconSetService.GetOpenGepaIcon() ?? _app.Data?.DefaultIcons?.TrayIcon;
        _icon.Icon = _app.IconService.TryLoadIcon(custom, size) ?? _app.IconService.TryExtractIcon(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OpenGepa.exe"), size) ?? SystemIcons.Application;
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}

public sealed class LaunchService
{
    private readonly AppService _app;
    public LaunchService(AppService app) => _app = app;
    public async Task<(bool Success, string Error)> LaunchAsync(LauncherNode item)
    {
        try
        {
            if (item is StoreAppItem storeApp) return await StoreAppsService.LaunchAsync(storeApp.Aumid);
            if (item is PresetItem preset) return await _app.PresetService.LaunchAsync(preset);
            if (item is WindowsMenuShortcutItem windowsMenu)
            {
                if (!File.Exists(windowsMenu.Target)) throw new FileNotFoundException("Start Menu のショートカットが見つかりません。", windowsMenu.Target);
                await Task.Run(() => Process.Start(new ProcessStartInfo(windowsMenu.Target) { UseShellExecute = true }));
                return (true, string.Empty);
            }
            var target = item switch { NamedLauncherItem named => named.Target, DirectoryItem directory => directory.Target, _ => throw new InvalidDataException("起動できない項目です。") };
            var repaired = false;
            if (item is FileItem file)
            {
                if (!File.Exists(target))
                {
                    if (file.IsTargetMissing || FindMovedFile(target) is not string moved) { _app.TryCommit(data => ((FileItem)FindItem(data, file.Id)!).IsTargetMissing = true, out _); throw new FileNotFoundException("登録されたファイルが見つかりません。", target); }
                    target = moved;
                    repaired = true;
                }
            }
            else if (item is DirectoryItem && !Directory.Exists(target)) throw new DirectoryNotFoundException($"ディレクトリが見つかりません: {target}");
            else if (item is UrlItem && (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
                throw new InvalidDataException("HTTPまたはHTTPSのURLではありません。");

            await Task.Run(() => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }));
            if (repaired && item is FileItem repairedFile)
            {
                var newTarget = target;
                if (!_app.TryCommit(data => { var found = (FileItem)FindItem(data, repairedFile.Id)!; found.Target = newTarget; found.IsTargetMissing = false; }, out var saveError))
                    return (false, "起動には成功しましたが、補正したパスを保存できませんでした: " + saveError);
            }
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public bool OpenProperties(IntPtr owner, string target) => SHObjectProperties(owner, 0x2, target, null);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string objectName, string? propertyName);

    private static NamedLauncherItem? FindItem(OpenGepaData data, string id)
    {
        foreach (var tab in data.Tabs) { var found = Find(tab.Children, id); if (found is not null) return found; } return null;
        static NamedLauncherItem? Find(IEnumerable<LauncherNode> nodes, string id)
        { foreach (var n in nodes) { if (n is NamedLauncherItem i && i.Id == id) return i; if (n is GroupNode g) { var f = Find(g.Children, id); if (f is not null) return f; } } return null; }
    }

    private static string? FindMovedFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\", StringComparison.Ordinal)) return null;
        var root = Path.GetPathRoot(path); if (string.IsNullOrEmpty(root)) return null;
        var relative = Path.GetRelativePath(root, path);
        foreach (var letter in Enumerable.Range('C', 24).Select(x => (char)x))
        {
            try
            {
                var drive = new DriveInfo($"{letter}:\\");
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                var candidate = Path.Combine(drive.RootDirectory.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception) { }
        }
        return null;
    }
}

public sealed class IconService
{
    private readonly AppPaths _paths;
    public IconService(AppPaths paths) => _paths = paths;
    public string? TryExtract(string target, string name)
    {
        try
        {
            using var icon = TryExtractLargeIcon(target) ?? Icon.ExtractAssociatedIcon(target); if (icon is null) return null;
            using var bitmap = icon.ToBitmap(); return SaveAsPng(bitmap, name);
        }
        catch { return null; }
    }
    public string ImportImage(string source, string name)
    {
        using var image = Image.FromFile(source); return SaveAsPng(image, name);
    }
    public string ImportImage(Stream source, string name)
    {
        using var image = Image.FromStream(source); return SaveAsPng(image, name);
    }
    public Icon? TryLoadIcon(string? relative, int size = 32)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(_paths.BaseDirectory, relative));
            var iconRoot = _paths.IconDirectory + Path.DirectorySeparatorChar; var iconSetRoot = _paths.IconSetDirectory + Path.DirectorySeparatorChar;
            if ((!path.StartsWith(iconRoot, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(iconSetRoot, StringComparison.OrdinalIgnoreCase)) || !File.Exists(path)) return null;
            size = Math.Clamp(size, 16, 256);
            if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase)) { using var ico = new Icon(path, new System.Drawing.Size(size, size)); return (Icon)ico.Clone(); }
            using var image = Image.FromFile(path); using var bitmap = new Bitmap(image, new System.Drawing.Size(size, size)); var handle = bitmap.GetHicon();
            try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); } finally { DestroyIcon(handle); }
        }
        catch { return null; }
    }
    public Icon? TryExtractIcon(string target, int size)
    {
        var icons = new[] { IntPtr.Zero }; var ids = new uint[1];
        try
        {
            size = Math.Clamp(size, 16, 256);
            if (PrivateExtractIcons(target, 0, size, size, icons, ids, 1, 0) == 0 || icons[0] == IntPtr.Zero) return null;
            using var borrowed = Icon.FromHandle(icons[0]); return (Icon)borrowed.Clone();
        }
        catch { return null; }
        finally { if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]); }
    }
    public void ImportTrayIcon(string source, string target)
    {
        if (Path.GetExtension(source).Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            using var icon = new Icon(source); File.Copy(source, target, true); return;
        }
        using var image = Image.FromFile(source); SaveAsIco(image, target);
    }
    private static void SaveAsIco(Image image, string target)
    {
        var max = Math.Max(1, Math.Min(image.Width, image.Height)); var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 256 }.Where(size => size <= max).ToList(); if (sizes.Count == 0) sizes.Add(max);
        var frames = new List<byte[]>();
        foreach (var size in sizes)
        {
            using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb); using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.Transparent); graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            var scale = Math.Min((double)size / image.Width, (double)size / image.Height); var width = image.Width * scale; var height = image.Height * scale; graphics.DrawImage(image, new RectangleF((float)((size - width) / 2), (float)((size - height) / 2), (float)width, (float)height));
            using var frame = new MemoryStream(); bitmap.Save(frame, System.Drawing.Imaging.ImageFormat.Png); frames.Add(frame.ToArray());
        }
        var temporary = target + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)frames.Count); var offset = 6 + (16 * frames.Count);
                for (var index = 0; index < frames.Count; index++) { var size = sizes[index]; writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(frames[index].Length); writer.Write(offset); offset += frames[index].Length; }
                foreach (var frame in frames) writer.Write(frame);
            }
            using var verify = new Icon(temporary); File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private Icon? TryExtractLargeIcon(string target)
    {
        return TryExtractIcon(target, 256);
    }
    private string SaveAsPng(Image image, string name)
    {
        var scale = Math.Min(1d, Math.Min(256d / image.Width, 256d / image.Height)); var width = Math.Max(1, (int)Math.Round(image.Width * scale)); var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var canvas = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(canvas); graphics.Clear(Color.Transparent); graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(image, 0, 0, width, height); var (path, stream) = NewFile(name);
        try
        {
            using (stream) canvas.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            using var verify = Image.FromFile(path); if (verify.Width != width || verify.Height != height || verify.Width > 256 || verify.Height > 256) throw new InvalidDataException("アイコンのPNG変換を検証できませんでした。");
            return Path.GetRelativePath(_paths.BaseDirectory, path).Replace('\\', '/');
        }
        catch { stream.Dispose(); try { File.Delete(path); } catch { } throw; }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(string fileName, int iconIndex, int width, int height, IntPtr[] icons, uint[] iconIds, uint iconCount, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
    private (string Path, FileStream Stream) NewFile(string name)
    {
        var invalid = Path.GetInvalidFileNameChars(); var safe = new string(NameRules.Normalize(name).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        if (safe.Length == 0) safe = "Icon"; if (safe.Length > 80) safe = safe[..80];
        var stem = $"{safe}_{DateTime.Now:yyyyMMdd_HHmmssfff}";
        for (var i = 1; ; i++) { var p = Path.Combine(_paths.IconDirectory, stem + (i == 1 ? "" : $"_{i}") + ".png"); try { return (p, new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None)); } catch (IOException) { } }
    }
}

public sealed class IconSetService
{
    private readonly AppPaths _paths; private readonly IconService _icons;
    public IconSetService(AppPaths paths, IconService icons) { _paths = paths; _icons = icons; }

    public bool HasOpenGepaIcon => File.Exists(Path.Combine(_paths.IconSetDirectory, "OpenGepa.ico"));
    public string? GetOpenGepaIcon() => HasOpenGepaIcon ? "iconSet/OpenGepa.ico" : File.Exists(Path.Combine(_paths.IconSetDirectory, "OpenGepa.png")) ? "iconSet/OpenGepa.png" : null;
    public void SetOpenGepaIcon(string source)
    {
        _icons.ImportTrayIcon(source, Path.Combine(_paths.IconSetDirectory, "OpenGepa.ico"));
    }
    public void DeleteOpenGepaIcon()
    {
        var ico = Path.Combine(_paths.IconSetDirectory, "OpenGepa.ico");
        var legacyPng = Path.Combine(_paths.IconSetDirectory, "OpenGepa.png");
        if (File.Exists(ico)) File.Delete(ico);
        if (File.Exists(legacyPng)) File.Delete(legacyPng);
    }
    public string? GetDefaultNodeIcon(LauncherNode node) => node switch { GroupNode => GetDefaultIcon("group"), DirectoryItem => GetDefaultIcon("directory"), UrlItem => GetDefaultIcon("url"), _ => null };
    public string? GetDefaultIcon(string kind)
    {
        var name = kind + "_default.png"; return File.Exists(Path.Combine(_paths.IconSetDirectory, name)) ? "iconSet/" + name : null;
    }
    public bool HasDefaultIcon(string kind) => GetDefaultIcon(kind) is not null;
    public void SetDefaultIcon(string kind, string source)
    {
        var temporary = _icons.ImportImage(source, kind + "_default"); var sourcePath = Path.Combine(_paths.BaseDirectory, temporary.Replace('/', Path.DirectorySeparatorChar)); var target = Path.Combine(_paths.IconSetDirectory, kind + "_default.png");
        try { File.Copy(sourcePath, target, true); } finally { if (File.Exists(sourcePath)) File.Delete(sourcePath); }
    }
    public void DeleteDefaultIcon(string kind)
    {
        var target = Path.Combine(_paths.IconSetDirectory, kind + "_default.png"); if (File.Exists(target)) File.Delete(target);
    }

    public string? GetAppIcon(LauncherTab tab, IEnumerable<LauncherTab> tabs)
    {
        var systemIcon = tab.Kind switch
        {
            LauncherTabKinds.WindowsMenu => "winMenu.png",
            LauncherTabKinds.StoreApps => "winStore.png",
            LauncherTabKinds.Presets => "winCust.png",
            _ => null
        };
        if (systemIcon is not null) return File.Exists(Path.Combine(_paths.IconSetDirectory, systemIcon)) ? "iconSet/" + systemIcon : null;
        var icons = GetTabIcons(tab.IsWebTab ? "urlIcon" : "appIcon"); if (icons.Count == 0) return null;
        var ordered = tabs.Where(item => item.IsWebTab == tab.IsWebTab && !item.IsSystemTab).OrderBy(x => x.Order).ToList(); var index = ordered.FindIndex(x => x.Id.Equals(tab.Id, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : icons[index % icons.Count];
    }

    private IReadOnlyList<string> GetTabIcons(string prefix)
    {
        if (!Directory.Exists(_paths.IconSetDirectory)) return [];
        return Directory.EnumerateFiles(_paths.IconSetDirectory, "*.png", SearchOption.TopDirectoryOnly)
            .Select(path => new { Path = path, Name = Path.GetFileNameWithoutExtension(path) })
            .Select(x => System.Text.RegularExpressions.Regex.Match(x.Name, $@"^{System.Text.RegularExpressions.Regex.Escape(prefix)}([1-9]\d*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is var match && match.Success ? new { x.Path, Number = int.Parse(match.Groups[1].Value) } : null)
            .Where(x => x is not null).Select(x => x!)
            .OrderBy(x => x.Number).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => Path.GetRelativePath(_paths.BaseDirectory, x.Path).Replace('\\', '/')).ToList();
    }
}

public sealed class SiteIconService
{
    private const int MaxIconBytes = 1_048_576;
    private const int MaxHtmlBytes = 2_097_152;
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36 OpenGepa/0.1";
    private readonly IconService _icons;
    private static readonly HttpClient Client = CreateClient(TimeSpan.FromSeconds(8));
    private static readonly HttpClient BackgroundClient = CreateClient(TimeSpan.FromSeconds(3));
    private static HttpClient CreateClient(TimeSpan timeout) { var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = timeout }; client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent); return client; }
    public SiteIconService(IconService icons) => _icons = icons;
    public async Task<SiteIconFetchResult> TryFetchAsync(string url, string name)
        => await TryFetchAsync(url, name, Client);
    private async Task<SiteIconFetchResult> TryFetchAsync(string url, string name, HttpClient client)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return SiteIconFetchResult.Failed("HTTPまたはHTTPSのURLではありません。");
        var diagnostics = new List<string>();
        try
        {
            var page = await FindPageIconCandidatesAsync(uri, diagnostics, client);
            if (!page.IsSuccess) { diagnostics.Add(""); diagnostics.Add("結果: ページHTMLを取得できなかったため、アイコン候補を試行しませんでした。"); return SiteIconFetchResult.Failed(string.Join(Environment.NewLine, diagnostics)); }
            var candidates = page.Candidates.ToList();
            var fallback = new Uri(page.FinalUri!.GetLeftPart(UriPartial.Authority) + "/favicon.ico");
            if (!candidates.Contains(fallback, UriComparer.Instance)) candidates.Add(fallback);
            diagnostics.Add($"アイコン候補数: {candidates.Count}");
            for (var index = 0; index < candidates.Count; index++)
            {
                var iconUri = candidates[index];
                diagnostics.Add("");
                diagnostics.Add($"[アイコン候補 {index + 1}/{candidates.Count}]");
                diagnostics.Add("Method: GET");
                diagnostics.Add($"URL: {iconUri}");
                diagnostics.Add($"User-Agent: {UserAgent}");
                diagnostics.Add("Accept: image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
                diagnostics.Add($"Referer: {uri}");
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, iconUri);
                    request.Headers.Referrer = uri;
                    request.Headers.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    var finalUri = response.RequestMessage?.RequestUri ?? iconUri;
                    diagnostics.Add($"HTTP: {(int)response.StatusCode} {response.ReasonPhrase}");
                    diagnostics.Add($"最終URL: {finalUri}");
                    diagnostics.Add($"Content-Type: {response.Content.Headers.ContentType?.ToString() ?? "(なし)"}");
                    diagnostics.Add($"Content-Length: {response.Content.Headers.ContentLength?.ToString() ?? "(なし)"}");
                    if (!response.IsSuccessStatusCode) continue;
                    if (response.Content.Headers.ContentLength is > MaxIconBytes) { diagnostics.Add($"結果: サイズ上限 {MaxIconBytes:N0} bytes 超過"); continue; }
                    var (content, overflow) = await ReadLimitedAsync(response.Content, MaxIconBytes);
                    if (overflow) { diagnostics.Add($"結果: 受信中にサイズ上限 {MaxIconBytes:N0} bytes 超過"); continue; }
                    diagnostics.Add($"受信サイズ: {content.Length:N0} bytes");
                    try
                    {
                        content.Position = 0;
                        var imported = _icons.ImportImage(content, name);
                        return SiteIconFetchResult.Succeeded(imported);
                    }
                    catch (Exception ex) { diagnostics.Add($"画像変換/保存: {DescribeException(ex)}"); }
                }
                catch (Exception ex) { diagnostics.Add($"通信エラー: {DescribeException(ex)}"); }
            }
            diagnostics.Add("");
            diagnostics.Add("結果: すべての候補からアイコンを取得できませんでした。");
            return SiteIconFetchResult.Failed(string.Join(Environment.NewLine, diagnostics));
        }
        catch (Exception ex)
        {
            diagnostics.Add($"処理エラー: {DescribeException(ex)}");
            return SiteIconFetchResult.Failed(string.Join(Environment.NewLine, diagnostics));
        }
    }
    public async Task<SiteIconFetchResult> TryFetchBookmarkIconAsync(string pageUrl, string? iconUrl, string name)
    {
        if (string.IsNullOrWhiteSpace(iconUrl) || !Uri.TryCreate(iconUrl, UriKind.Absolute, out var icon) || (icon.Scheme != Uri.UriSchemeHttp && icon.Scheme != Uri.UriSchemeHttps)) return await TryFetchAsync(pageUrl, name, BackgroundClient);
        try
        {
            using var response = await BackgroundClient.GetAsync(icon, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxIconBytes) return await TryFetchAsync(pageUrl, name, BackgroundClient);
            var (content, overflow) = await ReadLimitedAsync(response.Content, MaxIconBytes);
            if (overflow) return await TryFetchAsync(pageUrl, name, BackgroundClient);
            using (content) { content.Position = 0; return SiteIconFetchResult.Succeeded(_icons.ImportImage(content, name)); }
        }
        catch { return await TryFetchAsync(pageUrl, name, BackgroundClient); }
    }
    public async Task<SiteIconFetchResult> TryFetchExplicitIconAsync(string iconUrl, string name)
    {
        if (!Uri.TryCreate(iconUrl, UriKind.Absolute, out var icon) || (icon.Scheme != Uri.UriSchemeHttp && icon.Scheme != Uri.UriSchemeHttps)) return SiteIconFetchResult.Failed("HTTPまたはHTTPSのアイコンURLではありません。");
        try
        {
            using var response = await Client.GetAsync(icon, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxIconBytes) return SiteIconFetchResult.Failed("指定したアイコンURLから画像を取得できませんでした。");
            var (content, overflow) = await ReadLimitedAsync(response.Content, MaxIconBytes);
            if (overflow) return SiteIconFetchResult.Failed("指定したアイコン画像がサイズ上限を超えています。");
            using (content) { content.Position = 0; return SiteIconFetchResult.Succeeded(_icons.ImportImage(content, name)); }
        }
        catch (Exception ex) { return SiteIconFetchResult.Failed(DescribeException(ex)); }
    }
    public async Task<SiteTextFetchResult> TryFetchPageTitleAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return SiteTextFetchResult.Failed();
        try { var html = await Client.GetStringAsync(uri); var title = WebUtility.HtmlDecode(Regex.Match(html, "<title\\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value); title = Regex.Replace(title, "\\s+", " ").Trim(); return string.IsNullOrWhiteSpace(title) ? SiteTextFetchResult.Failed() : SiteTextFetchResult.Succeeded(title); } catch { return SiteTextFetchResult.Failed(); }
    }
    private static async Task<PageIconCandidates> FindPageIconCandidatesAsync(Uri page, List<string> diagnostics, HttpClient client)
    {
        diagnostics.Add("[ページHTML]");
        diagnostics.Add("Method: GET");
        diagnostics.Add($"URL: {page}");
        diagnostics.Add($"User-Agent: {UserAgent}");
        diagnostics.Add("Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, page);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var finalPage = response.RequestMessage?.RequestUri ?? page;
            diagnostics.Add($"HTTP: {(int)response.StatusCode} {response.ReasonPhrase}");
            diagnostics.Add($"最終URL: {finalPage}");
            diagnostics.Add($"Content-Type: {response.Content.Headers.ContentType?.ToString() ?? "(なし)"}");
            diagnostics.Add($"Content-Length: {response.Content.Headers.ContentLength?.ToString() ?? "(なし)"}");
            if (!response.IsSuccessStatusCode) return PageIconCandidates.Failed;
            if (response.Content.Headers.ContentLength is > MaxHtmlBytes) { diagnostics.Add($"HTML解析: サイズ上限 {MaxHtmlBytes:N0} bytes 超過"); return PageIconCandidates.Failed; }
            var (content, overflow) = await ReadLimitedAsync(response.Content, MaxHtmlBytes);
            if (overflow) { diagnostics.Add($"HTML解析: 受信中にサイズ上限 {MaxHtmlBytes:N0} bytes 超過"); return PageIconCandidates.Failed; }
            diagnostics.Add($"受信サイズ: {content.Length:N0} bytes");
            content.Position = 0;
            using var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true);
            var html = await reader.ReadToEndAsync();
            var candidates = ExtractIconCandidates(html, finalPage);
            diagnostics.Add($"HTML内のrel=icon候補: {candidates.Count}");
            foreach (var candidate in candidates) diagnostics.Add($"  {candidate}");
            return new PageIconCandidates(finalPage, candidates);
        }
        catch (Exception ex) { diagnostics.Add($"HTML取得/解析エラー: {DescribeException(ex)}"); return PageIconCandidates.Failed; }
    }
    public static IReadOnlyList<Uri> ExtractIconCandidates(string html, Uri page)
    {
        var baseUri = page;
        var baseTag = Regex.Match(html, "<base\\b[^>]*>", RegexOptions.IgnoreCase);
        var baseHref = baseTag.Success ? ReadHtmlAttribute(baseTag.Value, "href") : null;
        if (!string.IsNullOrWhiteSpace(baseHref) && Uri.TryCreate(page, WebUtility.HtmlDecode(baseHref), out var parsedBase) && parsedBase.IsAbsoluteUri) baseUri = parsedBase;
        var result = new List<Uri>();
        foreach (Match tag in Regex.Matches(html, "<link\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var rel = ReadHtmlAttribute(tag.Value, "rel") ?? "";
            var href = ReadHtmlAttribute(tag.Value, "href") ?? "";
            if (!rel.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(x => x.Contains("icon", StringComparison.OrdinalIgnoreCase))) continue;
            if (!Uri.TryCreate(baseUri, WebUtility.HtmlDecode(href), out var icon) || (icon.Scheme != Uri.UriSchemeHttp && icon.Scheme != Uri.UriSchemeHttps) || icon.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
            if (!result.Contains(icon, UriComparer.Instance)) result.Add(icon);
        }
        return result;
    }
    private static string? ReadHtmlAttribute(string tag, string name)
    {
        var match = Regex.Match(tag, $"\\b{Regex.Escape(name)}\\s*=\\s*(?:['\"](?<quoted>[^'\"]*)['\"]|(?<bare>[^\\s>]+))", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value) : null;
    }
    private static async Task<(MemoryStream Content, bool Overflow)> ReadLimitedAsync(HttpContent content, int limit)
    {
        await using var source = await content.ReadAsStreamAsync();
        var destination = new MemoryStream(); var buffer = new byte[81920]; var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer); if (read == 0) break;
            total += read; if (total > limit) { destination.Dispose(); return (new MemoryStream(), true); }
            await destination.WriteAsync(buffer.AsMemory(0, read));
        }
        destination.Position = 0; return (destination, false);
    }
    private static string DescribeException(Exception exception) => exception.InnerException is null ? $"{exception.GetType().Name}: {exception.Message}" : $"{exception.GetType().Name}: {exception.Message}\n内部例外: {DescribeException(exception.InnerException)}";
    private sealed class UriComparer : IEqualityComparer<Uri>
    {
        public static UriComparer Instance { get; } = new();
        public bool Equals(Uri? x, Uri? y) => string.Equals(x?.AbsoluteUri, y?.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(Uri obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.AbsoluteUri);
    }
    private sealed record PageIconCandidates(Uri? FinalUri, IReadOnlyList<Uri> Candidates)
    {
        public static PageIconCandidates Failed { get; } = new(null, []);
        public bool IsSuccess => FinalUri is not null;
    }
}

public sealed record SiteIconFetchResult(string? IconPath, string? Error)
{
    public bool IsSuccess => IconPath is not null;
    public static SiteIconFetchResult Succeeded(string iconPath) => new(iconPath, null);
    public static SiteIconFetchResult Failed(string error) => new(null, error);
}
public sealed record SiteTextFetchResult(string? Value)
{
    public bool IsSuccess => !string.IsNullOrWhiteSpace(Value);
    public static SiteTextFetchResult Succeeded(string value) => new(value);
    public static SiteTextFetchResult Failed() => new SiteTextFetchResult((string?)null);
}
