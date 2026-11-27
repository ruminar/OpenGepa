using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenGepa.Update;

var root = Path.GetDirectoryName(Environment.ProcessPath!)!;
var layout = new UpdateLayout(root);
try
{
    layout.EnsureDirectories();
    var state = UpdateLayout.Read<InstallationState>(layout.StatePath);
    if (state is null)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
        RequireApplication(layout, version); MigrateLegacyData(layout);
        state = new InstallationState(version); layout.WriteState(state);
    }
    while (true)
    {
        RequireApplication(layout, state.Current);
        using var process = Start(layout.ApplicationPath(state.Current), root, "--portable", root);
        process.WaitForExit();
        if (process.ExitCode != UpdateLayout.ApplyExitCode) return process.ExitCode;
        state = ApplyPending(layout, state);
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
{
    MessageBox(IntPtr.Zero, ex.Message, "OpenGepaを起動できません", 0x10); return 1;
}

static InstallationState ApplyPending(UpdateLayout layout, InstallationState current)
{
    var pending = UpdateLayout.Read<PendingUpdate>(layout.PendingPath) ?? throw new IOException("更新の準備が見つかりません。");
    var profile = UpdateProfile.Load(Assembly.GetExecutingAssembly()) ?? throw new InvalidDataException("この配布物には更新用公開鍵がありません。");
    if (UpdatePackage.ParseVersion(pending.Version) is not { } next || next <= UpdatePackage.ParseVersion(current.Current)) throw new InvalidDataException("更新先の版が正しくありません。");
    var work = layout.WorkDirectory(pending.WorkDirectory); var manifest = File.ReadAllBytes(Path.Combine(work, UpdatePackage.ManifestName)); var signatures = File.ReadAllBytes(Path.Combine(work, UpdatePackage.SignatureName));
    var verified = UpdatePackage.ReadManifest(manifest, signatures, profile, pending.Version);
    var extracted = Path.Combine(work, "extracted"); Directory.CreateDirectory(extracted);
    using (var zip = File.OpenRead(Path.Combine(work, "package.zip"))) { UpdatePackage.VerifyZip(zip, verified); UpdatePackage.Extract(zip, extracted, pending.Version); }
    var staged = Path.Combine(extracted, "app", "versions", pending.Version); RequireDirectory(staged); Directory.Move(staged, layout.VersionDirectory(pending.Version));
    RequireApplication(layout, pending.Version); layout.WriteState(new InstallationState(pending.Version, current.Current)); File.Delete(layout.PendingPath); return new InstallationState(pending.Version, current.Current);
}

static void MigrateLegacyData(UpdateLayout layout)
{
    if (Directory.Exists(layout.DataDirectory)) return;
    var data = layout.DataDirectory; Directory.CreateDirectory(data);
    foreach (var name in new[] { "opengepa.json", "opengepa.backup.json", "opengepa.lastgood.json", "opengepa.usage.json" }) CopyIfExists(Path.Combine(layout.Root, name), Path.Combine(data, name));
    foreach (var name in new[] { "icon", "iconSet", "shortcut" }) CopyDirectoryIfExists(Path.Combine(layout.Root, name), Path.Combine(data, name));
    File.WriteAllText(Path.Combine(data, ".legacy-imported"), "");
}
static void CopyIfExists(string source, string destination) { if (File.Exists(source)) File.Copy(source, destination, false); }
static void CopyDirectoryIfExists(string source, string destination) { if (!Directory.Exists(source)) return; foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) { var target = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, false); } }
static void RequireApplication(UpdateLayout layout, string version) { UpdateLayout.RequireDirectory(layout.VersionDirectory(version)); UpdateLayout.RequireFile(layout.ApplicationPath(version)); UpdateLayout.RequireFile(layout.McpPath(version)); }
static void RequireDirectory(string path) => UpdateLayout.RequireDirectory(path);
static Process Start(string file, string working, params string[] args) { var info = new ProcessStartInfo(file) { WorkingDirectory = working, UseShellExecute = false }; foreach (var arg in args) info.ArgumentList.Add(arg); return Process.Start(info) ?? throw new IOException("OpenGepaを起動できません。"); }
[DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] static extern int MessageBox(IntPtr owner, string text, string title, uint flags);
