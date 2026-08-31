using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.IO;
using System.IO.Compression;
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
        LaunchService = new LaunchService(this);
        StartupService = new StartupService();
        ProfileService = new ProfileService(this);
    }

    public AppPaths Paths { get; }
    public DataStore Store { get; }
    public IconService IconService { get; }
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
        _app = app; _icon.Text = "OpenGepa"; _icon.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OpenGepa.exe")) ?? SystemIcons.Application;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("設定", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(_app.ShowSettings));
        menu.Items.Add("編集", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(() => _app.ShowEditor()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(() => ((App)System.Windows.Application.Current).ExitApplication()));
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) System.Windows.Application.Current.Dispatcher.Invoke(_app.ShowLauncher); };
    }
    public void Show() => _icon.Visible = true;
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}

public sealed class LaunchService
{
    private readonly AppService _app;
    public LaunchService(AppService app) => _app = app;
    public async Task<(bool Success, string Error)> LaunchAsync(LauncherItem item)
    {
        try
        {
            var target = item.Target;
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

            await Task.Run(() => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })
                ?? throw new InvalidOperationException("起動要求を開始できませんでした。"));
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

    private static LauncherItem? FindItem(OpenGepaData data, string id)
    {
        foreach (var tab in data.Tabs) { var found = Find(tab.Children, id); if (found is not null) return found; } return null;
        static LauncherItem? Find(IEnumerable<LauncherNode> nodes, string id)
        { foreach (var n in nodes) { if (n is LauncherItem i && i.Id == id) return i; if (n is GroupNode g) { var f = Find(g.Children, id); if (f is not null) return f; } } return null; }
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
