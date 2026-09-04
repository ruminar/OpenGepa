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
    private TreeViewItem? _pressedTreeItem;
    private bool _pressedTreeExpander;
    private bool _pressedTreeModifier, _lastTreeClickModifier;
    private System.Windows.Point _treeDragStart, _tabDragStart;
    private string? _treeDragNodeId, _tabDragTabId;
    private const string TreeReorderDragFormat = "OpenGepa.MainTreeReorder";
    private const string TabReorderDragFormat = "OpenGepa.MainTabReorder";
    private System.Windows.Controls.Primitives.Popup? _renamePopup;
    private LauncherNode? _renamingNode;
    private readonly Dictionary<string, HashSet<string>> _expandedByTab = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _searchByTab = new(StringComparer.OrdinalIgnoreCase);
    private string? _renderedTabId;
    public MainWindow(AppService app) { InitializeComponent(); _app = app; _app.DataChanged += (_, _) => Dispatcher.BeginInvoke(() => RefreshData()); }
    private void Window_SourceInitialized(object? sender, EventArgs e) => ThemePalette.Apply(_app.Data.Appearance);
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { if (!App.IsExiting) { e.Cancel = true; Hide(); } }
    private void Window_StateChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; Hide(); } }

    public void RefreshData(bool refreshEnvironment = false)
    {
        Icon = WindowIconService.Load(_app); CaptureExpanded(_renderedTabId); _refreshing = true; var visible = _app.VisibleTabs; var selected = _app.SelectedTab; TabsList.ItemsSource = visible; TabsList.SelectedItem = selected; PinToggle.IsChecked = _app.Data.IsLauncherPinned; Topmost = !_app.Data.IsLauncherPinned; Title = selected is null ? "OpenGepa" : $"OpenGepa - {selected.Name}";
        _renderedTabId = selected?.Id; if (selected is not null) _app.GetDisplayChildren(selected, refreshEnvironment);
        _settingSearch = true; SearchText.Text = selected is not null && _searchByTab.TryGetValue(selected.Id, out var search) ? search : ""; _settingSearch = false; ApplySearch(false); EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed; _refreshing = false;
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
    private void PinToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        var pinned = PinToggle.IsChecked == true; Topmost = !pinned;
        if (_app.TrySetLauncherPinned(pinned, out var error)) return;
        Topmost = !_app.Data.IsLauncherPinned;
        ShowDialog(() => MessageBox.Show(error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error));
        RefreshData();
    }
    private void SearchText_Changed(object sender, TextChangedEventArgs e) { if (_settingSearch) return; if (_renderedTabId is not null) _searchByTab[_renderedTabId] = SearchText.Text; ApplySearch(true); }

    private void ApplySearch(bool captureState)
    {
        var tab = _app.SelectedTab; if (tab is null) { LauncherTree.ItemsSource = null; return; } var search = NameRules.Normalize(SearchText.Text);
        var nodes = _app.GetDisplayChildren(tab);
        if (search.Length == 0) { LauncherTree.ItemsSource = nodes; var expanded = _expandedByTab.TryGetValue(tab.Id, out var saved) ? saved : new HashSet<string>(StringComparer.OrdinalIgnoreCase); Dispatcher.BeginInvoke(() => RestoreExpanded(expanded)); return; }
        if (captureState) CaptureExpanded(tab.Id); LauncherTree.ItemsSource = Filter(nodes, search); Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(ExpandAll));
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
    private void LauncherTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        _pressedTreeItem = FindAncestor<TreeViewItem>(source);
        _pressedTreeExpander = FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(source) is not null;
        _pressedTreeModifier = LauncherClickRules.BlocksMouseAction(Keyboard.Modifiers);
        _treeDragNodeId = null;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.None && !_pressedTreeExpander && _app.SelectedTab is { IsSystemTab: false } && _pressedTreeItem?.DataContext is LauncherNode node) { _treeDragStart = e.GetPosition(LauncherTree); _treeDragNodeId = node.Id; }
    }
    private async void LauncherTree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pressedItem = _pressedTreeItem; var pressedExpander = _pressedTreeExpander; var modified = _pressedTreeModifier || LauncherClickRules.BlocksMouseAction(Keyboard.Modifiers);
        _pressedTreeItem = null; _pressedTreeExpander = false; _pressedTreeModifier = false; _lastTreeClickModifier = modified;
        var releasedItem = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (pressedItem is null || pressedExpander || modified || !ReferenceEquals(pressedItem, releasedItem)) return;
        if (pressedItem.DataContext is GroupNode && e.ClickCount == 1) { pressedItem.IsExpanded = !pressedItem.IsExpanded; e.Handled = true; return; }
        if (pressedItem.DataContext is LauncherNode launcher && ShouldLaunch(launcher, 1)) { e.Handled = true; await Launch(launcher); }
    }
    private async void LauncherTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var hit = LauncherTree.InputHitTest(e.GetPosition(LauncherTree)) as DependencyObject; var item = FindAncestor<TreeViewItem>(hit);
        if (item?.DataContext is LauncherNode launcher && !_pressedTreeModifier && !_lastTreeClickModifier && !LauncherClickRules.BlocksMouseAction(Keyboard.Modifiers) && ShouldLaunch(launcher, 2)) { e.Handled = true; await Launch(launcher); }
    }
    private async void LauncherTree_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F2 && LauncherTree.SelectedItem is LauncherNode rename && CanInlineRename(rename)) { BeginInlineRename(rename); e.Handled = true; }
        else if (LauncherTree.SelectedItem is GroupNode && (e.Key == Key.Enter || e.Key == Key.Space)) { var item = FindContainer(LauncherTree, LauncherTree.SelectedItem); if (item is not null) item.IsExpanded = !item.IsExpanded; e.Handled = true; }
        else if (LauncherTree.SelectedItem is LauncherNode item && IsLaunchable(item) && e.Key == Key.Enter) { e.Handled = true; await Launch(item); }
    }
    private async Task Launch(LauncherNode item)
    {
        if (_launching) return;
        if (item is PresetItem { RequiresConfirmation: true } && ShowDialog(() => MessageBox.Show($"「{DataValidator.NodeLabel(item)}」を実行しますか？", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)) != MessageBoxResult.Yes) return;
        _launching = true; try { var result = await _app.LaunchService.LaunchAsync(item); if (result.Success) { if (!_app.Data.IsLauncherPinned) Hide(); } else ShowDialog(() => MessageBox.Show($"「{DataValidator.NodeLabel(item)}」を起動できませんでした。\n\n{result.Error}", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); } finally { _launching = false; }
    }
    private void LauncherTree_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _treeDragNodeId is null) return;
        var point = e.GetPosition(LauncherTree); if (Math.Abs(point.X - _treeDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _treeDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var id = _treeDragNodeId; _treeDragNodeId = null;
        DragDrop.DoDragDrop(LauncherTree, new System.Windows.DataObject(TreeReorderDragFormat, id), System.Windows.DragDropEffects.Move);
    }
    private void LauncherTree_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(TreeReorderDragFormat) && e.Data.GetData(TreeReorderDragFormat) is string sourceId && CanReorderTree(sourceId, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as LauncherNode))
        {
            e.Effects = System.Windows.DragDropEffects.Move; e.Handled = true; return;
        }
        if (CanAddExternalDrop(e.Data)) { e.Effects = System.Windows.DragDropEffects.Copy; e.Handled = true; }
    }
    private void LauncherTree_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(TreeReorderDragFormat) && e.Data.GetData(TreeReorderDragFormat) is string sourceId)
        {
            var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject); var target = container?.DataContext as LauncherNode;
            if (!CanReorderTree(sourceId, target)) return;
            var after = container is not null && e.GetPosition(container).Y > container.ActualHeight / 2;
            var tabId = SelectedTabId; var targetId = target!.Id;
            Commit(data => { var tab = data.Tabs.First(item => item.Id == tabId); var siblings = FindSiblingCollection(tab.Children, sourceId) ?? throw new InvalidDataException("移動元が見つかりません。"); LauncherReorderRules.MoveSibling(siblings, sourceId, targetId, after); });
            e.Handled = true; return;
        }
        if (!CanAddExternalDrop(e.Data)) return;
        AddExternalDrop(e.Data, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as LauncherNode); e.Handled = true;
    }
    private bool CanReorderTree(string sourceId, LauncherNode? target)
    {
        var tab = _app.SelectedTab; if (tab is null || tab.IsSystemTab || target is null || sourceId == target.Id) return false;
        return FindSiblingCollection(tab.Children, sourceId) is { } source && FindSiblingCollection(tab.Children, target.Id) is { } destination && ReferenceEquals(source, destination);
    }
    private bool CanAddExternalDrop(System.Windows.IDataObject data)
    {
        var tab = _app.SelectedTab; if (tab is null || tab.IsSystemTab) return false;
        return ExternalDropRules.TryGetUrl(data, out _) || (!tab.IsWebTab && data.GetDataPresent(System.Windows.DataFormats.FileDrop));
    }
    private void AddExternalDrop(System.Windows.IDataObject data, LauncherNode? target)
    {
        var tab = _app.SelectedTab; if (tab is null || tab.IsSystemTab) return;
        var destinationId = target switch { GroupNode group => group.Id, not null => FindParentGroupId(tab.Children, target.Id), _ => null };
        if (ExternalDropRules.TryGetUrl(data, out var url)) { AddDroppedUrl(url, destinationId); return; }
        if (tab.IsWebTab || !data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        var paths = (string[])data.GetData(System.Windows.DataFormats.FileDrop)!;
        AddDroppedPaths(paths, destinationId);
    }
    private void AddDroppedUrl(string target, string? destinationId)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        var tab = _app.SelectedTab; if (tab is null) return;
        var siblings = destinationId is null ? tab.Children : (FindNode(tab.Children, destinationId) as GroupNode)?.Children ?? [];
        var node = new UrlItem { Name = UrlRegistrationRules.UniqueDroppedName(uri, siblings), Target = uri.AbsoluteUri }; var tabId = tab.Id;
        if (!_app.TryCommit(data => { var current = data.Tabs.First(item => item.Id == tabId); var collection = destinationId is null ? current.Children : ((FindNode(current.Children, destinationId) as GroupNode)?.Children ?? throw new InvalidDataException("登録先Groupが見つかりません。")); node.Order = collection.Count; collection.Add(node); }, out var error)) { ShowDialog(() => MessageBox.Show(error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); return; }
        _ = FetchUrlIcon(node, tabId);
    }
    private void AddDroppedPaths(IEnumerable<string> paths, string? destinationId)
    {
        var tab = _app.SelectedTab; if (tab is null) return; var tabId = tab.Id; var addedFiles = new List<FileItem>();
        if (!_app.TryCommit(data =>
        {
            var current = data.Tabs.First(item => item.Id == tabId); var collection = destinationId is null ? current.Children : ((FindNode(current.Children, destinationId) as GroupNode)?.Children ?? throw new InvalidDataException("登録先Groupが見つかりません。"));
            foreach (var path in paths)
            {
                if (File.Exists(path)) { var file = new FileItem { Name = UrlRegistrationRules.UniqueName(DirectoryCandidateRules.DefaultDisplayName(path), collection), Target = path, Order = collection.Count }; collection.Add(file); addedFiles.Add(file); }
                else if (Directory.Exists(path) && !collection.OfType<DirectoryItem>().Any(item => item.Target.Equals(path, StringComparison.OrdinalIgnoreCase))) collection.Add(new DirectoryItem { Target = path, Order = collection.Count });
            }
        }, out var error)) { ShowDialog(() => MessageBox.Show(error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); return; }
        foreach (var file in addedFiles) { var icon = _app.IconService.TryExtract(file.Target, file.Name); if (icon is not null) SetNodeIcon(file.Id, icon); }
    }

    private bool ShouldLaunch(LauncherNode item, int clickCount)
    {
        if (!IsLaunchable(item)) return false;
        return item is StoreAppItem or PresetItem or WindowsMenuShortcutItem || _app.Data.ItemLaunch.GetClickCount(item) == clickCount;
    }
    private static bool IsLaunchable(LauncherNode item) => item is FileItem or DirectoryItem or UrlItem or StoreAppItem or PresetItem;

    private void LauncherTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hit = LauncherTree.InputHitTest(e.GetPosition(LauncherTree)) as DependencyObject; var node = FindAncestor<TreeViewItem>(hit)?.DataContext as LauncherNode;
        ShowTreeContextMenu(node, (FrameworkElement?)FindAncestor<TreeViewItem>(hit) ?? LauncherTree, false); e.Handled = true;
    }
    private void OpenTreeContextMenu() => ShowTreeContextMenu(LauncherTree.SelectedItem as LauncherNode, (FrameworkElement?)FindContainer(LauncherTree, LauncherTree.SelectedItem) ?? LauncherTree, true);
    private void ShowTreeContextMenu(LauncherNode? node, FrameworkElement target, bool keyboard)
    {
        var menu = new ContextMenu();
        var tab = _app.SelectedTab;
        if (tab is null) return;
        if (node is null) AddRootMenu(menu, tab);
        else { var actual = FindNode(_app.GetDisplayChildren(tab), node.Id); if (actual is null) return; AddNodeMenu(menu, actual, tab); }
        LauncherTree.ContextMenu = menu; if (keyboard) OpenContextMenu(menu, target); else menu.IsOpen = true;
    }
    private void AddRootMenu(ContextMenu menu, LauncherTab tab)
    {
        menu.Items.Add(Menu("すべて折りたたむ", CollapseAll)); menu.Items.Add(new Separator());
        if (tab.Kind == LauncherTabKinds.WindowsMenu)
        {
            menu.Items.Add(Menu("更新", RefreshSpecialTab));
            if (_app.WindowsMenuService.CanEdit(WindowsMenuSource.CurrentUser, _app.Data.WindowsMenu) || _app.WindowsMenuService.CanEdit(WindowsMenuSource.AllUsers, _app.Data.WindowsMenu)) menu.Items.Add(Menu("ショートカットを作成", () => CreateWindowsMenuShortcut(null)));
            return;
        }
        if (tab.IsSystemTab) { menu.Items.Add(Menu("更新", RefreshSpecialTab)); return; }
        AddCreationItems(menu, null);
    }
    private void AddNodeMenu(ContextMenu menu, LauncherNode node, LauncherTab tab)
    {
        if (tab.Kind == LauncherTabKinds.WindowsMenu) { AddWindowsMenuNodeMenu(menu, node); return; }
        if (tab.Kind == LauncherTabKinds.StoreApps)
        {
            menu.Items.Add(Menu("すべて折りたたむ", CollapseAll));
            if (node is StoreAppItem app) { menu.Items.Add(new Separator()); menu.Items.Add(Menu("AUMIDをコピー", () => CopyText(app.Aumid))); }
            return;
        }
        if (tab.Kind == LauncherTabKinds.Presets) { menu.Items.Add(Menu("すべて折りたたむ", CollapseAll)); return; }
        AddRegularNodeMenu(menu, node);
    }
    private void AddRegularNodeMenu(ContextMenu menu, LauncherNode node)
    {
        var web = _app.SelectedTab?.IsWebTab == true;
        menu.Items.Add(Menu("すべて折りたたむ", CollapseAll)); menu.Items.Add(new Separator());
        if (node is GroupNode) { AddCreationItems(menu, node.Id); if (web) menu.Items.Add(Menu("配下のサイトのアイコンを取得", () => _app.QueueMissingGroupIcons(SelectedTabId, node.Id))); menu.Items.Add(new Separator()); }
        if (node is not DirectoryItem) menu.Items.Add(Menu("名前をコピー", () => CopyText(DataValidator.NodeLabel(node))));
        if (node is FileItem or DirectoryItem) menu.Items.Add(Menu("パスをコピー", () => CopyText(((node is NamedLauncherItem named) ? named.Target : ((DirectoryItem)node).Target))));
        else if (node is UrlItem url) menu.Items.Add(Menu("URLをコピー", () => CopyText(url.Target)));
        if (node is not GroupNode) menu.Items.Add(new Separator());
        if (node is not DirectoryItem) menu.Items.Add(Menu("名前を変更", () => RenameNode(node)));
        if (node is FileItem file) { menu.Items.Add(Menu("起動対象を変更", () => ChangeTarget(file))); menu.Items.Add(Menu("Windowsのプロパティを開く", () => OpenProperties(file))); }
        else if (node is DirectoryItem directory) menu.Items.Add(Menu("参照先を変更", () => ChangeDirectoryTarget(directory)));
        else if (node is UrlItem url) { menu.Items.Add(Menu("URLを変更", () => ChangeTarget(url))); menu.Items.Add(Menu("ページタイトルを名前に設定", () => _ = FetchPageTitle(url, SelectedTabId))); }
        menu.Items.Add(new Separator()); menu.Items.Add(Menu("アイコンを変更", () => ChangeNodeIcon(node))); if (!web && node is FileItem retry) menu.Items.Add(Menu("アイコンを再取得", () => RetryNodeIcon(retry))); if (node is UrlItem site) { menu.Items.Add(Menu("サイトのアイコンを取得", () => _ = FetchUrlIcon(site, SelectedTabId))); menu.Items.Add(Menu("アイコンURLを指定して取得", () => FetchSpecifiedUrlIcon(site, SelectedTabId))); } menu.Items.Add(Menu("アイコンを標準に戻す", () => SetNodeIcon(node.Id, null))); menu.Items.Add(new Separator()); menu.Items.Add(Menu("削除", () => DeleteNode(node)));
    }
    private void AddCreationItems(ContextMenu menu, string? parentId)
    {
        var web = _app.SelectedTab?.IsWebTab == true;
        menu.Items.Add(Menu("グループを追加", () => AddGroup(parentId)));
        if (web) { menu.Items.Add(Menu("URLを追加", () => AddUrl(parentId))); return; }
        menu.Items.Add(Menu("ファイルを追加", () => AddFile(parentId))); menu.Items.Add(Menu("Directory参照追加（UNC可）", () => AddDirectory(parentId))); menu.Items.Add(Menu("URLを追加", () => AddUrl(parentId))); menu.Items.Add(Menu("ショートカットを作成", () => CreateManagedShortcut(parentId))); menu.Items.Add(Menu("フォルダを走査して一括登録", () => _app.ShowEditor(_app.SelectedTab?.Id)));
    }
    private void AddWindowsMenuNodeMenu(ContextMenu menu, LauncherNode node)
    {
        menu.Items.Add(Menu("すべて折りたたむ", CollapseAll)); menu.Items.Add(new Separator());
        if (node is WindowsMenuGroupNode group)
        {
            if (CanEditGroup(group)) menu.Items.Add(Menu("ショートカットを作成", () => CreateWindowsMenuShortcut(group)));
            return;
        }
        if (node is not WindowsMenuShortcutItem shortcut) return;
        menu.Items.Add(Menu("名前をコピー", () => CopyText(shortcut.Name)));
        menu.Items.Add(Menu("パスをコピー", () => CopyText(shortcut.Target)));
        menu.Items.Add(Menu("Windowsのプロパティを開く", () => OpenProperties(shortcut)));
        if (!_app.WindowsMenuService.CanEdit(shortcut.Source, _app.Data.WindowsMenu)) return;
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("名前を変更", () => RenameWindowsMenuShortcut(shortcut)));
        menu.Items.Add(Menu("削除", () => DeleteWindowsMenuShortcut(shortcut)));
    }
    private bool CanEditGroup(WindowsMenuGroupNode group) =>
        group.CurrentUserPath is not null && _app.WindowsMenuService.CanEdit(WindowsMenuSource.CurrentUser, _app.Data.WindowsMenu) ||
        group.AllUsersPath is not null && _app.WindowsMenuService.CanEdit(WindowsMenuSource.AllUsers, _app.Data.WindowsMenu);
    private void CreateWindowsMenuShortcut(WindowsMenuGroupNode? parent)
    {
        if (!TrySelectWindowsMenuSource(parent, out var source)) return;
        var open = new OpenFileDialog { Title = "ショートカットの起動対象", CheckFileExists = true, Filter = DirectoryCandidateRules.FileItemDialogFilter };
        if (ShowDialog(open.ShowDialog) != true) return;
        var dialog = new TextPromptDialog("Start Menuのショートカットを作成", "表示名", DirectoryCandidateRules.DefaultDisplayName(open.FileName)) { Owner = this };
        if (ShowDialog(dialog.ShowDialog) != true) return;
        try
        {
            var path = _app.WindowsMenuService.CreateShortcut(source, _app.Data.WindowsMenu, open.FileName, dialog.Value, parent);
            RefreshSpecialTab();
            OpenProperties(new FileItem { Target = path, Name = Path.GetFileNameWithoutExtension(path) });
        }
        catch (Exception ex) { ShowDialog(() => MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); }
    }
    private bool TrySelectWindowsMenuSource(WindowsMenuGroupNode? parent, out WindowsMenuSource source)
    {
        var current = _app.WindowsMenuService.CanEdit(WindowsMenuSource.CurrentUser, _app.Data.WindowsMenu) && (parent is null || parent.CurrentUserPath is not null);
        var allUsers = _app.WindowsMenuService.CanEdit(WindowsMenuSource.AllUsers, _app.Data.WindowsMenu) && (parent is null || parent.AllUsersPath is not null);
        source = WindowsMenuSource.CurrentUser;
        if (!current && !allUsers) { ShowDialog(() => MessageBox.Show("この場所を編集するには、設定画面でStart Menu編集を許可してください。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Information)); return false; }
        if (current && !allUsers) return true;
        if (!current) { source = WindowsMenuSource.AllUsers; return true; }
        var selected = ShowDialog(() => MessageBox.Show("ショートカットの作成先を選んでください。\n［はい］: 現在のユーザー用\n［いいえ］: 全ユーザー用", "OpenGepa", MessageBoxButton.YesNoCancel, MessageBoxImage.Question));
        if (selected == MessageBoxResult.Cancel) return false;
        source = selected == MessageBoxResult.Yes ? WindowsMenuSource.CurrentUser : WindowsMenuSource.AllUsers;
        return true;
    }
    private void RenameWindowsMenuShortcut(WindowsMenuShortcutItem shortcut)
    {
        Dispatcher.BeginInvoke(() => BeginInlineRename(shortcut));
    }
    private void DeleteWindowsMenuShortcut(WindowsMenuShortcutItem shortcut)
    {
        var source = shortcut.Source == WindowsMenuSource.CurrentUser ? "現在のユーザー用 Start Menu" : "全ユーザー用 Start Menu";
        if (ShowDialog(() => MessageBox.Show($"{source} の「{shortcut.Name}」を削除しますか？\nこの操作は元に戻せません。", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)) != MessageBoxResult.Yes) return;
        try { _app.WindowsMenuService.DeleteShortcut(shortcut, _app.Data.WindowsMenu); RefreshSpecialTab(); }
        catch (Exception ex) { ShowDialog(() => MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); }
    }
    private void RefreshSpecialTab()
    {
        if (_app.SelectedTab is { IsSystemTab: true } tab) tab.RuntimeChildren = null;
        RefreshData(true);
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
    private void CreateManagedShortcut(string? parentId)
    {
        var open = new OpenFileDialog { Title = "ショートカットの起動対象", CheckFileExists = true, Filter = DirectoryCandidateRules.FileItemDialogFilter };
        if (ShowDialog(open.ShowDialog) != true) return;
        var dialog = new TextPromptDialog("ショートカットを作成", "表示名", DirectoryCandidateRules.DefaultDisplayName(open.FileName)) { Owner = this };
        if (ShowDialog(dialog.ShowDialog) != true) return;
        if (!_app.TryCreateManagedShortcut(SelectedTabId, parentId, open.FileName, dialog.Value, out var item, out var error)) { ShowDialog(() => MessageBox.Show(error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); return; }
        var icon = _app.IconService.TryExtract(item!.Target, item.Name); if (icon is not null) SetNodeIcon(item.Id, icon);
        OpenProperties(item);
    }
    private void AddNode(LauncherNode node, string? parentId)
    {
        var tabId = _app.SelectedTab?.Id; if (tabId is null || _app.SelectedTab?.IsSystemTab == true) return; Commit(data => { var tab = data.Tabs.First(t => t.Id == tabId); var target = parentId is null ? tab.Children : (FindNode(tab.Children, parentId) as GroupNode)?.Children ?? throw new InvalidDataException("登録先Groupが見つかりません。"); node.Order = target.Count; target.Add(node); });
    }
    private void RenameNode(LauncherNode node) => Dispatcher.BeginInvoke(() => BeginInlineRename(node));
    private bool CanInlineRename(LauncherNode node) => node switch
    {
        DirectoryItem => false,
        WindowsMenuShortcutItem shortcut => _app.WindowsMenuService.CanEdit(shortcut.Source, _app.Data.WindowsMenu),
        StoreAppItem or PresetItem or WindowsMenuGroupNode => false,
        _ => _app.SelectedTab is { IsSystemTab: false }
    };
    private void BeginInlineRename(LauncherNode node)
    {
        if (!CanInlineRename(node)) return;
        var container = FindContainer(LauncherTree, node);
        if (container is null) return;
        CancelInlineRename();
        var text = new System.Windows.Controls.TextBox { Text = DataValidator.NodeLabel(node), MinWidth = 120, Width = Math.Max(120, container.ActualWidth - 42), Padding = new Thickness(2, 0, 2, 0) };
        text.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CompleteInlineRename(true); e.Handled = true; } else if (e.Key == Key.Escape) { CompleteInlineRename(false); e.Handled = true; } };
        text.LostKeyboardFocus += (_, _) => CompleteInlineRename(true);
        _renamingNode = node;
        _renamePopup = new System.Windows.Controls.Primitives.Popup { PlacementTarget = container, Placement = System.Windows.Controls.Primitives.PlacementMode.Relative, HorizontalOffset = 33, VerticalOffset = 2, StaysOpen = true, Child = text, AllowsTransparency = true, IsOpen = true };
        Dispatcher.BeginInvoke(() => { text.Focus(); text.SelectAll(); });
    }
    private void CancelInlineRename()
    {
        var popup = _renamePopup; _renamePopup = null; _renamingNode = null;
        if (popup is not null) popup.IsOpen = false;
    }
    private void CompleteInlineRename(bool commit)
    {
        var popup = _renamePopup; var node = _renamingNode;
        if (popup?.Child is not System.Windows.Controls.TextBox text || node is null) return;
        CancelInlineRename();
        if (!commit) return;
        if (node is WindowsMenuShortcutItem shortcut)
        {
            try { _app.WindowsMenuService.RenameShortcut(shortcut, _app.Data.WindowsMenu, text.Text); RefreshSpecialTab(); }
            catch (Exception ex) { ShowDialog(() => MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); }
            return;
        }
        Commit(data => { var found = FindNode(data.Tabs.First(t => t.Id == SelectedTabId).Children, node.Id); if (found is GroupNode group) group.Name = text.Text; else if (found is NamedLauncherItem item) item.Name = text.Text; });
    }
    private void ChangeTarget(NamedLauncherItem node) { var d = new TextPromptDialog(node is UrlItem ? "URLを変更" : "起動対象を変更", "対象", node.Target) { Owner = this }; if (ShowDialog(d.ShowDialog) == true) Commit(data => { var found = (NamedLauncherItem)FindNode(data.Tabs.First(t => t.Id == SelectedTabId).Children, node.Id)!; found.Target = d.Value; if (found is FileItem file) file.IsTargetMissing = false; }); }
    private void ChangeDirectoryTarget(DirectoryItem node) { var d = new TextPromptDialog("参照先を変更", "対象", node.Target) { Owner = this }; if (ShowDialog(d.ShowDialog) == true) Commit(data => ((DirectoryItem)FindNode(data.Tabs.First(t => t.Id == SelectedTabId).Children, node.Id)!).Target = d.Value); }
    private void ChangeNodeIcon(LauncherNode node)
    {
        var dialog = new OpenFileDialog { Title = "アイコンに使う画像", Filter = "画像|*.png;*.jpg;*.jpeg;*.bmp;*.ico" }; if (ShowDialog(dialog.ShowDialog) != true) return;
        try { SetNodeIcon(node.Id, _app.IconService.ImportImage(dialog.FileName, DataValidator.NodeLabel(node))); } catch (Exception ex) { ShowDialog(() => MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); }
    }
    private void RetryNodeIcon(FileItem node) { var icon = _app.IconService.TryExtract(node.Target, node.Name); if (icon is null) { ShowDialog(() => MessageBox.Show("対象ファイルからアイコンを取得できませんでした。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Warning)); return; } SetNodeIcon(node.Id, icon); }
    private async Task FetchUrlIcon(UrlItem node, string tabId)
    {
        var result = await _app.SiteIconService.TryFetchAsync(node.Target, node.Name);
        if (!result.IsSuccess) { var dialog = new DiagnosticDialog("OpenGepa - URLアイコン診断", $"サイトのアイコンを取得できませんでした。\n対象: {node.Target}", result.Error ?? "詳細はありません。") { Owner = this }; ShowDialog(dialog.ShowDialog); return; }
        Commit(data => { var tab = data.Tabs.FirstOrDefault(t => t.Id == tabId); var found = tab is null ? null : FindNode(tab.Children, node.Id); if (found is not null) found.Icon = result.IconPath; });
    }
    private void FetchSpecifiedUrlIcon(UrlItem node, string tabId)
    {
        var dialog = new TextPromptDialog("アイコンURLを指定して取得", "アイコンURL（相対URL可）") { Owner = this };
        if (ShowDialog(dialog.ShowDialog) != true) return;
        if (_app.QueueSpecifiedBookmarkIcon(tabId, node, dialog.Value)) return;
        ShowDialog(() => MessageBox.Show("HTTPまたはHTTPSのアイコンURLを指定してください。\n相対URLは、このブックマークのURLを基準に解決します。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Warning));
    }
    private async Task FetchPageTitle(UrlItem node, string tabId)
    {
        var result = await _app.SiteIconService.TryFetchPageTitleAsync(node.Target);
        if (!result.IsSuccess) { ShowDialog(() => MessageBox.Show("ページタイトルを取得できませんでした。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Warning)); return; }
        Commit(data => { var tab = data.Tabs.FirstOrDefault(t => t.Id == tabId); var found = tab is null ? null : FindNode(tab.Children, node.Id) as UrlItem; if (found is not null) found.Name = UrlRegistrationRules.UniqueName(result.Value!, FindContainingCollection(tab!.Children, found.Id)!, found.Id); });
    }
    private static ObservableCollection<LauncherNode>? FindContainingCollection(ObservableCollection<LauncherNode> nodes, string id)
    { if (nodes.Any(x => x.Id == id)) return nodes; foreach (var group in nodes.OfType<GroupNode>()) { var found = FindContainingCollection(group.Children, id); if (found is not null) return found; } return null; }
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
        if (tab is null) AddNewTabItems(menu);
        else if (tab.IsSystemTab)
        {
            menu.Items.Add(Menu("更新", RefreshSpecialTab)); menu.Items.Add(new Separator()); menu.Items.Add(Menu("設定", _app.ShowSettings)); AddNewTabItems(menu);
        }
        else
        {
            menu.Items.Add(Menu("このランチャーを編集", () => _app.ShowEditor(tab.Id))); menu.Items.Add(Menu("このランチャーを複製", () => DuplicateTab(tab)));
            if (tab.IsWebTab) { menu.Items.Add(new Separator()); menu.Items.Add(Menu("ブックマークをインポート", () => ImportBookmarks(tab))); menu.Items.Add(Menu("ブックマークHTMLをエクスポート", () => ExportBookmarks(tab))); }
            menu.Items.Add(new Separator()); menu.Items.Add(Menu("名前を変更", () => RenameTab(tab))); menu.Items.Add(Menu("アイコンを変更", () => ChangeTabIcon(tab))); menu.Items.Add(Menu("アイコンを標準に戻す", () => Commit(d => d.Tabs.First(x => x.Id == tab.Id).Icon = null))); menu.Items.Add(Menu("非表示にする", () => Commit(d => d.Tabs.First(x => x.Id == tab.Id).IsVisible = false))); menu.Items.Add(Menu("削除", () => DeleteTab(tab))); menu.Items.Add(new Separator()); menu.Items.Add(Menu("設定", _app.ShowSettings)); AddNewTabItems(menu);
        }
        TabsList.ContextMenu = menu; if (keyboard) OpenContextMenu(menu, target); else menu.IsOpen = true;
    }
    private void TabsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragTabId = null;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.None || FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not LauncherTab tab) return;
        _tabDragStart = e.GetPosition(TabsList); _tabDragTabId = tab.Id;
    }
    private void TabsList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tabDragTabId is null) return;
        var point = e.GetPosition(TabsList); if (Math.Abs(point.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var id = _tabDragTabId; _tabDragTabId = null;
        DragDrop.DoDragDrop(TabsList, new System.Windows.DataObject(TabReorderDragFormat, id), System.Windows.DragDropEffects.Move);
    }
    private void TabsList_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TabReorderDragFormat) || e.Data.GetData(TabReorderDragFormat) is not string sourceId || FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not LauncherTab target || sourceId == target.Id) return;
        e.Effects = System.Windows.DragDropEffects.Move; e.Handled = true;
    }
    private void TabsList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TabReorderDragFormat) || e.Data.GetData(TabReorderDragFormat) is not string sourceId) return;
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject); if (container?.DataContext is not LauncherTab target || sourceId == target.Id) return;
        var after = e.GetPosition(container).Y > container.ActualHeight / 2; var targetId = target.Id;
        Commit(data => LauncherReorderRules.MoveTab(data, sourceId, targetId, after)); e.Handled = true;
    }
    private void AddNewTabItems(ContextMenu menu) { menu.Items.Add(Menu("アプリランチャーを新規登録", () => NewTab(LauncherTabKinds.Launcher))); menu.Items.Add(Menu("Webランチャーを新規登録", () => NewTab(LauncherTabKinds.Web))); }
    private void NewTab(string kind) { var title = kind == LauncherTabKinds.Web ? "Webランチャーの新規登録" : "アプリランチャーの新規登録"; var d = new TextPromptDialog(title, "名前") { Owner = this }; if (ShowDialog(d.ShowDialog) == true) Commit(data => { data.Tabs.Add(new LauncherTab { Name = d.Value, Kind = kind, Order = data.Tabs.Select(tab => tab.Order).DefaultIfEmpty(-1).Max() + 1 }); BuiltInTabs.Ensure(data); }); }
    private void DeleteTab(LauncherTab tab) { if (ShowDialog(() => MessageBox.Show($"App Launcher\n「{tab.Name}」を削除しますか？", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning)) == MessageBoxResult.Yes) Commit(d => { d.Tabs.Remove(d.Tabs.First(x => x.Id == tab.Id)); NormalizeTabOrders(d.Tabs); }); }
    private System.Windows.Controls.MenuItem Menu(string title, Action action) { var item = new System.Windows.Controls.MenuItem { Header = title }; item.Click += (_, _) => action(); return item; }
    private void RenameTab(LauncherTab tab) { var d = new TextPromptDialog("名前変更", "名前", tab.Name) { Owner = this }; if (ShowDialog(d.ShowDialog) == true) Commit(data => data.Tabs.First(x => x.Id == tab.Id).Name = d.Value); }
    private void DuplicateTab(LauncherTab tab) { if (!_app.TryDuplicateTab(tab.Id, out _, out var error)) ShowDialog(() => MessageBox.Show(error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); }
    private void ChangeTabIcon(LauncherTab tab)
    {
        var d = new OpenFileDialog { Title = "アイコンに使う画像", Filter = "画像|*.png;*.jpg;*.jpeg;*.bmp;*.ico" }; if (ShowDialog(d.ShowDialog) != true) return;
        try { var icon = _app.IconService.ImportImage(d.FileName, tab.Name); Commit(data => data.Tabs.First(x => x.Id == tab.Id).Icon = icon); } catch (Exception ex) { ShowDialog(() => MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); }
    }
    private void ImportBookmarks(LauncherTab tab)
    {
        var dialog = new OpenFileDialog { Title = "ブックマークHTMLをインポート", Filter = "ブックマークHTML|*.html;*.htm|すべてのファイル|*.*", CheckFileExists = true };
        if (ShowDialog(dialog.ShowDialog) != true) return;
        try
        {
            // Import は解析・URL検証の成功後にだけ一つの時刻Groupを追加する。
            var result = _app.WebBookmarkService.Import(dialog.FileName, tab.Children);
            if (result.Root is not null) Commit(data =>
            {
                var target = data.Tabs.First(value => value.Id == tab.Id);
                result.Root.Order = target.Children.Count; target.Children.Add(result.Root);
            });
            if (result.Root is not null) _app.QueueBookmarkIcons(tab.Id, result.IconCandidates);
            if (result.Skipped.Count == 0) { ShowDialog(() => MessageBox.Show($"{result.ImportedCount}件のブックマークを取り込みました。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Information)); return; }
            var detail = string.Join(Environment.NewLine, result.Skipped.Select(item => $"{item.Name}: {item.Url}"));
            var choice = ShowDialog(() => MessageBox.Show($"{result.ImportedCount}件のブックマークを取り込みました。\n{result.Skipped.Count}件はHTTP/HTTPS以外のURLのため取り込みませんでした。\n\nスキップしたURL一覧を表示しますか？", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Information));
            if (choice == MessageBoxResult.Yes) ShowDialog(() => new DiagnosticDialog("OpenGepa - 取り込みスキップ一覧", "HTTP/HTTPS以外のURLは取り込みませんでした。", detail) { Owner = this }.ShowDialog());
        }
        catch (Exception ex) { ShowDialog(() => MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); }
    }
    private void ExportBookmarks(LauncherTab tab)
    {
        var dialog = new SaveFileDialog { Title = "ブックマークHTMLをエクスポート", Filter = "ブックマークHTML|*.html", FileName = $"{tab.Name}_bookmarks.html", AddExtension = true, DefaultExt = ".html", OverwritePrompt = true };
        if (ShowDialog(dialog.ShowDialog) != true) return;
        try { _app.WebBookmarkService.Export(dialog.FileName, tab); }
        catch (Exception ex) { ShowDialog(() => MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error)); }
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
        var currentId = _app.SelectedTab?.Id; var current = currentId is null ? -1 : tabs.ToList().FindIndex(x => x.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase)); var target = tabs[LauncherReorderRules.CircularIndex(current, delta, tabs.Count)];
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
    private void ExpandAll()
    {
        ExpandAll(LauncherTree);
    }
    private static void ExpandAll(ItemsControl parent)
    {
        parent.UpdateLayout();
        foreach (var value in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(value) is not TreeViewItem item || item.DataContext is not GroupNode) continue;
            item.IsExpanded = true;
            item.UpdateLayout();
            ExpandAll(item);
        }
    }
    private void RestoreExpanded(IReadOnlySet<string> ids) { foreach (var item in EnumerateContainers(LauncherTree)) if (item.DataContext is GroupNode group) item.IsExpanded = ids.Contains(group.Id); }
    private static IEnumerable<TreeViewItem> EnumerateContainers(ItemsControl root) { foreach (var value in root.Items) if (root.ItemContainerGenerator.ContainerFromItem(value) is TreeViewItem item) { yield return item; foreach (var child in EnumerateContainers(item)) yield return child; } }
    private static IEnumerable<TreeViewItem> EnumerateVisibleContainers(ItemsControl root) { foreach (var value in root.Items) if (root.ItemContainerGenerator.ContainerFromItem(value) is TreeViewItem item) { yield return item; if (item.IsExpanded) foreach (var child in EnumerateVisibleContainers(item)) yield return child; } }
    private static LauncherNode? FindNode(IEnumerable<LauncherNode>? nodes, string id) { if (nodes is null) return null; foreach (var node in nodes) { if (node.Id == id) return node; if (node is GroupNode group) { var found = FindNode(group.Children, id); if (found is not null) return found; } } return null; }
    private static string? FindParentGroupId(IEnumerable<LauncherNode> nodes, string id) { foreach (var group in nodes.OfType<GroupNode>()) { if (group.Children.Any(node => node.Id == id)) return group.Id; var found = FindParentGroupId(group.Children, id); if (found is not null) return found; } return null; }
    private static ObservableCollection<LauncherNode>? FindSiblingCollection(ObservableCollection<LauncherNode> nodes, string id) { if (nodes.Any(node => node.Id == id)) return nodes; foreach (var group in nodes.OfType<GroupNode>()) { var found = FindSiblingCollection(group.Children, id); if (found is not null) return found; } return null; }
    private static bool RemoveNode(ObservableCollection<LauncherNode> nodes, string id) { var item = nodes.FirstOrDefault(x => x.Id == id); if (item is not null) return nodes.Remove(item); return nodes.OfType<GroupNode>().Any(group => RemoveNode(group.Children, id)); }
    private static void NormalizeOrders(ObservableCollection<LauncherNode> nodes) { for (var i = 0; i < nodes.Count; i++) { nodes[i].Order = i; if (nodes[i] is GroupNode group) NormalizeOrders(group.Children); } }
    private static void NormalizeTabOrders(ObservableCollection<LauncherTab> tabs) { var ordered = tabs.OrderBy(x => x.Order).ToList(); for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i; }
    private static T? FindAncestor<T>(DependencyObject? value) where T : DependencyObject { while (value is not null && value is not T) value = VisualTreeHelper.GetParent(value); return value as T; }
    private static TreeViewItem? FindContainer(ItemsControl root, object value) { if (root.ItemContainerGenerator.ContainerFromItem(value) is TreeViewItem direct) return direct; foreach (var item in root.Items) if (root.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem child) { var found = FindContainer(child, value); if (found is not null) return found; } return null; }
}
