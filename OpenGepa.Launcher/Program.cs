using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using OpenGepa;
using OpenGepa.Update;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.GetDirectoryName(Environment.ProcessPath!)!;
        InstanceIdentity.ConfigureRoot(root);
        using var hostMutex = new Mutex(true, $@"Local\OpenGepa.TrayHost.{InstanceIdentity.CurrentId}", out var first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(InstanceIdentity.ShowEventName, out var show))
            {
                using (show) show.Set();
            }
            return 0;
        }

        var layout = new UpdateLayout(root);
        try
        {
            layout.EnsureDirectories();
            var state = UpdateLayout.Read<InstallationState>(layout.StatePath);
            var version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
            if (state is null || UpdatePackage.ParseVersion(version) > UpdatePackage.ParseVersion(state.Current))
            {
                RequireApplication(layout, version);
                if (state is null) MigrateLegacyData(layout);
                state = new InstallationState(version, state?.Current);
                layout.WriteState(state);
            }

            Application.EnableVisualStyles();
            using var tray = new TrayHost(layout.DataDirectory);
            var worker = Task.Run(() => RunApplicationLoop(layout, state, tray));
            using var lifetimeTimer = new System.Windows.Forms.Timer { Interval = 100 };
            lifetimeTimer.Tick += (_, _) => { if (worker.IsCompleted) Application.ExitThread(); };
            lifetimeTimer.Start();
            Application.Run();
            return worker.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception or JsonException)
        {
            MessageBox.Show(ex.Message, "OpenGepaを起動できません", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            hostMutex.ReleaseMutex();
        }
    }

    private static int RunApplicationLoop(UpdateLayout layout, InstallationState state, TrayHost tray)
    {
        while (true)
        {
            RequireApplication(layout, state.Current);
            var info = new ProcessStartInfo(layout.ApplicationPath(state.Current))
            {
                WorkingDirectory = layout.Root,
                UseShellExecute = false,
                RedirectStandardInput = true
            };
            info.ArgumentList.Add("--portable-tray-host");
            info.ArgumentList.Add(layout.Root);
            using var process = Process.Start(info) ?? throw new IOException("OpenGepaを起動できません。");
            tray.Connect(process);
            try { process.WaitForExit(); }
            finally { tray.Connect(null); }
            if (process.ExitCode != UpdateLayout.ApplyExitCode) return process.ExitCode;
            state = ApplyPending(layout, state);
        }
    }

    static InstallationState ApplyPending(UpdateLayout layout, InstallationState current)
    {
        var pending = UpdateLayout.Read<PendingUpdate>(layout.PendingPath) ?? throw new IOException("更新の準備が見つかりません。");
        var profile = UpdateProfile.Load(Assembly.GetExecutingAssembly()) ?? throw new InvalidDataException("この配布物には更新用公開鍵がありません。");
        if (UpdatePackage.ParseVersion(pending.Version) is not { } next || next <= UpdatePackage.ParseVersion(current.Current)) throw new InvalidDataException("更新先の版が正しくありません。");
        var work = layout.WorkDirectory(pending.WorkDirectory);
        var manifest = File.ReadAllBytes(Path.Combine(work, UpdatePackage.ManifestName));
        var signatures = File.ReadAllBytes(Path.Combine(work, UpdatePackage.SignatureName));
        var verified = UpdatePackage.ReadManifest(manifest, signatures, profile, pending.Version);
        var extracted = Path.Combine(work, "extracted");
        Directory.CreateDirectory(extracted);
        using (var zip = File.OpenRead(Path.Combine(work, "package.zip")))
        {
            UpdatePackage.VerifyZip(zip, verified);
            UpdatePackage.Extract(zip, extracted, pending.Version);
        }
        var staged = Path.Combine(extracted, "app", "versions", pending.Version);
        RequireDirectory(staged);
        Directory.Move(staged, layout.VersionDirectory(pending.Version));
        RequireApplication(layout, pending.Version);
        layout.WriteState(new InstallationState(pending.Version, current.Current));
        File.Delete(layout.PendingPath);
        return new InstallationState(pending.Version, current.Current);
    }

    static void MigrateLegacyData(UpdateLayout layout)
    {
        if (Directory.Exists(layout.DataDirectory)) return;
        var data = layout.DataDirectory;
        Directory.CreateDirectory(data);
        foreach (var name in new[] { "opengepa.json", "opengepa.backup.json", "opengepa.lastgood.json", "opengepa.usage.json" }) CopyIfExists(Path.Combine(layout.Root, name), Path.Combine(data, name));
        foreach (var name in new[] { "icon", "iconSet", "shortcut" }) CopyDirectoryIfExists(Path.Combine(layout.Root, name), Path.Combine(data, name));
        File.WriteAllText(Path.Combine(data, ".legacy-imported"), "");
    }

    static void CopyIfExists(string source, string destination) { if (File.Exists(source)) File.Copy(source, destination, false); }
    static void CopyDirectoryIfExists(string source, string destination) { if (!Directory.Exists(source)) return; foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) { var target = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, false); } }
    static void RequireApplication(UpdateLayout layout, string version) { UpdateLayout.RequireDirectory(layout.VersionDirectory(version)); UpdateLayout.RequireFile(layout.ApplicationPath(version)); UpdateLayout.RequireFile(layout.McpPath(version)); }
    static void RequireDirectory(string path) => UpdateLayout.RequireDirectory(path);
}

