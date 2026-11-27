using System.Diagnostics;
using OpenGepa.Update;

var root = Path.GetDirectoryName(Environment.ProcessPath!)!;
try
{
    var layout = new UpdateLayout(root); layout.EnsureDirectories();
    var state = UpdateLayout.Read<InstallationState>(layout.StatePath) ?? throw new IOException("OpenGepaを一度起動して初期化してください。");
    var executable = layout.McpPath(state.Current); UpdateLayout.RequireFile(executable);
    var start = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false }; start.ArgumentList.Add("--portable"); start.ArgumentList.Add(root);
    using var process = Process.Start(start) ?? throw new IOException("OpenGepa.Mcpを起動できません。"); await process.WaitForExitAsync(); Environment.ExitCode = process.ExitCode;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
{
    await Console.Error.WriteLineAsync($"OpenGepa.Mcp: {ex.Message}"); Environment.ExitCode = 1;
}
