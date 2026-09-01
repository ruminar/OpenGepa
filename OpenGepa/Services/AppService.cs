using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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

    private AppService(AppPaths paths, DataStore store)
    {
        Paths = paths; Store = store;
        IconService = new IconService(paths);
        IconSetService = new IconSetService(paths, IconService);
        SiteIconService = new SiteIconService(IconService);
        LaunchService = new LaunchService(this);
        StartupService = new StartupService();
        ProfileService = new ProfileService(this);
    }

    public AppPaths Paths { get; }
    public DataStore Store { get; }
    public IconService IconService { get; }
    public IconSetService IconSetService { get; }
    public SiteIconService SiteIconService { get; }
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

    public bool TryCommit(Action<OpenGepaData> change, out string error)
    {
        try
        {
            var candidate = Store.Clone(Data); change(candidate); Store.Save(candidate); Data = candidate; ThemePalette.Apply(Data.Appearance);
            DataChanged?.Invoke(this, EventArgs.Empty); error = ""; return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public void ReplaceData(OpenGepaData data)
    {
        Store.Save(data); Data = data; ThemePalette.Apply(Data.Appearance); DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectTab(string id)
    {
        if (Data.SelectedTabId == id) return;
        TryCommit(x => x.SelectedTabId = id, out _);
    }

    public void PrepareLauncher()
    {
        _launcher ??= new MainWindow(this);
        _launcher.RefreshData();
    }

    public void ShowLauncher()
    {
        if (_launcher is { IsVisible: true }) { _launcher.Hide(); return; }
        PrepareLauncher(); _launcher!.PositionNearCursor(); _launcher.Show(); _launcher.Activate();
    }

    public void HideLauncher() => _launcher?.Hide();
    public void ShowEditor(string? tabId = null)
    {
        var id = tabId ?? SelectedTab?.Id;
        if (id is null) return;
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
            var baseName = CopyBaseName(source.Name);
            var index = 2;
            var name = $"{baseName} ({index})";
            var existing = data.Tabs.Select(x => NameRules.Normalize(x.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            while (existing.Contains(NameRules.Normalize(name))) name = $"{baseName} ({++index})";
            var clone = LauncherTabCopy.Create(source, name, data.Tabs.Count);
            data.Tabs.Add(clone);
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
        var custom = _app.IconSetService.GetOpenGepaIcon() ?? _app.Data?.DefaultIcons?.TrayIcon;
        _icon.Icon = _app.IconService.TryLoadIcon(custom) ?? Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OpenGepa.exe")) ?? SystemIcons.Application;
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
            var target = item switch { NamedLauncherItem named => named.Target, DirectoryItem directory => directory.Target, _ => throw new InvalidDataException("起動できない項目です。") };
            var repaired = false;
            if (item is FileItem file)
            {
                if (!File.Exists(target))
                {
                    target = FindMovedFile(target) ?? throw new FileNotFoundException("登録されたファイルが見つかりません。", target);
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
                if (!_app.TryCommit(data => FindItem(data, repairedFile.Id)!.Target = newTarget, out var saveError))
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
    public Icon? TryLoadIcon(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(_paths.BaseDirectory, relative));
            var iconRoot = _paths.IconDirectory + Path.DirectorySeparatorChar; var iconSetRoot = _paths.IconSetDirectory + Path.DirectorySeparatorChar;
            if ((!path.StartsWith(iconRoot, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(iconSetRoot, StringComparison.OrdinalIgnoreCase)) || !File.Exists(path)) return null;
            if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase)) { using var ico = new Icon(path); return (Icon)ico.Clone(); }
            using var image = Image.FromFile(path); using var bitmap = new Bitmap(image, new System.Drawing.Size(32, 32)); var handle = bitmap.GetHicon();
            try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); } finally { DestroyIcon(handle); }
        }
        catch { return null; }
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
        var icons = new[] { IntPtr.Zero }; var ids = new uint[1];
        try
        {
            if (PrivateExtractIcons(target, 0, 256, 256, icons, ids, 1, 0) == 0 || icons[0] == IntPtr.Zero) return null;
            using var borrowed = Icon.FromHandle(icons[0]); return (Icon)borrowed.Clone();
        }
        catch { return null; }
        finally { if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]); }
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

    public string? GetAppIcon(LauncherTab tab, IEnumerable<LauncherTab> tabs)
    {
        var icons = GetAppIcons(); if (icons.Count == 0) return null;
        var ordered = tabs.OrderBy(x => x.Order).ToList(); var index = ordered.FindIndex(x => x.Id.Equals(tab.Id, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : icons[index % icons.Count];
    }

    private IReadOnlyList<string> GetAppIcons()
    {
        if (!Directory.Exists(_paths.IconSetDirectory)) return [];
        return Directory.EnumerateFiles(_paths.IconSetDirectory, "*.png", SearchOption.TopDirectoryOnly)
            .Select(path => new { Path = path, Name = Path.GetFileNameWithoutExtension(path) })
            .Select(x => System.Text.RegularExpressions.Regex.Match(x.Name, @"^appIcon([1-9]\d*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is var match && match.Success ? new { x.Path, Number = int.Parse(match.Groups[1].Value) } : null)
            .Where(x => x is not null).Select(x => x!)
            .OrderBy(x => x.Number).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => Path.GetRelativePath(_paths.BaseDirectory, x.Path).Replace('\\', '/')).ToList();
    }
}

public sealed class SiteIconService
{
    private readonly IconService _icons;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(5) };
    public SiteIconService(IconService icons) => _icons = icons;
    public async Task<string?> TryFetchAsync(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
        try
        {
            var iconUri = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/favicon.ico");
            using var response = await Client.GetAsync(iconUri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 1_048_576) return null;
            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var limited = new MemoryStream(); var buffer = new byte[81920]; var total = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer); if (read == 0) break;
                total += read; if (total > 1_048_576) return null;
                await limited.WriteAsync(buffer.AsMemory(0, read));
            }
            limited.Position = 0; return _icons.ImportImage(limited, name);
        }
        catch { return null; }
    }
}
