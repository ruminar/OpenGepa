using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using OpenGepa.Models;
using OpenGepa.Services;

namespace OpenGepa;

public sealed class EditorRootNode(ObservableCollection<LauncherNode> children)
{
    public ObservableCollection<LauncherNode> Children { get; } = children;
}

public sealed record NodeDragInfo(string SourceTabId, IReadOnlyList<string> NodeIds);

public partial class EditorWindow : Window
{
    private readonly AppService _app; private readonly string _tabId;
    private System.Windows.Point _dragStart;
    private readonly Dictionary<string, HashSet<string>> _expandedByTab = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _selectedByTab = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentTabId;
    private string? _selectionAnchorId;
    private string? _primarySelectedId;
    private EditorRootNode? _editorRoot;
    private AdornerLayer? _dropAdornerLayer;
    private TreeDropInsertionAdorner? _dropAdorner;
    private const string NodeDragFormat = "OpenGepa.LauncherNodeId";
    public EditorWindow(AppService app, string tabId)
    {
        InitializeComponent(); _app = app; _tabId = tabId;
        EditorTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler((_, _) => Dispatcher.BeginInvoke(RestoreTreeState)));
        EditorTree.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is TreeViewItem { DataContext: EditorRootNode } root) Dispatcher.BeginInvoke(() => root.IsExpanded = true); }));
        _app.DataChanged += (_, _) => Dispatcher.Invoke(RefreshData);
        Loaded += (_, _) => Dispatcher.BeginInvoke(EnsureRootExpanded, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { if (!App.IsExiting) { e.Cancel = true; Hide(); } }
    private void Window_StateChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; Hide(); } }
    public void RefreshData()
    {
        CaptureExpansionState();
        _currentTabId = _app.Data.Tabs.Any(t => t.Id == _tabId) ? _tabId : null; TabNameText.Text = Tab?.Name ?? "削除されたApp Launcher"; SetTreeItems(); RestoreTreeState();
    }
    private LauncherTab? Tab => _app.Data.Tabs.FirstOrDefault(t => t.Id == _tabId);
    private HashSet<string> SelectedIds => _currentTabId is null ? [] : _selectedByTab.TryGetValue(_currentTabId, out var selected) ? selected : _selectedByTab[_currentTabId] = new(StringComparer.OrdinalIgnoreCase);
    private void SetTreeItems() { _editorRoot = Tab is null ? null : new EditorRootNode(Tab.Children); EditorTree.ItemsSource = _editorRoot is null ? Array.Empty<EditorRootNode>() : new[] { _editorRoot }; }
    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null) return; var id = Tab.Id; var d = new TextPromptDialog("名前変更", "名前", Tab.Name) { Owner = this }; if (d.ShowDialog() == true) Commit(data => data.Tabs.First(t => t.Id == id).Name = d.Value, id);
    }
    private void AddGroup_Click(object sender, RoutedEventArgs e) => AddNode(new GroupNode(), false, true);
    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var open = new OpenFileDialog { Title = "登録するファイル", CheckFileExists = true, Filter = DirectoryCandidateRules.FileItemDialogFilter, FilterIndex = 1 }; if (open.ShowDialog(this) != true) return;
        AddNode(new FileItem { Name = DirectoryCandidateRules.DefaultDisplayName(open.FileName), Target = open.FileName }, true, true);
    }
    private void AddDirectory_Click(object sender, RoutedEventArgs e)
    {
        using var folder = new System.Windows.Forms.FolderBrowserDialog { Description = "登録するディレクトリ" }; if (folder.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        AddNode(new DirectoryItem { Target = folder.SelectedPath }, true, true);
    }
    private void AddUrl_Click(object sender, RoutedEventArgs e) => AddNode(new UrlItem(), true, true);
    private void AddNode(LauncherNode node, bool target, bool chooseDestination = false, string? initialDestinationId = null, bool initialDestinationSpecified = false)
    {
        if (Tab is null) return;
        var currentDestinationId = chooseDestination ? initialDestinationSpecified ? initialDestinationId : DefaultDestinationId() : GetPrimarySelectedNode() is GroupNode selectedGroup ? selectedGroup.Id : null;
        var directory = node is DirectoryItem;
        var d = new ItemDialog("項目を追加", DataValidator.NodeLabel(node), node switch { NamedLauncherItem i => i.Target, DirectoryItem i => i.Target, _ => "" }, target, chooseDestination ? DestinationOptions.Build(Tab) : null, currentDestinationId, !directory) { Owner = this }; if (d.ShowDialog() != true) return;
        if (node is GroupNode group) group.Name = d.ItemName; else if (node is NamedLauncherItem item) { item.Name = d.ItemName; item.Target = d.Target; } else if (node is DirectoryItem directoryItem) directoryItem.Target = d.Target;
        var tabId = Tab.Id; var parentId = chooseDestination ? d.DestinationId : currentDestinationId;
        Commit(data => { var tab = data.Tabs.First(t => t.Id == tabId); var collection = parentId is null ? tab.Children : FindGroup(tab.Children, parentId)!.Children; node.Order = collection.Count; collection.Add(node); }, tabId);
        if (node is FileItem file) TryAddIcon(file.Id, file.Target, file.Name, tabId);
        else if (node is UrlItem url) _ = TryAddUrlIcon(url.Id, url.Target, url.Name, tabId);
    }
    private void TryAddIcon(string id, string target, string name, string tabId)
    {
        var icon = _app.IconService.TryExtract(target, name); if (icon is null) return;
        Commit(data => FindNode(data.Tabs.First(t => t.Id == tabId).Children, id)!.Icon = icon, tabId);
    }
    private async Task TryAddUrlIcon(string id, string target, string name, string tabId)
    {
        var result = await _app.SiteIconService.TryFetchAsync(target, name);
        if (!result.IsSuccess) { MessageBox.Show("サイトのアイコンを取得できませんでした。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Commit(data => { var tab = data.Tabs.FirstOrDefault(t => t.Id == tabId); var found = tab is null ? null : FindNode(tab.Children, id); if (found is not null) found.Icon = result.IconPath; }, tabId);
    }
    private async void FetchPageTitle()
    {
        if (Tab is null || GetSingleSelectedNode() is not UrlItem url) return;
        var result = await _app.SiteIconService.TryFetchPageTitleAsync(url.Target);
        if (!result.IsSuccess) { MessageBox.Show("ページタイトルを取得できませんでした。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var tabId = Tab.Id; Commit(data => { var tab = data.Tabs.First(t => t.Id == tabId); var found = FindNode(tab.Children, url.Id) as UrlItem; if (found is not null) found.Name = UrlRegistrationRules.UniqueName(result.Value!, FindContainingCollection(tab.Children, found.Id)!, found.Id); }, tabId);
    }
    private void RenameSelectedNode()
    {
        if (Tab is null || GetSingleSelectedNode() is not LauncherNode selected) return;
        if (selected is DirectoryItem) return;
        var d = new TextPromptDialog("名前を変更", "表示名", DataValidator.NodeLabel(selected)) { Owner = this }; if (d.ShowDialog() != true) return;
        var tabId = Tab.Id; Commit(data => { var found = FindNode(data.Tabs.First(t => t.Id == tabId).Children, selected.Id); if (found is GroupNode group) group.Name = d.Value; else if (found is NamedLauncherItem item) item.Name = d.Value; }, tabId);
    }
    private void ChangeSelectedTarget()
    {
        if (Tab is null || GetSingleSelectedNode() is not LauncherNode selected || selected is GroupNode) return;
        var title = selected switch { FileItem => "起動対象を変更", DirectoryItem => "開く場所を変更", UrlItem => "URLを変更", _ => "対象を変更" };
        var target = selected switch { NamedLauncherItem item => item.Target, DirectoryItem item => item.Target, _ => "" };
        var d = new TextPromptDialog(title, "対象", target) { Owner = this }; if (d.ShowDialog() != true) return;
        var tabId = Tab.Id; Commit(data => { var found = FindNode(data.Tabs.First(t => t.Id == tabId).Children, selected.Id); if (found is NamedLauncherItem item) item.Target = d.Value; if (found is FileItem file) file.IsTargetMissing = false; else if (found is DirectoryItem directory) directory.Target = d.Value; }, tabId);
    }
    private void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null || GetSingleSelectedNode() is not LauncherNode node) return;
        var dialog = new OpenFileDialog { Title = "アイコンに使う画像", Filter = "画像|*.png;*.jpg;*.jpeg;*.bmp;*.ico" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var icon = _app.IconService.ImportImage(dialog.FileName, DataValidator.NodeLabel(node)); var tabId = Tab.Id;
            Commit(data => FindNode(data.Tabs.First(t => t.Id == tabId).Children, node.Id)!.Icon = icon, tabId);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void RetryIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null || GetSingleSelectedNode() is not FileItem file) return;
        var icon = _app.IconService.TryExtract(file.Target, file.Name);
        if (icon is null) { MessageBox.Show("対象ファイルからアイコンを取得できませんでした。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var tabId = Tab.Id; Commit(data => FindNode(data.Tabs.First(t => t.Id == tabId).Children, file.Id)!.Icon = icon, tabId);
    }
    private void ResetIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null || GetSingleSelectedNode() is not LauncherNode node) return; var tabId = Tab.Id;
        Commit(data => FindNode(data.Tabs.First(t => t.Id == tabId).Children, node.Id)!.Icon = null, tabId);
    }
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();
    private void EditorTree_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Delete && e.OriginalSource is not System.Windows.Controls.TextBox) { DeleteSelected(); e.Handled = true; } }
    private void DeleteSelected()
    {
        if (Tab is null) return; var nodes = GetSelectedNodes(); if (nodes.Count == 0) return;
        var selectedSet = nodes.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = nodes.Where(node => !HasSelectedAncestor(Tab.Children, node.Id, selectedSet)).ToList();
        var descendants = roots.OfType<GroupNode>().Sum(group => Walk(group.Children).Count());
        var detail = nodes.Count == 1 ? $"「{DataValidator.NodeLabel(nodes[0])}」" : $"選択した{nodes.Count}件";
        if (descendants > 0) detail += $"と、その中にある{descendants}件の項目";
        if (MessageBox.Show($"{detail}を削除しますか？\nこの操作は元に戻せません。", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var tabId = Tab.Id; SelectedIds.Clear(); _selectionAnchorId = null; _primarySelectedId = null;
        Commit(data => { var children = data.Tabs.First(t => t.Id == tabId).Children; foreach (var root in roots) RemoveNode(children, root.Id); NormalizeOrders(children); }, tabId);
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null) return; using var folder = new System.Windows.Forms.FolderBrowserDialog { Description = "走査するディレクトリ" }; if (folder.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var root = folder.SelectedPath; if (IsDangerousRoot(root) && MessageBox.Show("非常に多くのファイルが含まれる可能性があります。\n将来のWindows Menuで利用できる予定です。\n\n走査を続行しますか？", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        IsEnabled = false; var scanProgress = new OperationProgressDialog("ディレクトリ走査中", "候補を走査しています…", true) { Owner = this }; scanProgress.Show();
        try
        {
            var result = await Task.Run(() => Scan(root, scanProgress.Token)); scanProgress.Complete(); var tabId = Tab.Id; var parentId = DefaultDestinationId();
            var preview = new ScanPreviewDialog(root, result.Files, result.Skipped, selection => ValidateScanned(tabId, parentId, root, selection)) { Owner = this }; if (preview.ShowDialog() != true) return;
            var selected = preview.Selected;
            var registrationProgress = new OperationProgressDialog("ディレクトリ登録中", "登録準備中…", false) { Owner = this }; registrationProgress.Show();
            try
            {
                var iconFailures = await Task.Run(() => CacheCandidateIcons(selected, registrationProgress));
                registrationProgress.Token.ThrowIfCancellationRequested(); registrationProgress.Report("設定を検証し、JSONへ保存しています…", selected.Count, selected.Count + 1); await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                if (!Commit(data => { var target = parentId is null ? data.Tabs.First(t => t.Id == tabId).Children : FindGroup(data.Tabs.First(t => t.Id == tabId).Children, parentId)!.Children; AddScanned(target, root, selected); }, tabId)) return;
                registrationProgress.Report("登録が完了しました。", selected.Count + 1, selected.Count + 1);
                if (iconFailures > 0) MessageBox.Show($"{iconFailures}件のアイコンを取得できなかったため、標準アイコンを使用します。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally { registrationProgress.Complete(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { scanProgress.Complete(); IsEnabled = true; }
    }
    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Handled) return;
        var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject); var destinationId = container?.DataContext switch { GroupNode group => group.Id, LauncherNode node when Tab is not null => FindParentId(Tab.Children, node.Id), _ => null };
        if (TryGetDroppedUrl(e.Data, out var url)) { AddDroppedUrl(url, destinationId); e.Handled = true; return; }
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop)!;
        foreach (var path in paths) { if (File.Exists(path)) AddNode(new FileItem { Name = DirectoryCandidateRules.DefaultDisplayName(path), Target = path }, true, true, destinationId, true); else if (Directory.Exists(path)) AddNode(new DirectoryItem { Target = path }, true, true, destinationId, true); }
    }
    private void EditorTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(EditorTree); var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not LauncherNode node || FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource as DependencyObject) is not null) return;
        var visible = EnumerateVisibleContainers(EditorTree).Where(x => x.DataContext is LauncherNode).Select(x => ((LauncherNode)x.DataContext).Id).ToList(); var modifiers = Keyboard.Modifiers;
        var update = TreeSelectionLogic.Apply(SelectedIds, visible, _selectionAnchorId, node.Id, (modifiers & ModifierKeys.Shift) != 0, (modifiers & ModifierKeys.Control) != 0);
        SelectedIds.Clear(); SelectedIds.UnionWith(update.Selected); _selectionAnchorId = update.AnchorId; _primarySelectedId = update.PrimaryId;
        ApplySelectionVisuals(); container.Focus(); e.Handled = true;
    }
    private void EditorTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is EditorRootNode || container is null)
        {
            OpenRootContextMenu(container as FrameworkElement ?? EditorTree); e.Handled = true; return;
        }
        if (container?.DataContext is not LauncherNode node) return;
        if (!SelectedIds.Contains(node.Id)) SelectOnly(node.Id, container); else { _primarySelectedId = node.Id; ApplySelectionVisuals(); container.Focus(); }
        var single = GetSelectedNodes().Count == 1;
        var menu = new ContextMenu();
        menu.Items.Add(ContextMenuItem("すべて折りたたむ", CollapseAll, true)); menu.Items.Add(new Separator());
        if (node is GroupNode) { AddCreationItems(menu); menu.Items.Add(new Separator()); }
        if (node is not DirectoryItem) menu.Items.Add(ContextMenuItem("名前を変更", RenameSelectedNode, single));
        if (node is FileItem) menu.Items.Add(ContextMenuItem("起動対象を変更", ChangeSelectedTarget, single));
        else if (node is DirectoryItem) menu.Items.Add(ContextMenuItem("開く場所を変更", ChangeSelectedTarget, single));
        else if (node is UrlItem) { menu.Items.Add(ContextMenuItem("URLを変更", ChangeSelectedTarget, single)); menu.Items.Add(ContextMenuItem("ページタイトルを名前に設定", FetchPageTitle, single)); }
        menu.Items.Add(new Separator());
        menu.Items.Add(ContextMenuItem("アイコンを変更", () => ChangeIcon_Click(this, new RoutedEventArgs()), single));
        if (node is FileItem) menu.Items.Add(ContextMenuItem("アイコンを再取得", () => RetryIcon_Click(this, new RoutedEventArgs()), single));
        if (node is UrlItem) menu.Items.Add(ContextMenuItem("サイトのアイコンを取得", () => { if (Tab is not null && GetSingleSelectedNode() is UrlItem url) _ = TryAddUrlIcon(url.Id, url.Target, url.Name, Tab.Id); }, single));
        menu.Items.Add(ContextMenuItem("アイコンを標準に戻す", () => ResetIcon_Click(this, new RoutedEventArgs()), single));
        menu.Items.Add(new Separator());
        menu.Items.Add(ContextMenuItem("削除", DeleteSelected, true));
        container.ContextMenu = menu; menu.IsOpen = true; e.Handled = true;
    }
    private void OpenRootContextMenu(FrameworkElement target)
    {
        SelectedIds.Clear(); _selectionAnchorId = null; _primarySelectedId = null; ApplySelectionVisuals(); target.Focus();
        var menu = new ContextMenu(); menu.Items.Add(ContextMenuItem("すべて折りたたむ", CollapseAll, true)); menu.Items.Add(new Separator()); AddCreationItems(menu);
        target.ContextMenu = menu; menu.IsOpen = true;
    }
    private void CollapseAll()
    {
        foreach (var container in EnumerateAllContainers(EditorTree)) if (container.DataContext is GroupNode) container.IsExpanded = false;
        if (_currentTabId is not null) _expandedByTab[_currentTabId] = [];
        EnsureRootExpanded();
    }
    private void AddCreationItems(ContextMenu menu)
    {
        menu.Items.Add(ContextMenuItem("グループを追加", () => AddGroup_Click(this, new RoutedEventArgs()), true));
        menu.Items.Add(ContextMenuItem("ファイルを追加", () => AddFile_Click(this, new RoutedEventArgs()), true));
        menu.Items.Add(ContextMenuItem("Directory参照追加（UNC可）", () => AddDirectory_Click(this, new RoutedEventArgs()), true));
        menu.Items.Add(ContextMenuItem("URLを追加", () => AddUrl_Click(this, new RoutedEventArgs()), true));
        menu.Items.Add(ContextMenuItem("フォルダを走査して一括登録", () => Scan_Click(this, new RoutedEventArgs()), true));
    }
    private static System.Windows.Controls.MenuItem ContextMenuItem(string header, Action action, bool enabled)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header, IsEnabled = enabled }; item.Click += (_, _) => action(); return item;
    }
    private void EditorTree_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || GetPrimarySelectedNode() is not LauncherNode node) return;
        var current = e.GetPosition(EditorTree);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var ids = GetSelectedNodes().Select(x => x.Id).ToList();
        if (ids.Count == 0 || Tab is null) return;
        System.Windows.DragDrop.DoDragDrop(EditorTree, new System.Windows.DataObject(NodeDragFormat, new NodeDragInfo(Tab.Id, ids)), System.Windows.DragDropEffects.Move);
    }
    private void EditorTree_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(NodeDragFormat))
        {
            var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject); var targetNode = container?.DataContext as LauncherNode;
            var relativeY = container is null || container.ActualHeight <= 0 ? .5 : e.GetPosition(container).Y / container.ActualHeight;
            var enterGroup = targetNode is GroupNode && relativeY is >= .25 and <= .75;
            if (container is null || enterGroup) ClearDropInsertion(); else ShowDropInsertion(container, relativeY > .5);
            e.Effects = System.Windows.DragDropEffects.Move; e.Handled = true;
        }
        else
        {
            ClearDropInsertion();
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) || TryGetDroppedUrl(e.Data, out _)) { e.Effects = System.Windows.DragDropEffects.Copy; e.Handled = true; }
        }
    }
    private void EditorTree_DragLeave(object sender, System.Windows.DragEventArgs e) => ClearDropInsertion();
    private void EditorTree_Drop(object sender, System.Windows.DragEventArgs e)
    {
        ClearDropInsertion();
        if (!e.Data.GetDataPresent(NodeDragFormat))
        {
            var externalTarget = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject); var destinationId = externalTarget?.DataContext switch { GroupNode group => group.Id, LauncherNode node when Tab is not null => FindParentId(Tab.Children, node.Id), _ => null };
            if (TryGetDroppedUrl(e.Data, out var url)) { AddDroppedUrl(url, destinationId); e.Handled = true; return; }
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) && externalTarget?.DataContext is LauncherNode externalNode) SelectOnly(externalNode.Id, externalTarget);
            return;
        }
        e.Handled = true; if (Tab is null) return;
        var drag = e.Data.GetData(NodeDragFormat) as NodeDragInfo; if (drag is null) return;
        var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        var targetNode = container?.DataContext as LauncherNode;
        var relativeY = container is null || container.ActualHeight <= 0 ? .5 : e.GetPosition(container).Y / container.ActualHeight;
        var enterGroup = targetNode is GroupNode && relativeY is >= .25 and <= .75;
        var parentId = enterGroup ? targetNode!.Id : FindParentId(Tab.Children, targetNode?.Id);
        var targetId = enterGroup ? null : targetNode?.Id; var after = relativeY > .5;
        var tabId = Tab.Id;
        Commit(data => MoveNodes(data, drag.SourceTabId, tabId, drag.NodeIds, parentId, targetId, after), tabId);
    }
    private void AddDroppedUrl(string target, string? destinationId)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        var siblings = destinationId is null ? Tab?.Children ?? [] : FindGroup(Tab?.Children ?? [], destinationId)?.Children ?? [];
        AddNode(new UrlItem { Name = UrlRegistrationRules.UniqueDroppedName(uri, siblings), Target = uri.AbsoluteUri }, true, true, destinationId, true);
    }
    private void ShowDropInsertion(TreeViewItem target, bool after)
    {
        if (_dropAdorner is not null && ReferenceEquals(_dropAdorner.AdornedElement, target) && _dropAdornerAfter == after) return;
        ClearDropInsertion(); _dropAdornerLayer = AdornerLayer.GetAdornerLayer(target); if (_dropAdornerLayer is null) return;
        _dropAdorner = new TreeDropInsertionAdorner(target, after); _dropAdornerAfter = after; _dropAdornerLayer.Add(_dropAdorner);
    }
    private bool _dropAdornerAfter;
    private void ClearDropInsertion()
    {
        if (_dropAdorner is not null && _dropAdornerLayer is not null) _dropAdornerLayer.Remove(_dropAdorner);
        _dropAdorner = null; _dropAdornerLayer = null;
    }
    private static bool TryGetDroppedUrl(System.Windows.IDataObject data, out string url)
    {
        foreach (var format in new[] { System.Windows.DataFormats.UnicodeText, System.Windows.DataFormats.Text, "text/uri-list", "UniformResourceLocator", "UniformResourceLocatorW" })
        {
            if (!data.GetDataPresent(format)) continue;
            var value = data.GetData(format); var text = value as string;
            if (text is null && value is Stream stream)
            {
                using var copy = new MemoryStream(); stream.CopyTo(copy); text = format == "UniformResourceLocatorW" ? Encoding.Unicode.GetString(copy.ToArray()) : Encoding.UTF8.GetString(copy.ToArray());
            }
            if (text is null) continue;
            var parsed = ExtractUrlFromDropText(text);
            if (parsed is not null) { url = parsed; return true; }
        }
        url = ""; return false;
    }
    public static string? ExtractUrlFromDropText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => !x.StartsWith('#')))
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return uri.AbsoluteUri;
        return null;
    }
    private bool Commit(Action<OpenGepaData> action, string? tabId)
    {
        if (_app.TryCommit(action, out var error)) return true;
        MessageBox.Show(error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); return false;
    }
    private static GroupNode? FindGroup(IEnumerable<LauncherNode> nodes, string id) => FindNode(nodes, id) as GroupNode;
    private static LauncherNode? FindNode(IEnumerable<LauncherNode> nodes, string id) { foreach (var n in nodes) { if (n.Id == id) return n; if (n is GroupNode g) { var f = FindNode(g.Children, id); if (f is not null) return f; } } return null; }
    private static bool RemoveNode(ObservableCollection<LauncherNode> nodes, string id) { var n = nodes.FirstOrDefault(x => x.Id == id); if (n is not null) return nodes.Remove(n); return nodes.OfType<GroupNode>().Any(g => RemoveNode(g.Children, id)); }
    private static bool HasSelectedAncestor(IEnumerable<LauncherNode> nodes, string targetId, HashSet<string> selected, bool ancestorSelected = false)
    {
        foreach (var node in nodes)
        {
            if (node.Id == targetId) return ancestorSelected;
            if (node is GroupNode group && HasSelectedAncestor(group.Children, targetId, selected, ancestorSelected || selected.Contains(node.Id))) return true;
        }
        return false;
    }
    private static string? FindParentId(IEnumerable<LauncherNode> nodes, string? id)
    { if (id is null) return null; foreach (var group in nodes.OfType<GroupNode>()) { if (group.Children.Any(x => x.Id == id)) return group.Id; var found = FindParentId(group.Children, id); if (found is not null) return found; } return null; }
    public static void MoveNodes(OpenGepaData data, string sourceTabId, string targetTabId, IReadOnlyList<string> sourceIds, string? parentId, string? targetId, bool after)
    {
        var sourceTab = data.Tabs.FirstOrDefault(t => t.Id == sourceTabId) ?? throw new InvalidDataException("移動元のApp Launcherが見つかりません。");
        var targetTab = data.Tabs.FirstOrDefault(t => t.Id == targetTabId) ?? throw new InvalidDataException("移動先のApp Launcherが見つかりません。");
        var selected = sourceIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => FindNode(sourceTab.Children, id) ?? throw new InvalidDataException("移動元が見つかりません。")).ToList();
        var selectedIds = selected.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = selected.Where(node => !HasSelectedAncestor(sourceTab.Children, node.Id, selectedIds)).OrderBy(node => NodeOrder(sourceTab.Children, node.Id)).ToList();
        if (sources.Count == 0) return;
        if (sourceTabId == targetTabId && sources.Any(source => source.Id == parentId || source is GroupNode group && parentId is not null && FindNode(group.Children, parentId) is not null))
            throw new InvalidDataException("自分自身または子孫のGroupへは移動できません。");
        var newCollection = parentId is null ? targetTab.Children : (FindNode(targetTab.Children, parentId) as GroupNode)?.Children ?? throw new InvalidDataException("移動先Groupが見つかりません。");
        foreach (var source in sources)
        {
            var oldCollection = FindContainingCollection(sourceTab.Children, source.Id) ?? throw new InvalidDataException("移動元が見つかりません。");
            oldCollection.Remove(source);
        }
        var index = targetId is null ? newCollection.Count : newCollection.ToList().FindIndex(x => x.Id == targetId);
        if (index < 0) index = newCollection.Count; else if (after) index++;
        if (index > newCollection.Count) index = newCollection.Count;
        foreach (var source in sources) newCollection.Insert(index++, source);
        NormalizeOrders(sourceTab.Children);
        if (sourceTabId != targetTabId) NormalizeOrders(targetTab.Children);
    }
    private static int NodeOrder(IEnumerable<LauncherNode> nodes, string id)
    {
        var order = 0;
        foreach (var node in Walk(nodes)) { if (node.Id == id) return order; order++; }
        return int.MaxValue;
    }
    private static ObservableCollection<LauncherNode>? FindContainingCollection(ObservableCollection<LauncherNode> nodes, string id)
    { if (nodes.Any(x => x.Id == id)) return nodes; foreach (var group in nodes.OfType<GroupNode>()) { var found = FindContainingCollection(group.Children, id); if (found is not null) return found; } return null; }
    private static void NormalizeOrders(ObservableCollection<LauncherNode> nodes)
    { for (var i = 0; i < nodes.Count; i++) { nodes[i].Order = i; if (nodes[i] is GroupNode group) NormalizeOrders(group.Children); } }
    private static T? FindAncestor<T>(DependencyObject? value) where T : DependencyObject
    { while (value is not null && value is not T) value = VisualTreeHelper.GetParent(value); return value as T; }
    private static IEnumerable<LauncherNode> Walk(IEnumerable<LauncherNode> nodes) { foreach (var n in nodes) { yield return n; if (n is GroupNode g) foreach (var c in Walk(g.Children)) yield return c; } }
    private IReadOnlyList<LauncherNode> GetSelectedNodes()
    { if (Tab is null) return []; var ids = SelectedIds; return Walk(Tab.Children).Where(x => ids.Contains(x.Id)).ToList(); }
    private LauncherNode? GetSingleSelectedNode() { var selected = GetSelectedNodes(); return selected.Count == 1 ? selected[0] : null; }
    private LauncherNode? GetPrimarySelectedNode() => Tab is null || _primarySelectedId is null ? null : FindNode(Tab.Children, _primarySelectedId);
    private string? DefaultDestinationId() => GetPrimarySelectedNode() switch { GroupNode group => group.Id, LauncherNode node when Tab is not null => FindParentId(Tab.Children, node.Id), _ => null };
    private void SelectOnly(string id, TreeViewItem container)
    { SelectedIds.Clear(); SelectedIds.Add(id); _selectionAnchorId = id; _primarySelectedId = id; ApplySelectionVisuals(); container.Focus(); }
    private void CaptureExpansionState()
    {
        if (_currentTabId is null || EditorTree.ItemsSource is null) return;
        _expandedByTab[_currentTabId] = EnumerateAllContainers(EditorTree).Where(x => x.IsExpanded && x.DataContext is GroupNode).Select(x => ((LauncherNode)x.DataContext).Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    private void RestoreTreeState()
    {
        if (_currentTabId is null) return; EditorTree.UpdateLayout(); EnsureRootExpanded(); Dispatcher.BeginInvoke(EnsureRootExpanded, System.Windows.Threading.DispatcherPriority.Loaded);
        var expanded = _expandedByTab.TryGetValue(_currentTabId, out var values) ? values : [];
        var validIds = Tab is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : Walk(Tab.Children).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SelectedIds.RemoveWhere(id => !validIds.Contains(id)); if (_primarySelectedId is not null && !validIds.Contains(_primarySelectedId)) _primarySelectedId = null; _primarySelectedId ??= SelectedIds.FirstOrDefault(); RestoreContainers(EditorTree, expanded); ApplySelectionVisuals();
    }
    private void EnsureRootExpanded()
    {
        if (EditorTree.Items.Count > 0 && EditorTree.ItemContainerGenerator.ContainerFromItem(EditorTree.Items[0]) is TreeViewItem rootContainer) { rootContainer.IsExpanded = true; rootContainer.UpdateLayout(); }
    }
    private void RestoreContainers(ItemsControl parent, HashSet<string> expanded)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
            if (container.DataContext is GroupNode group && expanded.Contains(group.Id)) container.IsExpanded = true;
            if (container.IsExpanded) { container.UpdateLayout(); RestoreContainers(container, expanded); }
        }
    }
    private void ApplySelectionVisuals()
    {
        var selected = SelectedIds;
        foreach (var container in EnumerateAllContainers(EditorTree)) if (container.DataContext is LauncherNode node) { TreeSelectionVisual.SetIsSelected(container, selected.Contains(node.Id)); container.IsSelected = node.Id == _primarySelectedId; }
    }
    private static IEnumerable<TreeViewItem> EnumerateAllContainers(ItemsControl parent)
    {
        foreach (var item in parent.Items) if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container) { yield return container; foreach (var child in EnumerateAllContainers(container)) yield return child; }
    }
    private static IEnumerable<TreeViewItem> EnumerateVisibleContainers(ItemsControl parent)
    {
        foreach (var item in parent.Items) if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container) { yield return container; if (container.IsExpanded) foreach (var child in EnumerateVisibleContainers(container)) yield return child; }
    }
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
        var currentRoot = DirectoryScanRootRules.GetOrCreateRootGroup(target, root);
        foreach (var c in selected)
        {
            var destination = c.DestinationPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(destination)) throw new InvalidDataException("登録先は相対パスで指定してください。");
            var segments = destination.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(x => x is "." or "..")) throw new InvalidDataException("登録先相対パスが不正です。");
            var parts = segments.SkipLast(1);
            var current = currentRoot;
            foreach (var part in parts) { var group = current.OfType<GroupNode>().FirstOrDefault(x => x.Name.Equals(part, StringComparison.OrdinalIgnoreCase)); if (group is null) { group = new GroupNode { Name = part, Order = current.Count }; current.Add(group); } current = group.Children; }
            current.Add(new FileItem { Name = DirectoryCandidateRules.DefaultDisplayName(segments[^1]), Target = c.FullPath, Icon = c.CachedIcon, Order = current.Count });
        }
    }
    private ScanValidationResult ValidateScanned(string tabId, string? parentId, string root, IReadOnlyList<ScanCandidate> selected)
    {
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var errors = new List<string>();
        var duplicateDestinations = selected.GroupBy(x => NormalizeDestinationKey(x.DestinationPath), StringComparer.OrdinalIgnoreCase).Where(x => x.Key is null || x.Count() > 1);
        foreach (var duplicate in duplicateDestinations) foreach (var candidate in duplicate) conflicts.Add(candidate.FullPath);
        if (conflicts.Count > 0) errors.Add("同じ登録先名が複数選択されています。");
        var working = _app.Store.Clone(_app.Data);
        foreach (var selectedCandidate in selected)
        {
            try
            {
                var trial = _app.Store.Clone(working); var tab = trial.Tabs.First(t => t.Id == tabId);
                var target = parentId is null ? tab.Children : FindGroup(tab.Children, parentId)!.Children; AddScanned(target, root, [selectedCandidate]);
                working = _app.Store.Deserialize(_app.Store.Serialize(trial));
            }
            catch (Exception ex) { conflicts.Add(selectedCandidate.FullPath); errors.Add($"{selectedCandidate.DestinationPath}: {ex.Message}"); }
        }
        return conflicts.Count == 0 ? ScanValidationResult.Success : new ScanValidationResult(string.Join("\n", errors.Distinct()), conflicts);
    }
    private static string? NormalizeDestinationKey(string destination)
    {
        try
        {
            var normalized = destination.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized)) return null;
            var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(x => x is "." or "..")) return null;
            return string.Join("/", segments.Select(NameRules.Normalize));
        }
        catch { return null; }
    }
    private int CacheCandidateIcons(IReadOnlyList<ScanCandidate> selected, OperationProgressDialog progress)
    {
        var failures = 0;
        for (var i = 0; i < selected.Count; i++)
        {
            progress.Token.ThrowIfCancellationRequested(); var candidate = selected[i];
            progress.Report($"アイコンを取得しています ({i + 1}/{selected.Count})\n{candidate.DestinationPath}", i, selected.Count + 1);
            candidate.CachedIcon = _app.IconService.TryExtract(candidate.FullPath, Path.GetFileName(candidate.FullPath)); if (candidate.CachedIcon is null) failures++; progress.Report($"アイコンを取得しています ({i + 1}/{selected.Count})\n{candidate.DestinationPath}", i + 1, selected.Count + 1);
        }
        return failures;
    }
}