internal sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _iconTimer = new() { Interval = 2000 };
    private readonly string _dataDirectory;
    private sealed record TrayConnection(StreamWriter Commands, int ProcessId);
    private TrayConnection? _connection;
    private (string? Path, DateTime Written)? _iconKey;
    private (DateTime Written, string? IconPath)? _legacyIcon;
    private bool _commandPending;

    public TrayHost(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        _menu.Items.Add("設定", null, async (_, _) => await SendCommandAsync("settings"));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("終了", null, async (_, _) => await SendCommandAsync("exit"));
        _icon.Text = "OpenGepa";
        _icon.ContextMenuStrip = _menu;
        _icon.MouseClick += async (_, e) => { if (e.Button == MouseButtons.Left) await SendCommandAsync("toggle"); };
        RefreshIcon();
        _icon.Visible = true;
        _iconTimer.Tick += (_, _) => RefreshIcon();
        _iconTimer.Start();
    }

    public void Connect(Process? process) =>
        Volatile.Write(ref _connection, process is null ? null : new TrayConnection(process.StandardInput, process.Id));

    private async Task SendCommandAsync(string command)
    {
        var connection = Volatile.Read(ref _connection);
        if (connection is null || _commandPending) return;
        _commandPending = true;
        try
        {
            // Windows can give the tray click's foreground permission to the child process.
            if (command == "toggle") AllowSetForegroundWindow(connection.ProcessId);
            await connection.Commands.WriteLineAsync(command);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { _commandPending = false; }
    }

    private string? FindCustomIcon()
    {
        var iconSet = Path.Combine(_dataDirectory, "iconSet");
        var ico = Path.Combine(iconSet, "OpenGepa.ico");
        if (File.Exists(ico)) return ico;
        var png = Path.Combine(iconSet, "OpenGepa.png");
        if (File.Exists(png)) return png;

        var configuration = Path.Combine(_dataDirectory, "opengepa.json");
        var written = File.GetLastWriteTimeUtc(configuration);
        if (_legacyIcon is { } cached && cached.Written == written) return cached.IconPath;

        string? path = null;
        if (File.Exists(configuration))
        {
            using var stream = File.Open(configuration, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var json = JsonDocument.Parse(stream);
            if (json.RootElement.TryGetProperty("defaultIcons", out var defaults) &&
                defaults.ValueKind == JsonValueKind.Object &&
                defaults.TryGetProperty("trayIcon", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 } relative)
            {
                var candidate = Path.GetFullPath(relative, _dataDirectory);
                foreach (var directory in new[] { Path.Combine(_dataDirectory, "icon"), iconSet })
                {
                    if (candidate.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        path = candidate;
                        break;
                    }
                }
            }
        }

        _legacyIcon = (written, path);
        return path;
    }

    private void RefreshIcon()
    {
        string? path = null;
        try { path = FindCustomIcon(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { }

        var key = (path, path is null ? DateTime.MinValue : File.GetLastWriteTimeUtc(path));
        if (_iconKey == key) return;
        _iconKey = key;
        var next = LoadCustomIcon(path) ?? Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
        var previous = _icon.Icon;
        _icon.Icon = next;
        previous?.Dispose();
    }

    private static Icon? LoadCustomIcon(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var size = SystemInformation.SmallIconSize;
            if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase)) return new Icon(path, size);

            using var image = Image.FromFile(path);
            using var bitmap = new Bitmap(image, size);
            var handle = bitmap.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _iconTimer.Dispose();
        var image = _icon.Icon;
        _icon.Dispose();
        image?.Dispose();
        _menu.Dispose();
    }
}
