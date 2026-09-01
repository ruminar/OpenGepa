using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using OpenGepa.Models;
using OpenGepa.Services;

namespace OpenGepa;

public partial class MainWindow : Window
{
    private readonly AppService _app;
    private bool _refreshing, _launching, _dialogOpen, _settingSearch;
    private readonly Dictionary<string, HashSet<string>> _expandedByTab = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _searchByTab = new(StringComparer.OrdinalIgnoreCase);
    private string? _renderedTabId;
    public MainWindow(AppService app) { InitializeComponent(); _app = app; _app.DataChanged += (_, _) => Dispatcher.BeginInvoke(RefreshData); }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); }
    private void Window_StateChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; Hide(); } }

    public void RefreshData()
    {
        CaptureExpanded(_renderedTabId); _refreshing = true; var visible = _app.VisibleTabs; var selected = _app.SelectedTab; TabsList.ItemsSource = visible; TabsList.SelectedItem = selected; PinToggle.IsChecked = _app.Data.IsLauncherPinned; Topmost = !_app.Data.IsLauncherPinned; Title = selected is null ? "OpenGepa" : $"OpenGepa - {selected.Name}";
        _renderedTabId = selected?.Id; _settingSearch = true; SearchText.Text = selected is not null && _searchByTab.TryGetValue(selected.Id, out var search) ? search : ""; _settingSearch = false; ApplySearch(false); EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed; _refreshing = false;
    }
    public void PositionNearCursor()
    {
        var point = System.Windows.Forms.Cursor.Position; var screen = System.Windows.Forms.Screen.FromPoint(point); var area = screen.WorkingArea; var source = HwndSource.FromHwnd(new WindowInteropHelper(this).EnsureHandle()); var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var cursor = transform.Transform(new System.Windows.Point(point.X, point.Y)); var topLeft = transform.Transform(new System.Windows.Point(area.Left, area.Top)); var bottomRight = transform.Transform(new System.Windows.Point(area.Right, area.Bottom)); Left = Math.Max(topLeft.X, Math.Min(cursor.X - Width, bottomRight.X - Width)); Top = Math.Max(topLeft.Y, Math.Min(cursor.Y - Height, bottomRight.Y - Height));
    }
    private void Window_Deactivated(object sender, EventArgs e) { if (IsVisible && !_app.Data.IsLauncherPinned && !_dialogOpen) Hide(); }
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down)
        {
            var delta = e.Key == Key.Up ? -1 : 1;
            if (Keyboard.Modifiers == ModifierKeys.Shift) MoveTabSelection(delta); else MoveTreeSelection(delta);
            e.Handled = true; return;
        }
        if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            if (IsDescendantOf(Keyboard.FocusedElement as DependencyObject, TabsList)) OpenTabContextMenu(); else OpenTreeContextMenu();
            e.Handled = true; return;
        }
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchText.Text)) { SearchText.Clear(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
    }
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (SearchText.IsKeyboardFocused || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != ModifierKeys.None || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        SearchText.Focus(); SearchText.CaretIndex = SearchText.Text.Length; SearchText.SelectedText = e.Text; e.Handled = true;
    }
    private void PinToggle_Changed(object sender, RoutedEventArgs e) { if (!_refreshing) { Topmost = PinToggle.IsChecked != true; Commit(data => data.IsLauncherPinned = PinToggle.IsChecked == true); } }
    private void SearchText_Changed(object sender, TextChangedEventArgs e) { if (_settingSearch) return; if (_renderedTabId is not null) _searchByTab[_renderedTabId] = SearchText.Text; ApplySearch(true); }

    private void ApplySearch(bool captureState)
    {
        var tab = _app.SelectedTab; if (tab is null) { LauncherTree.ItemsSource = null; return; } var search = NameRules.Normalize(SearchText.Text);
        if (search.Length == 0) { LauncherTree.ItemsSource = tab.Children; var expanded = _expandedByTab.TryGetValue(tab.Id, out var saved) ? saved : new HashSet<string>(StringComparer.OrdinalIgnoreCase); Dispatcher.BeginInvoke(() => RestoreExpanded(expanded)); return; }
        if (captureState) CaptureExpanded(tab.Id); LauncherTree.ItemsSource = Filter(tab.Children, search); Dispatcher.BeginInvoke(ExpandAll);
    }
    private static ObservableCollection<LauncherNode> Filter(IEnumerable<LauncherNode> nodes, string text)
    {
        var result = new ObservableCollection<LauncherNode>();
        foreach (var node in nodes.OrderBy(x => x.Order))
        {
            if (node is GroupNode group)
            {
                if (Contains(group.Name, text)) result.Add(CloneGroup(group, new ObservableCollection<LauncherNode>(group.Children)));
                else { var children = Filter(group.Children, text); if (children.Count > 0) result.Add(CloneGroup(group, children)); }
            }
            else if (Contains(DataValidator.NodeLabel(node), text)) result.Add(node);
        }
        return result;
    }
    private static GroupNode CloneGroup(GroupNode source, ObservableCollection<LauncherNode> children) => new() { Id = source.Id, Name = source.Name, Icon = source.Icon, Order = source.Order, Children = children };
    private static bool Contains(string value, string text) => NameRules.Normalize(value).Contains(text, StringComparison.OrdinalIgnoreCase);

    private void TabsList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_refreshing && TabsList.SelectedItem is LauncherTab tab) _app.SelectTab(tab.Id); }
    private async void LauncherTree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var hit = LauncherTree.InputHitTest(e.GetPosition(LauncherTree)) as DependencyObject; var item = FindAncestor<TreeViewItem>(hit);
        if (item?.DataContext is GroupNode && e.ClickCount == 1) { if (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(hit) is null) item.IsExpanded = !item.IsExpanded; e.Handled = true; return; }
        if (item?.DataContext is LauncherNode launcher && (launcher is FileItem or DirectoryItem or UrlItem) && _app.Data.ItemLaunch.GetClickCount(launcher) == 1) { e.Handled = true; await Launch(launcher); }
    }
    private async void LauncherTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var hit = LauncherTree.InputHitTest(e.GetPosition(LauncherTree)) as DependencyObject; var item = FindAncestor<TreeViewItem>(hit);
        if (item?.DataContext is LauncherNode launcher && (launcher is FileItem or DirectoryItem or UrlItem) && _app.Data.ItemLaunch.GetClickCount(launcher) == 2) { e.Handled = true; await Launch(launcher); }
    }
    private async void LauncherTree_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (LauncherTree.SelectedItem is GroupNode && (e.Key == Key.Enter || e.Key == Key.Space)) { var item = FindContainer(LauncherTree, LauncherTree.SelectedItem); if (item is not null) item.IsExpanded = !item.IsExpanded; e.Handled = true; }
        else if (LauncherTree.SelectedItem is LauncherNode item && item is FileItem or DirectoryItem or UrlItem && e.Key == Key.Enter) { e.Handled = true; await Launch(item); }
    }
    private async Task Launch(LauncherNode item)
    {
        if (_launching) return; _launching = true; try { var result = await _app.LaunchService.LaunchAsync(item); if (result.Success) { if (!_app.Data.IsLauncherPinned) Hide(); } else ShowDialog(() => MessageBox.Show($"「{DataValidator.NodeLabel(item)}」を起動できませんでした。\n\n{result.Error}", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); } finally { _launching = false; }
    }

    private void LauncherTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hit = LauncherTree.InputHitTest(e.GetPosition(LauncherTree)) as DependencyObject; var node = FindAncestor<TreeViewItem>(hit)?.DataContext as LauncherNode;
        ShowTreeContextMenu(node, (FrameworkElement?)FindAncestor<TreeViewItem>(hit) ?? LauncherTree, false); e.Handled = true;
    }
    private void OpenTreeContextMenu() => ShowTreeContextMenu(LauncherTree.SelectedItem as LauncherNode, (FrameworkElement?)FindContainer(LauncherTree, LauncherTree.SelectedItem) ?? LauncherTree, true);
    private void ShowTreeContextMenu(LauncherNode? node, FrameworkElement target, bool keyboard)
    {
        var menu = new ContextMenu();
        if (node is null) { menu.Items.Add(Menu("すべて折りたたむ", CollapseAll)); menu.Items.Add(new Separator()); AddCreationItems(menu, null); }
        else { var actual = FindNode(_app.SelectedTab?.Children, node.Id); if (actual is null) return; AddNodeMenu(menu, actual); }
        LauncherTree.ContextMenu = menu; if (keyboard) OpenContextMenu(menu, target); else menu.IsOpen = true;
    }
    private void AddNodeMenu(ContextMenu menu, LauncherNode node)
    {
        menu.Items.Add(Menu("すべて折りたたむ", CollapseAll)); menu.Items.Add(new Separator());
        if (node is GroupNode) { AddCreationItems(menu, node.Id); menu.Items.Add(new Separator()); }
        if (node is not DirectoryItem) menu.Items.Add(Menu("名前をコピー", () => CopyText(DataValidator.NodeLabel(node))));
        if (node is FileItem or DirectoryItem) menu.Items.Add(Menu("パスをコピー", () => CopyText(((node is NamedLauncherItem named) ? named.Target : ((DirectoryItem)node).Target))));
        else if (node is UrlItem url) menu.Items.Add(Menu("URLをコピー", () => CopyText(url.Target)));
        if (node is not GroupNode) menu.Items.Add(new Separator());
        if (node is not DirectoryItem) menu.Items.Add(Menu("名前を変更", () => RenameNode(node)));
        if (node is FileItem file) { menu.Items.Add(Menu("起動対象を変更", () => ChangeTarget(file))); menu.Items.Add(Menu("Windowsのプロパティを開く", () => OpenProperties(file))); }
        else if (node is DirectoryItem directory) menu.Items.Add(Menu("参照先を変更", () => ChangeDirectoryTarget(directory)));
        else if (node is UrlItem url) menu.Items.Add(Menu("URLを変更", () => ChangeTarget(url)));
        menu.Items.Add(new Separator()); menu.Items.Add(Menu("アイコンを変更", () => ChangeNodeIcon(node))); if (node is FileItem retry) menu.Items.Add(Menu("アイコンを再取得", () => RetryNodeIcon(retry))); if (node is UrlItem site) menu.Items.Add(Menu("サイトのアイコンを取得", () => _ = FetchUrlIcon(site, SelectedTabId))); menu.Items.Add(Menu("アイコンを標準に戻す", () => SetNodeIcon(node.Id, null))); menu.Items.Add(new Separator()); menu.Items.Add(Menu("削除", () => DeleteNode(node)));
    }
    private void AddCreationItems(ContextMenu menu, string? parentId)
    {
        menu.Items.Add(Menu("グループを追加", () => AddGroup(parentId))); menu.Items.Add(Menu("ファイルを追加", () => AddFile(parentId))); menu.Items.Add(Menu("Directory参照追加（UNC可）", () => AddDirectory(parentId))); menu.Items.Add(Menu("URLを追加", () => AddUrl(parentId))); menu.Items.Add(Menu("フォルダを走査して一括登録", () => _app.ShowEditor(_app.SelectedTab?.Id)));
    }
    private void CollapseAll() { foreach (var item in EnumerateContainers(LauncherTree)) if (item.DataContext is GroupNode) item.IsExpanded = false; CaptureExpanded(_renderedTabId); }
    private static void CopyText(string text) { try { System.Windows.Clipboard.SetText(text); } catch (Exception) { } }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchText.Clear();
    private void AddGroup(string? parentId) { var d = new TextPromptDialog("グループを追加", "名前") { Owner = this }; if (ShowDialog(d.ShowDialog) == true) AddNode(new GroupNode { Name = d.Value }, parentId); }
    private void AddFile(string? parentId)
    {
        var open = new OpenFileDialog { Title = "登録するファイル", CheckFileExists = true, Filter = DirectoryCandidateRules.FileItemDialogFilter }; if (ShowDialog(open.ShowDialog) != true) return;
        var d = new ItemDialog("ファイルを追加", DirectoryCandidateRules.DefaultDisplayName(open.FileName), open.FileName, true) { Owner = this }; if (ShowDialog(d.ShowDialog) != true) return;
        var file = new FileItem { Name = d.ItemName, Target = d.Target }; AddNode(file, parentId); var icon = _app.IconService.TryExtract(file.Target, file.Name); if (icon is not null) SetNodeIcon(file.Id, icon);
    }
    private void AddDirectory(string? parentId)
    {
        using var folder = new System.Windows.Forms.FolderBrowserDialog { Description = "参照するDirectory（UNC可）" }; _dialogOpen = true; System.Windows.Forms.DialogResult result; try { result = folder.ShowDialog(); } finally { _dialogOpen = false; } if (result != System.Windows.Forms.DialogResult.OK) return;
        var d = new ItemDialog("Directory参照追加（UNC可）", "", folder.SelectedPath, true, showName: false) { Owner = this }; if (ShowDialog(d.ShowDialog) == true) AddNode(new DirectoryItem { Target = d.Target }, parentId);
    }
    private void AddUrl(string? parentId) { var d = new ItemDialog("URLを追加", "", "", true) { Owner = this }; if (ShowDialog(d.ShowDialog) == true) { var url = new UrlItem { Name = d.ItemName, Target = d.Target }; var tabId = SelectedTabId; AddNode(url, parentId); _ = FetchUrlIcon(url, tabId); } }
    private void AddNode(LauncherNode node, string? parentId)
    {
        var tabId = _app.SelectedTab?.Id; if (tabId is null) return; Commit(data => { var tab = data.Tabs.First(t => t.Id == tabId); var target = parentId is null ? tab.Children : (FindNode(tab.Children, parentId) as GroupNode)?.Children ?? throw new InvalidDataException("登録先Groupが見つかりません。"); node.Order = target.Count; target.Add(node); });
    }
    private void RenameNode(LauncherNode node) { var d = new TextPromptDialog("名前を変更", "表示名", DataValidator.NodeLabel(node)) { Owner = this }; if (ShowDialog(d.ShowDialog) == true) Commit(data => { var found = FindNode(data.Tabs.First(t => t.Id == SelectedTabId).Children, node.Id); if (found is GroupNode group) group.Name = d.Value; else if (found is NamedLauncherItem item) item.Name = d.Value; }); }
    private void ChangeTarget(NamedLauncherItem node) { var d = new TextPromptDialog(node is UrlItem ? "URLを変更" : "起動対象を変更", "対象", node.Target) { Owner = this }; if (ShowDialog(d.ShowDialog) == true) Commit(data => ((NamedLauncherItem)FindNode(data.Tabs.First(t => t.Id == SelectedTabId).Children, node.Id)!).Target = d.Value); }
    private void ChangeDirectoryTarget(DirectoryItem node) { var d = new TextPromptDialog("参照先を変更", "対象", node.Target) { Owner = this }; if (ShowDialog(d.ShowDialog) == true) Commit(data => ((DirectoryItem)FindNode(data.Tabs.First(t => t.Id == SelectedTabId).Children, node.Id)!).Target = d.Value); }
    private void ChangeNodeIcon(LauncherNode node)
    {
        var dialog = new OpenFileDialog { Title = "アイコンに使う画像", Filter = "画像|*.png;*.jpg;*.jpeg;*.bmp;*.ico" }; if (ShowDialog(dialog.ShowDialog) != true) return;
        try { SetNodeIcon(node.Id, _app.IconService.ImportImage(dialog.FileName, DataValidator.NodeLabel(node))); } catch (Exception ex) { ShowDialog(() => MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); }
    }
    private void RetryNodeIcon(FileItem node) { var icon = _app.IconService.TryExtract(node.Target, node.Name); if (icon is null) { ShowDialog(() => MessageBox.Show("対象ファイルからアイコンを取得できませんでした。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Warning)); return; } SetNodeIcon(node.Id, icon); }
    private async Task FetchUrlIcon(UrlItem node, string tabId)
    {
        var icon = await _app.SiteIconService.TryFetchAsync(node.Target, node.Name); if (icon is null) return;
        Commit(data => { var tab = data.Tabs.FirstOrDefault(t => t.Id == tabId); var found = tab is null ? null : FindNode(tab.Children, node.Id); if (found is not null) found.Icon = icon; });
    }
    private void SetNodeIcon(string id, string? icon) => Commit(data => FindNode(data.Tabs.First(t => t.Id == SelectedTabId).Children, id)!.Icon = icon);
    private void OpenProperties(FileItem node) { if (!_app.LaunchService.OpenProperties(new WindowInteropHelper(this).Handle, node.Target)) ShowDialog(() => MessageBox.Show("Windowsのプロパティを開けませんでした。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); }
    private void DeleteNode(LauncherNode node) { if (ShowDialog(() => MessageBox.Show($"「{DataValidator.NodeLabel(node)}」を削除しますか？\nこの操作は元に戻せません。", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning)) == MessageBoxResult.Yes) Commit(data => { var tab = data.Tabs.First(t => t.Id == SelectedTabId); RemoveNode(tab.Children, node.Id); NormalizeOrders(tab.Children); }); }

    private void TabsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        ShowTabContextMenu(item?.DataContext as LauncherTab, (FrameworkElement?)item ?? TabsList, false); e.Handled = true;
    }
    private void OpenTabContextMenu() => ShowTabContextMenu(TabsList.SelectedItem as LauncherTab ?? _app.SelectedTab, (FrameworkElement?)(TabsList.ItemContainerGenerator.ContainerFromItem(TabsList.SelectedItem) as ListBoxItem) ?? TabsList, true);
    private void ShowTabContextMenu(LauncherTab? tab, FrameworkElement target, bool keyboard)
    {
        var menu = new ContextMenu();
        if (tab is null) { menu.Items.Add(Menu("設定", _app.ShowSettings)); menu.Items.Add(Menu("ランチャーの新規登録", NewTab)); }
        else { menu.Items.Add(Menu("このランチャーを編集", () => _app.ShowEditor(tab.Id))); menu.Items.Add(Menu("このランチャーを複製", () => DuplicateTab(tab))); menu.Items.Add(new Separator()); menu.Items.Add(Menu("名前を変更", () => RenameTab(tab))); menu.Items.Add(Menu("アイコンを変更", () => ChangeTabIcon(tab))); menu.Items.Add(Menu("アイコンを標準に戻す", () => Commit(d => d.Tabs.First(x => x.Id == tab.Id).Icon = null))); menu.Items.Add(Menu("非表示にする", () => Commit(d => d.Tabs.First(x => x.Id == tab.Id).IsVisible = false))); menu.Items.Add(Menu("削除", () => DeleteTab(tab))); menu.Items.Add(new Separator()); menu.Items.Add(Menu("設定", _app.ShowSettings)); menu.Items.Add(Menu("ランチャーの新規登録", NewTab)); }
        TabsList.ContextMenu = menu; if (keyboard) OpenContextMenu(menu, target); else menu.IsOpen = true;
    }
    private void NewTab() { var d = new TextPromptDialog("ランチャーの新規登録", "名前") { Owner = this }; if (ShowDialog(d.ShowDialog) == true) Commit(data => data.Tabs.Add(new LauncherTab { Name = d.Value, Order = data.Tabs.Count })); }
    private void DeleteTab(LauncherTab tab) { if (ShowDialog(() => MessageBox.Show($"App Launcher\n「{tab.Name}」を削除しますか？", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning)) == MessageBoxResult.Yes) Commit(d => { d.Tabs.Remove(d.Tabs.First(x => x.Id == tab.Id)); NormalizeTabOrders(d.Tabs); }); }
    private System.Windows.Controls.MenuItem Menu(string title, Action action) { var item = new System.Windows.Controls.MenuItem { Header = title }; item.Click += (_, _) => action(); return item; }
    private void RenameTab(LauncherTab tab) { var d = new TextPromptDialog("名前変更", "名前", tab.Name) { Owner = this }; if (ShowDialog(d.ShowDialog) == true) Commit(data => data.Tabs.First(x => x.Id == tab.Id).Name = d.Value); }
    private void DuplicateTab(LauncherTab tab) { if (!_app.TryDuplicateTab(tab.Id, out _, out var error)) ShowDialog(() => MessageBox.Show(error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); }
    private void ChangeTabIcon(LauncherTab tab)
    {
        var d = new OpenFileDialog { Title = "アイコンに使う画像", Filter = "画像|*.png;*.jpg;*.jpeg;*.bmp;*.ico" }; if (ShowDialog(d.ShowDialog) != true) return;
        try { var icon = _app.IconService.ImportImage(d.FileName, tab.Name); Commit(data => data.Tabs.First(x => x.Id == tab.Id).Icon = icon); } catch (Exception ex) { ShowDialog(() => MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); }
    }

    private void MoveTreeSelection(int delta)
    {
        var items = EnumerateVisibleContainers(LauncherTree).Where(x => x.DataContext is LauncherNode).ToList(); if (items.Count == 0) return;
        var current = items.FindIndex(x => ReferenceEquals(x.DataContext, LauncherTree.SelectedItem)); var index = current < 0 ? (delta > 0 ? 0 : items.Count - 1) : Math.Clamp(current + delta, 0, items.Count - 1);
        var target = items[index]; target.IsSelected = true; target.Focus(); target.BringIntoView();
    }
    private void MoveTabSelection(int delta)
    {
        var tabs = _app.VisibleTabs; if (tabs.Count == 0) return;
        var currentId = _app.SelectedTab?.Id; var current = currentId is null ? -1 : tabs.ToList().FindIndex(x => x.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase)); var index = current < 0 ? (delta > 0 ? 0 : tabs.Count - 1) : Math.Clamp(current + delta, 0, tabs.Count - 1); var target = tabs[index];
        TabsList.SelectedItem = target; _app.SelectTab(target.Id);
        Dispatcher.BeginInvoke(() => { if (TabsList.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem item) { item.Focus(); item.BringIntoView(); } });
    }
    private static void OpenContextMenu(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }
    private static bool IsDescendantOf(DependencyObject? value, DependencyObject ancestor)
    {
        while (value is not null) { if (ReferenceEquals(value, ancestor)) return true; value = VisualTreeHelper.GetParent(value); }
        return false;
    }

    private string SelectedTabId => _app.SelectedTab?.Id ?? throw new InvalidOperationException("表示中のApp Launcherがありません。");
    private void Commit(Action<OpenGepaData> change) { if (!_app.TryCommit(change, out var error)) ShowDialog(() => MessageBox.Show(error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); }
    private T ShowDialog<T>(Func<T> show) { _dialogOpen = true; try { return show(); } finally { _dialogOpen = false; } }
    private void CaptureExpanded(string? tabId) { if (tabId is not null) _expandedByTab[tabId] = EnumerateContainers(LauncherTree).Where(x => x.IsExpanded && x.DataContext is GroupNode).Select(x => ((GroupNode)x.DataContext).Id).ToHashSet(StringComparer.OrdinalIgnoreCase); }
    private void ExpandAll() { foreach (var item in EnumerateContainers(LauncherTree)) if (item.DataContext is GroupNode) item.IsExpanded = true; }
    private void RestoreExpanded(IReadOnlySet<string> ids) { foreach (var item in EnumerateContainers(LauncherTree)) if (item.DataContext is GroupNode group) item.IsExpanded = ids.Contains(group.Id); }
    private static IEnumerable<TreeViewItem> EnumerateContainers(ItemsControl root) { foreach (var value in root.Items) if (root.ItemContainerGenerator.ContainerFromItem(value) is TreeViewItem item) { yield return item; foreach (var child in EnumerateContainers(item)) yield return child; } }
    private static IEnumerable<TreeViewItem> EnumerateVisibleContainers(ItemsControl root) { foreach (var value in root.Items) if (root.ItemContainerGenerator.ContainerFromItem(value) is TreeViewItem item) { yield return item; if (item.IsExpanded) foreach (var child in EnumerateVisibleContainers(item)) yield return child; } }
    private static LauncherNode? FindNode(IEnumerable<LauncherNode>? nodes, string id) { if (nodes is null) return null; foreach (var node in nodes) { if (node.Id == id) return node; if (node is GroupNode group) { var found = FindNode(group.Children, id); if (found is not null) return found; } } return null; }
    private static bool RemoveNode(ObservableCollection<LauncherNode> nodes, string id) { var item = nodes.FirstOrDefault(x => x.Id == id); if (item is not null) return nodes.Remove(item); return nodes.OfType<GroupNode>().Any(group => RemoveNode(group.Children, id)); }
    private static void NormalizeOrders(ObservableCollection<LauncherNode> nodes) { for (var i = 0; i < nodes.Count; i++) { nodes[i].Order = i; if (nodes[i] is GroupNode group) NormalizeOrders(group.Children); } }
    private static void NormalizeTabOrders(ObservableCollection<LauncherTab> tabs) { var ordered = tabs.OrderBy(x => x.Order).ToList(); for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i; }
    private static T? FindAncestor<T>(DependencyObject? value) where T : DependencyObject { while (value is not null && value is not T) value = VisualTreeHelper.GetParent(value); return value as T; }
    private static TreeViewItem? FindContainer(ItemsControl root, object value) { if (root.ItemContainerGenerator.ContainerFromItem(value) is TreeViewItem direct) return direct; foreach (var item in root.Items) if (root.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem child) { var found = FindContainer(child, value); if (found is not null) return found; } return null; }
}
