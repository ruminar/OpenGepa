using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using OpenGepa.Models;

namespace OpenGepa.Services;

public sealed class WindowsMenuService
{
    private readonly string? _currentProgramsDirectory;
    private readonly string? _allUsersProgramsDirectory;
    public WindowsMenuService(AppPaths paths) { }
    public WindowsMenuService(string currentProgramsDirectory, string allUsersProgramsDirectory)
    {
        _currentProgramsDirectory = Path.GetFullPath(currentProgramsDirectory);
        _allUsersProgramsDirectory = Path.GetFullPath(allUsersProgramsDirectory);
    }

    public ObservableCollection<LauncherNode> Load(WindowsMenuSettings settings)
    {
        var result = ReadSource(CurrentProgramsDirectory, WindowsMenuSource.CurrentUser);
        Merge(result, ReadSource(AllUsersProgramsDirectory, WindowsMenuSource.AllUsers));
        Sort(result, settings.FoldersFirst);
        return result;
    }

    public string CurrentProgramsDirectory => _currentProgramsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    public string AllUsersProgramsDirectory => _allUsersProgramsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);

    public bool CanEdit(WindowsMenuSource source, WindowsMenuSettings settings) => source switch
    {
        WindowsMenuSource.CurrentUser => settings.AllowCurrentUserEdit,
        WindowsMenuSource.AllUsers => settings.AllowAllUsersEdit,
        _ => false
    };

    public string CreateShortcut(WindowsMenuSource source, WindowsMenuSettings settings, string target, string displayName, WindowsMenuGroupNode? parent = null)
    {
        if (!CanEdit(source, settings)) throw new InvalidOperationException("この Start Menu の編集は設定画面で許可されていません。");
        if (!Path.IsPathFullyQualified(target) || !File.Exists(target)) throw new FileNotFoundException("ショートカットの起動対象が見つかりません。", target);
        var root = GetRoot(source); Directory.CreateDirectory(root);
        var directory = parent is null ? root : source == WindowsMenuSource.CurrentUser ? parent.CurrentUserPath : parent.AllUsersPath;
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("選択したGroupはこの Start Menu に存在しません。");
        Directory.CreateDirectory(directory);
        var name = SafeLinkName(displayName);
        var path = UniqueLinkPath(directory, name);
        if (source == WindowsMenuSource.AllUsers)
            ElevatedWindowsMenuHelper.Execute("create", Path.GetRelativePath(root, path), target, null);
        else
            CreateLink(path, target);
        return path;
    }

    public void DeleteShortcut(WindowsMenuShortcutItem item, WindowsMenuSettings settings)
    {
        if (!CanEdit(item.Source, settings)) throw new InvalidOperationException("この Start Menu の編集は設定画面で許可されていません。");
        if (item.Source == WindowsMenuSource.AllUsers)
            ElevatedWindowsMenuHelper.Execute("delete", item.RelativePath, null, null);
        else if (File.Exists(item.Target)) File.Delete(item.Target);
    }

    public void RenameShortcut(WindowsMenuShortcutItem item, WindowsMenuSettings settings, string name)
    {
        if (!CanEdit(item.Source, settings)) throw new InvalidOperationException("この Start Menu の編集は設定画面で許可されていません。");
        var root = GetRoot(item.Source); var newLeaf = SafeLinkName(name); var newPath = Path.Combine(Path.GetDirectoryName(item.Target)!, newLeaf + ".lnk");
        if (File.Exists(newPath)) throw new IOException("同名のショートカットが既にあります。");
        var relative = Path.GetRelativePath(root, newPath);
        if (item.Source == WindowsMenuSource.AllUsers)
            ElevatedWindowsMenuHelper.Execute("rename", item.RelativePath, null, relative);
        else
            File.Move(item.Target, newPath);
    }

    private string GetRoot(WindowsMenuSource source) => source == WindowsMenuSource.CurrentUser ? CurrentProgramsDirectory : AllUsersProgramsDirectory;
    private static ObservableCollection<LauncherNode> ReadSource(string root, WindowsMenuSource source)
    {
        var result = new ObservableCollection<LauncherNode>();
        if (!Directory.Exists(root)) return result;
        ReadDirectory(root, root, source, result);
        return result;
    }

    private static void ReadDirectory(string directory, string root, WindowsMenuSource source, ObservableCollection<LauncherNode> destination)
    {
        try
        {
            foreach (var path in Directory.EnumerateDirectories(directory))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var group = new WindowsMenuGroupNode { Id = RuntimeNodeIds.Create("windows-group:" + relative), Name = Path.GetFileName(path), Order = destination.Count };
                if (source == WindowsMenuSource.CurrentUser) group.CurrentUserPath = path; else group.AllUsersPath = path;
                ReadDirectory(path, root, source, group.Children);
                destination.Add(group);
            }
            foreach (var path in Directory.EnumerateFiles(directory, "*.lnk", SearchOption.TopDirectoryOnly))
            {
                destination.Add(new WindowsMenuShortcutItem
                {
                    Id = RuntimeNodeIds.Create($"windows-shortcut:{source}:{Path.GetRelativePath(root, path).Replace('\\', '/')}"), Name = Path.GetFileNameWithoutExtension(path), Target = path, Source = source,
                    RelativePath = Path.GetRelativePath(root, path), Order = destination.Count
                });
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void Merge(ObservableCollection<LauncherNode> destination, ObservableCollection<LauncherNode> source)
    {
        foreach (var node in source)
        {
            if (node is WindowsMenuGroupNode incoming)
            {
                var existing = destination.OfType<WindowsMenuGroupNode>().FirstOrDefault(group => group.Name.Equals(incoming.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null) destination.Add(incoming);
                else
                {
                    existing.CurrentUserPath ??= incoming.CurrentUserPath;
                    existing.AllUsersPath ??= incoming.AllUsersPath;
                    Merge(existing.Children, incoming.Children);
                }
                continue;
            }
            if (node is WindowsMenuShortcutItem shortcut && destination.OfType<WindowsMenuShortcutItem>().Any(item => item.Name.Equals(shortcut.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            destination.Add(node);
        }
    }

    private static void Sort(ObservableCollection<LauncherNode> nodes, bool foldersFirst)
    {
        foreach (var group in nodes.OfType<WindowsMenuGroupNode>()) Sort(group.Children, foldersFirst);
        var ordered = nodes.OrderBy(node => foldersFirst == (node is WindowsMenuGroupNode) ? 0 : 1)
            .ThenBy(NodeName, StringComparer.Ordinal).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            ordered[index].Order = index;
            var current = nodes.IndexOf(ordered[index]); if (current != index) nodes.Move(current, index);
        }
    }

    private static string NodeName(LauncherNode node) => node switch
    {
        WindowsMenuGroupNode group => group.Name,
        WindowsMenuShortcutItem item => item.Name,
        _ => string.Empty
    };
    private static string SafeLinkName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(NameRules.Normalize(value).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("ショートカット名を入力してください。");
        return result.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? result[..^4] : result;
    }
    private static string UniqueLinkPath(string directory, string name)
    {
        for (var number = 1; ; number++)
        {
            var candidate = Path.Combine(directory, name + (number == 1 ? string.Empty : $"_{number}") + ".lnk");
            if (!File.Exists(candidate)) return candidate;
        }
    }
    internal static void CreateLink(string path, string target)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty;
        link.Save();
    }
}

/// <summary>OpenGepa の shortcut/ にだけ作成する、通常ランチャー用ショートカットです。</summary>
public sealed class ManagedShortcutService
{
    private readonly AppPaths _paths;
    public ManagedShortcutService(AppPaths paths) => _paths = paths;

    public string Create(string target, string displayName)
    {
        if (!Path.IsPathFullyQualified(target) || !File.Exists(target)) throw new FileNotFoundException("ショートカットの起動対象が見つかりません。", target);
        Directory.CreateDirectory(_paths.ShortcutDirectory);
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string(NameRules.Normalize(displayName).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(stem)) stem = "Shortcut";
        if (stem.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        string path;
        for (var index = 1; ; index++)
        {
            path = Path.Combine(_paths.ShortcutDirectory, stem + (index == 1 ? string.Empty : $"_{index}") + ".lnk");
            if (!File.Exists(path)) break;
        }
        WindowsMenuService.CreateLink(path, target);
        return path;
    }
    public void Delete(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = _paths.ShortcutDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !fullPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }
}

public sealed class StoreAppsService
{
    private const string StartAppsCommand = "Get-StartApps | Select-Object Name, AppID | ConvertTo-Json -Compress";
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ja-JP");
    private IReadOnlyList<StoreAppEntry> _last = [];

    public StoreAppsService() { }
    public StoreAppsService(IEnumerable<StoreAppEntry> initialApps) => _last = initialApps.ToList();

    public ObservableCollection<LauncherNode> Load(bool refresh = false)
    {
        if (refresh || _last.Count == 0) _last = Enumerate();
        return BuildGroups(_last);
    }

    public static ObservableCollection<LauncherNode> BuildGroups(IEnumerable<StoreAppEntry> source)
    {
        var result = new ObservableCollection<LauncherNode>();
        var sorted = source.OrderBy(item => item.Name, Comparer<string>.Create((left, right) => Culture.CompareInfo.Compare(left, right, CompareOptions.StringSort))).ToList();
        var chunk = new List<StoreAppEntry>();
        string? previousInitial = null;
        foreach (var app in sorted)
        {
            var initial = Initial(app.Name);
            if (chunk.Count >= 15 && previousInitial is not null && !initial.Equals(previousInitial, StringComparison.Ordinal))
            {
                AddBlock(result, chunk); chunk.Clear();
            }
            chunk.Add(app); previousInitial = initial;
        }
        if (chunk.Count > 0) AddBlock(result, chunk);
        return result;
    }

    public string? FindNvidiaControlPanelAumid()
    {
        return FindAumid("NVIDIA Control Panel", "NVIDIA コントロール パネル");
    }

    public string? FindAumid(params string[] nameFragments) => FindAumid(_last.Count == 0 ? Enumerate() : _last, nameFragments);
    public static string? FindAumid(IEnumerable<StoreAppEntry> source, params string[] nameFragments) => source.FirstOrDefault(item => nameFragments.Any(fragment => item.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))?.Aumid;

    public static async Task<(bool Success, string Error)> LaunchAsync(string aumid)
    {
        try
        {
            await Task.Run(() => Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{aumid}") { UseShellExecute = true }));
            return (true, string.Empty);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private IReadOnlyList<StoreAppEntry> Enumerate()
    {
        try
        {
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) return [];
            using var process = Process.Start(new ProcessStartInfo(powershell, $"-NoProfile -NonInteractive -Command \"{StartAppsCommand}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (process is null) return [];
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(8_000) || process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return [];
            using var document = JsonDocument.Parse(output);
            IEnumerable<JsonElement> elements = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
            return elements.Select(Read).Where(item => item is not null).Select(item => item!).DistinctBy(item => item.Aumid, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return []; }
    }

    private static StoreAppEntry? Read(JsonElement element)
    {
        if (!element.TryGetProperty("Name", out var nameElement) || !element.TryGetProperty("AppID", out var idElement)) return null;
        var name = NameRules.Normalize(nameElement.GetString()); var aumid = NameRules.Normalize(idElement.GetString());
        // Get-StartApps は Start Menu に公開されている項目だけを返す。'!' を持つものだけがパッケージアプリの AUMID。
        return name.Length > 0 && aumid.Contains('!') ? new StoreAppEntry(name, aumid) : null;
    }

    private static void AddBlock(ObservableCollection<LauncherNode> destination, IReadOnlyList<StoreAppEntry> apps)
    {
        var first = Initial(apps[0].Name); var last = Initial(apps[^1].Name);
        var group = new GroupNode { Id = RuntimeNodeIds.Create($"store-group:{destination.Count}:{first}:{last}"), Name = first == last ? first : $"{first}～{last}", Order = destination.Count };
        foreach (var app in apps) group.Children.Add(new StoreAppItem { Id = RuntimeNodeIds.Create("store:" + app.Aumid), Name = app.Name, Aumid = app.Aumid, Order = group.Children.Count });
        destination.Add(group);
    }

    public static string Initial(string name)
    {
        var trimmed = NameRules.Normalize(name);
        return trimmed.Length == 0 ? "その他" : trimmed[0].ToString();
    }

}

public sealed record StoreAppEntry(string Name, string Aumid);

public static class RegisteredApplicationResolver
{
    private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    public static string? FindExecutable(params string[] executableNames)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var name in executableNames)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey($"{AppPathsKey}\\{name}");
                if (key?.GetValue(null) is not string value) continue;
                var path = NormalizeExecutablePath(value, name);
                if (path is not null) return path;
            }
            catch { }
        }
        return null;
    }

    private static string? NormalizeExecutablePath(string value, string executableName)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        var index = expanded.IndexOf(executableName, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var path = expanded[..(index + executableName.Length)].Trim().Trim('"');
        return Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
    }
}

public sealed class PresetService
{
    private readonly StoreAppsService _storeApps;
    public PresetService(StoreAppsService storeApps) => _storeApps = storeApps;

    public ObservableCollection<LauncherNode> Load(PresetSettings settings)
    {
        var result = new ObservableCollection<LauncherNode>();
        foreach (var item in Catalog.Where(item => !settings.HiddenItemIds.Contains(item.Id) && IsAvailable(item)).OrderBy(item => item.Order))
        {
            var destination = EnsureGroupPath(result, item.Group);
            destination.Add(new PresetItem { Id = RuntimeNodeIds.Create("preset:" + item.Id), PresetId = item.Id, Name = item.Name, IconSource = IconSource(item), RequiresConfirmation = item.RequiresConfirmation, Order = destination.Count });
        }
        return result;
    }

    public IReadOnlyList<PresetDefinition> AvailableDefinitions() => Catalog.Where(IsAvailable).ToList();

    public async Task<(bool Success, string Error)> LaunchAsync(PresetItem item)
    {
        var definition = Catalog.FirstOrDefault(value => value.Id == item.PresetId);
        if (definition is null) return (false, "主要操作プリセットが見つかりません。");
        try
        {
            if (MediaKeyService.TrySend(definition.Id)) return (true, string.Empty);
            var aumid = FindGpuAumid(definition.Id);
            if (aumid is not null)
            {
                return await StoreAppsService.LaunchAsync(aumid);
            }
            if (definition.Id == "amd-software")
            {
                var path = FindAmdSoftwarePath();
                if (path is null) return (false, "AMD Software が見つかりません。");
                await Task.Run(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
                return (true, string.Empty);
            }
            await Task.Run(() => Process.Start(new ProcessStartInfo(definition.FileName, definition.Arguments)
            { UseShellExecute = true, Verb = definition.RunAsAdmin ? "runas" : string.Empty }));
            return (true, string.Empty);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private bool IsAvailable(PresetDefinition item) => item.Id switch
    {
        "nvidia-control-panel" or "intel-graphics-command-center" or "intel-arc-control" => FindGpuAumid(item.Id) is not null,
        "amd-software" => FindAmdSoftwarePath() is not null,
        "local-group-policy" => File.Exists(Path.Combine(Environment.SystemDirectory, "gpedit.msc")),
        _ => true
    };

    private string? IconSource(PresetDefinition item)
    {
        var aumid = FindGpuAumid(item.Id);
        if (aumid is not null)
        {
            return $"shell:AppsFolder\\{aumid}";
        }
        if (item.Id == "amd-software") return FindAmdSoftwarePath();
        if (item.Id is "settings" or "search" or "installed-apps" or "default-apps" or "system" or "windows-update" or "mouse-settings" or "display-settings" or "bluetooth-settings" or "printers-settings")
            return "shell:AppsFolder\\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
        if (item.Id == "microsoft-store") return "shell:AppsFolder\\Microsoft.WindowsStore_8wekyb3d8bbwe!App";
        if (item.Id == "windows-security") return "shell:AppsFolder\\Microsoft.SecHealthUI_8wekyb3d8bbwe!SecHealthUI";
        if (item.FileName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return item.FileName;
        if (item.FileName.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase) || item.FileName.EndsWith(".msc", StringComparison.OrdinalIgnoreCase)) return Path.Combine(Environment.SystemDirectory, item.FileName);
        if (item.FileName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), item.FileName);
        var systemPath = Path.Combine(Environment.SystemDirectory, item.FileName);
        return File.Exists(systemPath) ? systemPath : item.FileName;
    }

    private string? FindGpuAumid(string id) => id switch
    {
        "nvidia-control-panel" => _storeApps.FindNvidiaControlPanelAumid(),
        "intel-graphics-command-center" => _storeApps.FindAumid("Intel Graphics Command Center", "Intel® Graphics Command Center", "インテル® グラフィックス・コマンド・センター"),
        "intel-arc-control" => _storeApps.FindAumid("Intel Arc Control", "インテル Arc Control"),
        _ => null
    };
    private static string? FindAmdSoftwarePath() => RegisteredApplicationResolver.FindExecutable("RadeonSoftware.exe", "AMDSoftware.exe");
    private static ObservableCollection<LauncherNode> EnsureGroupPath(ObservableCollection<LauncherNode> root, string path)
    {
        var current = root; var key = "";
        foreach (var name in path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            key = key.Length == 0 ? name : key + "/" + name;
            var group = current.OfType<GroupNode>().FirstOrDefault(node => node.Name.Equals(name, StringComparison.Ordinal));
            if (group is null) { group = new GroupNode { Id = RuntimeNodeIds.Create("preset-group:" + key), Name = name, Order = current.Count }; current.Add(group); }
            current = group.Children;
        }
        return current;
    }

    private static readonly IReadOnlyList<PresetDefinition> Catalog =
    [
        P("settings", "基本", 10, "設定", "ms-settings:"), P("search", "基本", 20, "検索", "ms-settings:search"), P("run", "基本", 30, "ファイル名を指定して実行", "explorer.exe", "shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}"), P("explorer", "基本", 40, "エクスプローラー", "explorer.exe"), P("desktop", "基本", 50, "デスクトップ", "explorer.exe", "shell:Desktop"),
        P("documents", "ファイルと個人用", 110, "ドキュメント", "explorer.exe", "shell:Personal"), P("pictures", "ファイルと個人用", 120, "ピクチャ", "explorer.exe", "shell:My Pictures"), P("music", "ファイルと個人用", 130, "ミュージック", "explorer.exe", "shell:My Music"), P("recent", "ファイルと個人用", 140, "最近使った項目", "explorer.exe", "shell:Recent"), P("this-pc", "ファイルと個人用", 150, "PC", "explorer.exe", "shell:MyComputerFolder"), P("explorer-options", "ファイルと個人用", 160, "エクスプローラーのオプション", "control.exe", "folders"), P("recycle-bin", "ファイルと個人用", 170, "ゴミ箱を開く", "explorer.exe", "shell:RecycleBinFolder"),
        P("installed-apps", "アプリ", 210, "インストールされているアプリ", "ms-settings:appsfeatures"), P("default-apps", "アプリ", 220, "既定のアプリ", "ms-settings:defaultapps"), P("programs-features", "アプリ", 230, "プログラムと機能", "appwiz.cpl"), P("windows-features", "アプリ", 240, "Windows の機能", "OptionalFeatures.exe"), P("microsoft-store", "アプリ", 250, "Microsoft Store", "ms-windows-store:"),
        P("nvidia-control-panel", "GPU 管理", 270, "NVIDIA コントロール パネル", ""), P("amd-software", "GPU 管理", 280, "AMD Software: Adrenalin Edition", ""), P("intel-graphics-command-center", "GPU 管理", 290, "Intel Graphics Command Center", ""), P("intel-arc-control", "GPU 管理", 300, "Intel Arc Control", ""),
        P("system", "システムとデバイス", 310, "システム", "ms-settings:about"), P("system-properties", "システムとデバイス", 320, "システムのプロパティ", "sysdm.cpl"), P("power-options", "システムとデバイス", 330, "電源オプション", "powercfg.cpl"), P("mobility-center", "システムとデバイス", 340, "モビリティ センター", "mblctr.exe"), P("device-manager", "システムとデバイス", 350, "デバイス マネージャー", "devmgmt.msc"), P("disk-management", "システムとデバイス", 360, "ディスクの管理", "diskmgmt.msc"), P("computer-management", "システムとデバイス", 370, "コンピューターの管理", "compmgmt.msc"), P("mouse-settings", "システムとデバイス", 380, "マウスの設定", "ms-settings:mousetouchpad"), P("display-settings", "システムとデバイス", 390, "ディスプレイの設定", "ms-settings:display"), P("bluetooth-settings", "システムとデバイス", 400, "Bluetooth とデバイス", "ms-settings:bluetooth"), P("printers-settings", "システムとデバイス", 410, "プリンターとスキャナー", "ms-settings:printers"),
        P("network-connections", "ネットワーク", 410, "ネットワーク接続", "ncpa.cpl"), P("network-sharing-center", "ネットワーク", 420, "ネットワークと共有センター", "control.exe", "/name Microsoft.NetworkAndSharingCenter"), P("internet-options", "ネットワーク", 430, "インターネットのプロパティ", "inetcpl.cpl"), P("remote-desktop", "ネットワーク", 440, "リモート デスクトップ", "mstsc.exe"),
        P("event-viewer", "管理と診断", 510, "イベント ビューアー", "eventvwr.msc"), P("task-manager", "管理と診断", 520, "タスク マネージャー", "taskmgr.exe"), P("terminal", "管理と診断", 530, "ターミナル", "wt.exe"), P("terminal-admin", "管理と診断", 540, "ターミナル（管理者）", "wt.exe", "", true), P("system-config", "管理と診断", 550, "システム構成", "msconfig.exe"), P("services", "管理と診断", 560, "サービス", "services.msc"), P("task-scheduler", "管理と診断", 570, "タスク スケジューラ", "taskschd.msc"), P("resource-monitor", "管理と診断", 580, "リソース モニター", "resmon.exe"), P("performance-monitor", "管理と診断", 590, "パフォーマンス モニター", "perfmon.msc"), P("cert-current-user", "管理と診断", 600, "証明書（現在のユーザー）", "certmgr.msc"), P("cert-local-machine", "管理と診断", 610, "証明書（ローカル コンピューター）", "certlm.msc"), P("local-group-policy", "管理と診断", 620, "ローカル グループ ポリシー エディター", "gpedit.msc"), P("registry-editor", "管理と診断", 630, "レジストリ エディター", "regedit.exe"),
        P("windows-security", "セキュリティと更新", 710, "Windows セキュリティ", "windowsdefender:"), P("credential-manager", "セキュリティと更新", 720, "資格情報マネージャー", "control.exe", "/name Microsoft.CredentialManager"), P("windows-update", "セキュリティと更新", 730, "Windows Update", "ms-settings:windowsupdate"), P("firewall-advanced", "セキュリティと更新", 740, "Windows Defender ファイアウォール（詳細設定）", "wf.msc"),
        P("lock", "電源とセッション", 810, "ロック", "rundll32.exe", "user32.dll,LockWorkStation", false, true), P("sign-out", "電源とセッション", 820, "サインアウト", "shutdown.exe", "/l", false, true), P("sleep", "電源とセッション", 830, "スリープ", "rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0", false, true), P("shutdown", "電源とセッション", 840, "シャットダウン", "shutdown.exe", "/s /t 0", false, true), P("restart", "電源とセッション", 850, "再起動", "shutdown.exe", "/r /t 0", false, true),
        P("media-previous", "メディア コントロール", 860, "前の曲", ""), P("media-play-pause", "メディア コントロール", 870, "再生／一時停止", ""), P("media-next", "メディア コントロール", 880, "次の曲", ""), P("media-stop", "メディア コントロール", 890, "停止", ""), P("media-volume-down", "メディア コントロール/音量", 910, "音量を下げる", ""), P("media-volume-up", "メディア コントロール/音量", 920, "音量を上げる", ""), P("media-volume-mute", "メディア コントロール/音量", 930, "ミュート切替", ""),
    ];

    private static PresetDefinition P(string id, string group, int order, string name, string file, string arguments = "", bool runAsAdmin = false, bool requiresConfirmation = false) => new(id, group, order, name, file, arguments, runAsAdmin, requiresConfirmation);
}

public sealed record PresetDefinition(string Id, string Group, int Order, string Name, string FileName, string Arguments, bool RunAsAdmin, bool RequiresConfirmation);

/// <summary>全ユーザー Start Menu に限定して昇格実行する同一EXE内ヘルパーです。</summary>
public static class ElevatedWindowsMenuHelper
{
    private const string Switch = "--opengepa-windows-menu-helper";
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 1;
        if (args.Length != 3 || !string.Equals(args[0], Switch, StringComparison.Ordinal)) return false;
        try
        {
            var operation = args[1];
            var payload = JsonSerializer.Deserialize<MenuHelperPayload>(Encoding.UTF8.GetString(Convert.FromBase64String(args[2]))) ?? throw new InvalidDataException("昇格ヘルパーの要求が不正です。");
            var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            var path = Resolve(root, payload.RelativePath);
            switch (operation)
            {
                case "create":
                    if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(payload.Target) || !Path.IsPathFullyQualified(payload.Target) || !File.Exists(payload.Target) || File.Exists(path)) throw new InvalidDataException("ショートカット作成要求が不正です。");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!); WindowsMenuService.CreateLink(path, payload.Target); break;
                case "delete":
                    if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new InvalidDataException("ショートカット削除要求が不正です。");
                    File.Delete(path); break;
                case "rename":
                    var renamed = Resolve(root, payload.NewRelativePath ?? string.Empty);
                    if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !renamed.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || File.Exists(renamed)) throw new InvalidDataException("ショートカット名前変更要求が不正です。");
                    File.Move(path, renamed); break;
                default: throw new InvalidDataException("未対応の昇格ヘルパー操作です。");
            }
            exitCode = 0;
        }
        catch { exitCode = 1; }
        return true;
    }

    public static void Execute(string operation, string relativePath, string? target, string? newRelativePath)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("OpenGepaの実行ファイルを取得できません。");
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new MenuHelperPayload(relativePath, target, newRelativePath))));
        using var process = Process.Start(new ProcessStartInfo(executable, $"{Switch} {operation} {payload}") { UseShellExecute = true, Verb = "runas" });
        if (process is null) throw new InvalidOperationException("昇格ヘルパーを開始できませんでした。");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("全ユーザー Start Menu の操作が取り消されたか、失敗しました。");
    }

    private static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("相対パスが不正です。");
        var full = Path.GetFullPath(Path.Combine(root, relative)); var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "" or "." or "..")) throw new InvalidDataException("相対パスが不正です。");
        return full;
    }

    private sealed record MenuHelperPayload(string RelativePath, string? Target, string? NewRelativePath);
}
