using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using OpenGepa.Models;
using OpenGepa.Services;

namespace OpenGepa;

public partial class EditorWindow : Window
{
    private readonly AppService _app; private bool _refreshing;
    private System.Windows.Point _dragStart;
    private const string NodeDragFormat = "OpenGepa.LauncherNodeId";
    public EditorWindow(AppService app) { InitializeComponent(); _app = app; _app.DataChanged += (_, _) => Dispatcher.Invoke(() => RefreshData((TabsCombo.SelectedItem as LauncherTab)?.Id)); }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); }
    private void Window_StateChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; Hide(); } }
    public void RefreshData(string? tabId = null)
    {
        _refreshing = true; TabsCombo.ItemsSource = _app.Data.Tabs.OrderBy(t => t.Order).ToList(); TabsCombo.SelectedItem = _app.Data.Tabs.FirstOrDefault(t => t.Id == tabId) ?? _app.Data.Tabs.FirstOrDefault(t => t.Id == _app.Data.SelectedTabId) ?? _app.Data.Tabs.FirstOrDefault(); EditorTree.ItemsSource = (TabsCombo.SelectedItem as LauncherTab)?.Children; _refreshing = false;
    }
    private LauncherTab? Tab => TabsCombo.SelectedItem as LauncherTab;
    private void TabsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_refreshing) EditorTree.ItemsSource = Tab?.Children; }
    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        var d = new TextPromptDialog("新しいランチャー", "名前") { Owner = this }; if (d.ShowDialog() != true) return;
        Commit(data => data.Tabs.Add(new LauncherTab { Name = d.Value, Order = data.Tabs.Count }), null);
    }
    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null) return; var id = Tab.Id; var d = new TextPromptDialog("名前変更", "名前", Tab.Name) { Owner = this }; if (d.ShowDialog() == true) Commit(data => data.Tabs.First(t => t.Id == id).Name = d.Value, id);
    }
    private void AddGroup_Click(object sender, RoutedEventArgs e) => AddNode(new GroupNode(), false);
    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var open = new OpenFileDialog { Title = "登録するファイル", CheckFileExists = true }; if (open.ShowDialog(this) != true) return;
        AddNode(new FileItem { Name = Path.GetFileNameWithoutExtension(open.FileName), Target = open.FileName }, true);
    }
    private void AddDirectory_Click(object sender, RoutedEventArgs e)
    {
        using var folder = new System.Windows.Forms.FolderBrowserDialog { Description = "登録するディレクトリ" }; if (folder.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        AddNode(new DirectoryItem { Name = Path.GetFileName(folder.SelectedPath.TrimEnd(Path.DirectorySeparatorChar)), Target = folder.SelectedPath }, true);
    }
    private void AddUrl_Click(object sender, RoutedEventArgs e) => AddNode(new UrlItem(), true);
    private void AddNode(LauncherNode node, bool target)
    {
        if (Tab is null) return; var d = new ItemDialog("項目を追加", node.Name, node is LauncherItem i ? i.Target : "", target) { Owner = this }; if (d.ShowDialog() != true) return;
        node.Name = d.ItemName; if (node is LauncherItem item) item.Target = d.Target;
        var tabId = Tab.Id; var parentId = (EditorTree.SelectedItem as GroupNode)?.Id;
        Commit(data => { var tab = data.Tabs.First(t => t.Id == tabId); var collection = parentId is null ? tab.Children : FindGroup(tab.Children, parentId)!.Children; node.Order = collection.Count; collection.Add(node); }, tabId);
        if (node is FileItem file) TryAddIcon(file.Id, file.Target, file.Name, tabId);
    }
    private void TryAddIcon(string id, string target, string name, string tabId)
    {
        var icon = _app.IconService.TryExtract(target, name); if (icon is null) return;
        Commit(data => FindNode(data.Tabs.First(t => t.Id == tabId).Children, id)!.Icon = icon, tabId);
    }
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null || EditorTree.SelectedItem is not LauncherNode selected) return; var d = new ItemDialog("項目を編集", selected.Name, selected is LauncherItem i ? i.Target : "", selected is LauncherItem) { Owner = this }; if (d.ShowDialog() != true) return;
        var tabId = Tab.Id; Commit(data => { var node = FindNode(data.Tabs.First(t => t.Id == tabId).Children, selected.Id)!; node.Name = d.ItemName; if (node is LauncherItem item) item.Target = d.Target; }, tabId);
    }
    private void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null || EditorTree.SelectedItem is not LauncherNode node) return;
        var dialog = new OpenFileDialog { Title = "アイコンに使う画像", Filter = "画像|*.png;*.jpg;*.jpeg;*.bmp;*.ico" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var icon = _app.IconService.ImportImage(dialog.FileName, node.Name); var tabId = Tab.Id;
            Commit(data => FindNode(data.Tabs.First(t => t.Id == tabId).Children, node.Id)!.Icon = icon, tabId);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void RetryIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null || EditorTree.SelectedItem is not FileItem file) return;
        var icon = _app.IconService.TryExtract(file.Target, file.Name);
        if (icon is null) { MessageBox.Show("対象ファイルからアイコンを取得できませんでした。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var tabId = Tab.Id; Commit(data => FindNode(data.Tabs.First(t => t.Id == tabId).Children, file.Id)!.Icon = icon, tabId);
    }
    private void ResetIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null || EditorTree.SelectedItem is not LauncherNode node) return; var tabId = Tab.Id;
        Commit(data => FindNode(data.Tabs.First(t => t.Id == tabId).Children, node.Id)!.Icon = null, tabId);
    }
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();
    private void EditorTree_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Delete && e.OriginalSource is not System.Windows.Controls.TextBox) { DeleteSelected(); e.Handled = true; } }
    private void DeleteSelected()
    {
        if (Tab is null || EditorTree.SelectedItem is not LauncherNode node) return; var descendants = node is GroupNode g ? Walk(g.Children).Count() : 0;
        var suffix = descendants > 0 ? $"と、その中にある{descendants}件の項目" : "";
        if (MessageBox.Show($"「{node.Name}」{suffix}を削除しますか？\nこの操作は元に戻せません。", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var tabId = Tab.Id; Commit(data => RemoveNode(data.Tabs.First(t => t.Id == tabId).Children, node.Id), tabId);
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null) return; using var folder = new System.Windows.Forms.FolderBrowserDialog { Description = "走査するディレクトリ" }; if (folder.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var root = folder.SelectedPath; if (IsDangerousRoot(root) && MessageBox.Show("非常に多くのファイルが含まれる可能性があります。\n将来のWindows Menuで利用できる予定です。\n\n走査を続行しますか？", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        IsEnabled = false; var progress = new ScanProgressDialog { Owner = this }; progress.Show();
        try
        {
            var result = await Task.Run(() => Scan(root, progress.Token)); progress.Complete(); var tabId = Tab.Id; var parentId = (EditorTree.SelectedItem as GroupNode)?.Id;
            var preview = new ScanPreviewDialog(root, result.Files, result.Skipped, selection => ValidateScanned(tabId, parentId, root, selection)) { Owner = this }; if (preview.ShowDialog() != true) return;
            var selected = preview.Selected;
            var iconFailures = 0; foreach (var candidate in selected) { candidate.CachedIcon = _app.IconService.TryExtract(candidate.FullPath, Path.GetFileNameWithoutExtension(candidate.FullPath)); if (candidate.CachedIcon is null) iconFailures++; }
            Commit(data => { var target = parentId is null ? data.Tabs.First(t => t.Id == tabId).Children : FindGroup(data.Tabs.First(t => t.Id == tabId).Children, parentId)!.Children; AddScanned(target, root, selected); }, tabId);
            if (iconFailures > 0) MessageBox.Show($"{iconFailures}件のアイコンを取得できなかったため、標準アイコンを使用します。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { progress.Complete(); IsEnabled = true; }
    }
    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Handled || !e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return; var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop)!;
        foreach (var path in paths) { if (File.Exists(path)) AddNode(new FileItem { Name = Path.GetFileNameWithoutExtension(path), Target = path }, true); else if (Directory.Exists(path)) AddNode(new DirectoryItem { Name = Path.GetFileName(path), Target = path }, true); }
    }
    private void EditorTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(EditorTree);
    private void EditorTree_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || EditorTree.SelectedItem is not LauncherNode node) return;
        var current = e.GetPosition(EditorTree);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        System.Windows.DragDrop.DoDragDrop(EditorTree, new System.Windows.DataObject(NodeDragFormat, node.Id), System.Windows.DragDropEffects.Move);
    }
    private void EditorTree_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(NodeDragFormat)) { e.Effects = System.Windows.DragDropEffects.Move; e.Handled = true; }
        else if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) e.Effects = System.Windows.DragDropEffects.Copy;
    }
    private void EditorTree_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(NodeDragFormat))
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) { var externalTarget = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject); if (externalTarget is not null) externalTarget.IsSelected = true; }
            return;
        }
        e.Handled = true; if (Tab is null) return;
        var sourceId = e.Data.GetData(NodeDragFormat) as string; if (sourceId is null) return;
        var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        var targetNode = container?.DataContext as LauncherNode;
        var relativeY = container is null || container.ActualHeight <= 0 ? .5 : e.GetPosition(container).Y / container.ActualHeight;
        var enterGroup = targetNode is GroupNode && relativeY is >= .25 and <= .75;
        var parentId = enterGroup ? targetNode!.Id : FindParentId(Tab.Children, targetNode?.Id);
        var targetId = enterGroup ? null : targetNode?.Id; var after = relativeY > .5;
        var tabId = Tab.Id;
        Commit(data => MoveNode(data.Tabs.First(t => t.Id == tabId).Children, sourceId, parentId, targetId, after), tabId);
    }
    private void Commit(Action<OpenGepaData> action, string? tabId)
    { if (!_app.TryCommit(action, out var error)) MessageBox.Show(error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); else RefreshData(tabId); }
    private static GroupNode? FindGroup(IEnumerable<LauncherNode> nodes, string id) => FindNode(nodes, id) as GroupNode;
    private static LauncherNode? FindNode(IEnumerable<LauncherNode> nodes, string id) { foreach (var n in nodes) { if (n.Id == id) return n; if (n is GroupNode g) { var f = FindNode(g.Children, id); if (f is not null) return f; } } return null; }
    private static bool RemoveNode(ObservableCollection<LauncherNode> nodes, string id) { var n = nodes.FirstOrDefault(x => x.Id == id); if (n is not null) return nodes.Remove(n); return nodes.OfType<GroupNode>().Any(g => RemoveNode(g.Children, id)); }
    private static string? FindParentId(IEnumerable<LauncherNode> nodes, string? id)
    { if (id is null) return null; foreach (var group in nodes.OfType<GroupNode>()) { if (group.Children.Any(x => x.Id == id)) return group.Id; var found = FindParentId(group.Children, id); if (found is not null) return found; } return null; }
    private static void MoveNode(ObservableCollection<LauncherNode> root, string sourceId, string? parentId, string? targetId, bool after)
    {
        var source = FindNode(root, sourceId) ?? throw new InvalidDataException("移動元が見つかりません。");
        if (sourceId == parentId || source is GroupNode sourceGroup && parentId is not null && FindNode(sourceGroup.Children, parentId) is not null)
            throw new InvalidDataException("自分自身または子孫のGroupへは移動できません。");
        var oldCollection = FindContainingCollection(root, sourceId) ?? throw new InvalidDataException("移動元が見つかりません。");
        var newCollection = parentId is null ? root : (FindNode(root, parentId) as GroupNode)?.Children ?? throw new InvalidDataException("移動先Groupが見つかりません。");
        oldCollection.Remove(source);
        var index = targetId is null ? newCollection.Count : newCollection.ToList().FindIndex(x => x.Id == targetId);
        if (index < 0) index = newCollection.Count; else if (after) index++;
        if (index > newCollection.Count) index = newCollection.Count; newCollection.Insert(index, source);
        NormalizeOrders(root);
    }
    private static ObservableCollection<LauncherNode>? FindContainingCollection(ObservableCollection<LauncherNode> nodes, string id)
    { if (nodes.Any(x => x.Id == id)) return nodes; foreach (var group in nodes.OfType<GroupNode>()) { var found = FindContainingCollection(group.Children, id); if (found is not null) return found; } return null; }
    private static void NormalizeOrders(ObservableCollection<LauncherNode> nodes)
    { for (var i = 0; i < nodes.Count; i++) { nodes[i].Order = i; if (nodes[i] is GroupNode group) NormalizeOrders(group.Children); } }
    private static T? FindAncestor<T>(DependencyObject? value) where T : DependencyObject
    { while (value is not null && value is not T) value = VisualTreeHelper.GetParent(value); return value as T; }
    private static IEnumerable<LauncherNode> Walk(IEnumerable<LauncherNode> nodes) { foreach (var n in nodes) { yield return n; if (n is GroupNode g) foreach (var c in Walk(g.Children)) yield return c; } }
    private static (List<string> Files, int Skipped) Scan(string root, CancellationToken cancellationToken)
    {
        var files = new List<string>(); var stack = new Stack<(string Path, int Depth)>(); stack.Push((root, 0)); var visited = 0; var skipped = 0;
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, depth) = stack.Pop(); if (++visited > 100000 || files.Count > 10000 || depth > 256) throw new InvalidOperationException("安全上限に達しました。より小さいディレクトリを選択してください。");
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(path)) { cancellationToken.ThrowIfCancellationRequested(); var a = File.GetAttributes(dir); if ((a & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0) stack.Push((dir, depth + 1)); }
                foreach (var file in Directory.EnumerateFiles(path)) { cancellationToken.ThrowIfCancellationRequested(); if (++visited > 100000 || files.Count >= 10000) throw new InvalidOperationException("安全上限に達しました。より小さいディレクトリを選択してください。"); var ext = Path.GetExtension(file); if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase)) files.Add(file); }
            }
            catch (UnauthorizedAccessException) { skipped++; }
            catch (IOException) { skipped++; }
        }
        return (files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), skipped);
    }
    private static bool IsDangerousRoot(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd(Path.DirectorySeparatorChar); var pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd(Path.DirectorySeparatorChar);
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.Equals(pf, StringComparison.OrdinalIgnoreCase) || full.Equals(pfx, StringComparison.OrdinalIgnoreCase);
    }
    private static void AddScanned(ObservableCollection<LauncherNode> target, string root, IReadOnlyList<ScanCandidate> selected)
    {
        foreach (var c in selected)
        {
            var destination = c.DestinationPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(destination)) throw new InvalidDataException("登録先は相対パスで指定してください。");
            var segments = destination.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(x => x is "." or "..")) throw new InvalidDataException("登録先相対パスが不正です。");
            var parts = segments.SkipLast(1);
            var current = target;
            foreach (var part in parts) { var group = current.OfType<GroupNode>().FirstOrDefault(x => x.Name.Equals(part, StringComparison.OrdinalIgnoreCase)); if (group is null) { group = new GroupNode { Name = part, Order = current.Count }; current.Add(group); } current = group.Children; }
            current.Add(new FileItem { Name = Path.GetFileNameWithoutExtension(segments[^1]), Target = c.FullPath, Icon = c.CachedIcon, Order = current.Count });
        }
    }
    private string? ValidateScanned(string tabId, string? parentId, string root, IReadOnlyList<ScanCandidate> selected)
    {
        try
        {
            var candidate = _app.Store.Clone(_app.Data); var tab = candidate.Tabs.First(t => t.Id == tabId);
            var target = parentId is null ? tab.Children : FindGroup(tab.Children, parentId)!.Children; AddScanned(target, root, selected);
            _ = _app.Store.Deserialize(_app.Store.Serialize(candidate)); return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
