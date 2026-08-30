using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using OpenGepa.Models;
using OpenGepa.Services;

namespace OpenGepa;

public partial class MainWindow : Window
{
    private readonly AppService _app; private bool _refreshing; private bool _launching;
    public MainWindow(AppService app) { InitializeComponent(); _app = app; _app.DataChanged += (_, _) => Dispatcher.Invoke(RefreshData); }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); }
    private void Window_StateChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; Hide(); } }
    public void RefreshData()
    {
        _refreshing = true; var visible = _app.VisibleTabs; TabsList.ItemsSource = visible;
        TabsList.SelectedItem = _app.SelectedTab; LauncherTree.ItemsSource = _app.SelectedTab?.Children;
        EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed; _refreshing = false;
    }
    public void PositionNearCursor()
    {
        var point = System.Windows.Forms.Cursor.Position; var screen = System.Windows.Forms.Screen.FromPoint(point); var area = screen.WorkingArea;
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).EnsureHandle());
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var cursor = transform.Transform(new System.Windows.Point(point.X, point.Y));
        var topLeft = transform.Transform(new System.Windows.Point(area.Left, area.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(area.Right, area.Bottom));
        Left = Math.Max(topLeft.X, Math.Min(cursor.X - Width, bottomRight.X - Width)); Top = Math.Max(topLeft.Y, Math.Min(cursor.Y - Height, bottomRight.Y - Height));
    }
    private void Window_Deactivated(object sender, EventArgs e) { if (IsVisible) Hide(); }
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } }
    private void TabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (_refreshing || TabsList.SelectedItem is not LauncherTab tab) return; _app.SelectTab(tab.Id); LauncherTree.ItemsSource = tab.Children; }
    private async void LauncherTree_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>((DependencyObject)e.OriginalSource); if (item?.DataContext is GroupNode) { if (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>((DependencyObject)e.OriginalSource) is null) item.IsExpanded = !item.IsExpanded; e.Handled = true; return; }
        if (item?.DataContext is LauncherItem launcher) { e.Handled = true; await Launch(launcher); }
    }
    private async void LauncherTree_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (LauncherTree.SelectedItem is GroupNode && (e.Key == Key.Enter || e.Key == Key.Space)) { var item = FindContainer(LauncherTree, LauncherTree.SelectedItem); if (item is not null) item.IsExpanded = !item.IsExpanded; e.Handled = true; }
        else if (LauncherTree.SelectedItem is LauncherItem item && e.Key == Key.Enter) { e.Handled = true; await Launch(item); }
    }
    private async Task Launch(LauncherItem item)
    { if (_launching) return; _launching = true; try { var result = await _app.LaunchService.LaunchAsync(item); if (result.Success) Hide(); else if (MessageBox.Show($"「{item.Name}」を起動できませんでした。\n\n{result.Error}\n\n編集画面を開きますか？", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Error) == MessageBoxResult.Yes) _app.ShowEditor(_app.SelectedTab?.Id); } finally { _launching = false; } }
    private void TabsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource); if (item?.DataContext is not LauncherTab tab) return;
        TabsList.SelectedItem = tab; var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(Menu("このランチャーを編集", () => _app.ShowEditor(tab.Id)));
        menu.Items.Add(new System.Windows.Controls.Separator()); menu.Items.Add(Menu("名前を変更", () => RenameTab(tab)));
        menu.Items.Add(Menu("非表示にする", () => Commit(d => d.Tabs.First(x => x.Id == tab.Id).IsVisible = false)));
        menu.Items.Add(Menu("削除", () => { if (MessageBox.Show($"「{tab.Name}」を削除しますか？", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) Commit(d => d.Tabs.Remove(d.Tabs.First(x => x.Id == tab.Id))); }));
        item.ContextMenu = menu; menu.IsOpen = true; e.Handled = true;
    }
    private System.Windows.Controls.MenuItem Menu(string title, Action action) { var m = new System.Windows.Controls.MenuItem { Header = title }; m.Click += (_, _) => action(); return m; }
    private void RenameTab(LauncherTab tab) { var dialog = new TextPromptDialog("名前変更", "名前", tab.Name) { Owner = this }; if (dialog.ShowDialog() == true) Commit(data => data.Tabs.First(x => x.Id == tab.Id).Name = dialog.Value); }
    private void Commit(Action<OpenGepaData> change) { if (!_app.TryCommit(change, out var error)) MessageBox.Show(error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); }
    private static T? FindAncestor<T>(DependencyObject? value) where T : DependencyObject { while (value is not null && value is not T) value = VisualTreeHelper.GetParent(value); return value as T; }
    private static TreeViewItem? FindContainer(ItemsControl root, object value)
    { if (root.ItemContainerGenerator.ContainerFromItem(value) is TreeViewItem direct) return direct; foreach (var item in root.Items) if (root.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem child) { var found = FindContainer(child, value); if (found is not null) return found; } return null; }
}
