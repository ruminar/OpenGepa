using System.Windows;
using Microsoft.Win32;
using OpenGepa.Models;
using OpenGepa.Services;

namespace OpenGepa;

public sealed class PresetVisibilityRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsVisible { get; set; }
}

public partial class SettingsWindow : Window
{
    private readonly AppService _app; private bool _refreshing;
    public SettingsWindow(AppService app) { InitializeComponent(); _app = app; }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { if (!App.IsExiting) { e.Cancel = true; Hide(); } }
    private void Window_StateChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; Hide(); } }
    public void RefreshData(string? selectedTabId = null, bool restoreFocus = false)
    {
        Icon = WindowIconService.Load(_app);
        selectedTabId ??= (TabsList.SelectedItem as LauncherTab)?.Id;
        _refreshing = true; StartupCheck.IsChecked = _app.StartupService.IsEnabled; var tabs = _app.Data.Tabs.OrderBy(t => t.Order).ToList(); TabsList.ItemsSource = tabs;
        TabsList.SelectedItem = tabs.FirstOrDefault(tab => tab.Id == selectedTabId);
        var appearance = _app.Data.Appearance; ThemeCombo.SelectedValue = appearance.Theme; GroupBackgroundText.Text = appearance.GroupBackgroundColor; GroupForegroundText.Text = appearance.GroupForegroundColor; ItemBackgroundText.Text = appearance.LauncherItemBackgroundColor; ItemForegroundText.Text = appearance.LauncherItemForegroundColor; var custom = appearance.Theme == "custom"; GroupBackgroundText.IsEnabled = custom; GroupForegroundText.IsEnabled = custom; ItemBackgroundText.IsEnabled = custom; ItemForegroundText.IsEnabled = custom; CustomAppearancePanel.IsEnabled = custom;
        GroupIconPath.Text = _app.IconSetService.GetDefaultIcon("group") ?? _app.Data.DefaultIcons.GroupIcon ?? "標準";
        DirectoryIconPath.Text = _app.IconSetService.GetDefaultIcon("directory") ?? _app.Data.DefaultIcons.DirectoryIcon ?? "標準";
        UrlIconPath.Text = _app.IconSetService.GetDefaultIcon("url") ?? _app.Data.DefaultIcons.UrlIcon ?? "標準";
        TrayIconPath.Text = _app.IconSetService.GetOpenGepaIcon() ?? _app.Data.DefaultIcons.TrayIcon ?? "アプリ標準";
        GroupIconDelete.IsEnabled = _app.IconSetService.HasDefaultIcon("group"); DirectoryIconDelete.IsEnabled = _app.IconSetService.HasDefaultIcon("directory"); UrlIconDelete.IsEnabled = _app.IconSetService.HasDefaultIcon("url"); TrayIconDelete.IsEnabled = _app.IconSetService.HasOpenGepaIcon;
        FileItemClickCombo.SelectedValue = _app.Data.ItemLaunch.FileItemClickCount.ToString(); DirectoryItemClickCombo.SelectedValue = _app.Data.ItemLaunch.DirectoryItemClickCount.ToString(); UrlItemClickCombo.SelectedValue = _app.Data.ItemLaunch.UrlItemClickCount.ToString();
        WindowsMenuCurrentEditCheck.IsChecked = _app.Data.WindowsMenu.AllowCurrentUserEdit; WindowsMenuAllUsersEditCheck.IsChecked = _app.Data.WindowsMenu.AllowAllUsersEdit;
        FoldersFirstRadio.IsChecked = _app.Data.WindowsMenu.FoldersFirst; ShortcutsFirstRadio.IsChecked = !_app.Data.WindowsMenu.FoldersFirst;
        PresetItemsList.ItemsSource = _app.PresetService.AvailableDefinitions().Select(item => new PresetVisibilityRow { Id = item.Id, Name = $"{item.Group.Replace("/", " / ")} / {item.Name}", IsVisible = !_app.Data.Presets.HiddenItemIds.Contains(item.Id) }).ToList();
        _refreshing = false; UpdateMoveButtons();
        if (restoreFocus && TabsList.SelectedItem is LauncherTab selected)
            Dispatcher.BeginInvoke(() => { if (TabsList.ItemContainerGenerator.ContainerFromItem(selected) is System.Windows.Controls.ListBoxItem item) item.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
    }
    private void StartupCheck_Changed(object sender, RoutedEventArgs e) { if (_refreshing) return; try { _app.StartupService.SetEnabled(StartupCheck.IsChecked == true); } catch (Exception ex) { MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); RefreshData(); } }
    private void ThemeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshing || ThemeCombo.SelectedValue is not string theme) return;
        Commit(data => data.Appearance.Theme = theme);
    }
    private void ApplyAppearance_Click(object sender, RoutedEventArgs e)
    {
        Commit(data =>
        {
            data.Appearance.GroupBackgroundColor = GroupBackgroundText.Text; data.Appearance.GroupForegroundColor = GroupForegroundText.Text;
            data.Appearance.LauncherItemBackgroundColor = ItemBackgroundText.Text; data.Appearance.LauncherItemForegroundColor = ItemForegroundText.Text;
        });
    }
    private void ItemClickCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshing || sender is not System.Windows.Controls.ComboBox combo || !int.TryParse(combo.SelectedValue?.ToString(), out var count)) return;
        Commit(data => { if (combo == FileItemClickCombo) data.ItemLaunch.FileItemClickCount = count; else if (combo == DirectoryItemClickCombo) data.ItemLaunch.DirectoryItemClickCount = count; else data.ItemLaunch.UrlItemClickCount = count; });
    }
    private void DefaultIcon_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string kind) return;
        var dialog = new OpenFileDialog { Title = "既定アイコン", Filter = "画像|*.png;*.jpg;*.jpeg;*.bmp;*.ico" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (kind == "tray")
            {
                if (_app.IconSetService.HasOpenGepaIcon && MessageBox.Show("iconSet/OpenGepa.ico は既にあります。置き換えますか？", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                _app.IconSetService.SetOpenGepaIcon(dialog.FileName); Commit(data => data.DefaultIcons.TrayIcon = null); return;
            }
            if (_app.IconSetService.HasDefaultIcon(kind) && MessageBox.Show($"iconSet/{kind}_default.png は既にあります。置き換えますか？", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _app.IconSetService.SetDefaultIcon(kind, dialog.FileName);
            Commit(data => { if (kind == "group") data.DefaultIcons.GroupIcon = null; else if (kind == "directory") data.DefaultIcons.DirectoryIcon = null; else if (kind == "url") data.DefaultIcons.UrlIcon = null; });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void DefaultIcon_Delete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string kind) return;
        var relative = kind == "tray" ? "iconSet/OpenGepa.ico" : $"iconSet/{kind}_default.png";
        if (MessageBox.Show($"既存のアイコンファイル「{relative}」を削除し、標準アイコンへ戻しますか？", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            if (kind == "tray") { _app.IconSetService.DeleteOpenGepaIcon(); Commit(data => data.DefaultIcons.TrayIcon = null); }
            else { _app.IconSetService.DeleteDefaultIcon(kind); Commit(data => { if (kind == "group") data.DefaultIcons.GroupIcon = null; else if (kind == "directory") data.DefaultIcons.DirectoryIcon = null; else if (kind == "url") data.DefaultIcons.UrlIcon = null; }); }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void Visibility_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LauncherTab tab) return; var visible = ((System.Windows.Controls.CheckBox)sender).IsChecked == true;
        Commit(data =>
        {
            data.Tabs.First(t => t.Id == tab.Id).IsVisible = visible;
            if (!visible && data.SelectedTabId == tab.Id) data.SelectedTabId = data.Tabs.Where(t => t.IsVisible).OrderBy(t => t.Order).FirstOrDefault()?.Id;
        });
    }
    private void WindowsMenuEdit_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        Commit(data => { data.WindowsMenu.AllowCurrentUserEdit = WindowsMenuCurrentEditCheck.IsChecked == true; data.WindowsMenu.AllowAllUsersEdit = WindowsMenuAllUsersEditCheck.IsChecked == true; });
    }
    private void WindowsMenuSort_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        Commit(data => data.WindowsMenu.FoldersFirst = FoldersFirstRadio.IsChecked == true);
    }
    private void PresetVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing || (sender as FrameworkElement)?.DataContext is not PresetVisibilityRow row) return;
        var visible = ((System.Windows.Controls.CheckBox)sender).IsChecked == true;
        Commit(data => { if (visible) data.Presets.HiddenItemIds.Remove(row.Id); else data.Presets.HiddenItemIds.Add(row.Id); });
    }
    private void RestorePresets_Click(object sender, RoutedEventArgs e) => Commit(data => data.Presets.HiddenItemIds.Clear());
    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int delta) { if (TabsList.SelectedItem is not LauncherTab tab) return; var ordered = _app.Data.Tabs.OrderBy(x => x.Order).ToList(); var i = ordered.FindIndex(x => x.Id == tab.Id); var j = i + delta; if (j < 0 || j >= ordered.Count) return; var other = ordered[j]; Commit(data => { var a = data.Tabs.First(x => x.Id == tab.Id); var b = data.Tabs.First(x => x.Id == other.Id); (a.Order, b.Order) = (b.Order, a.Order); }, tab.Id, true); }
    private void TabsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (!_refreshing) UpdateMoveButtons(); }
    private void UpdateMoveButtons()
    {
        var ordered = _app.Data.Tabs.OrderBy(tab => tab.Order).ToList(); var selected = TabsList.SelectedItem as LauncherTab; var index = selected is null ? -1 : ordered.FindIndex(tab => tab.Id == selected.Id);
        UpButton.IsEnabled = index > 0; DownButton.IsEnabled = index >= 0 && index < ordered.Count - 1;
    }
    private void Save_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "OpenGepa Profile|*.ogp", FileName = $"OpenGepaProfile_{DateTime.Now:yyyyMMdd_HHmmssff}.ogp" }; if (d.ShowDialog(this) == true) try { _app.ProfileService.Save(d.FileName); MessageBox.Show("Profileを保存しました。", "OpenGepa"); } catch (Exception ex) { MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private void Load_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "OpenGepa Profile|*.ogp" }; if (d.ShowDialog(this) != true || MessageBox.Show("現在の設定をProfileで置き換えますか？\n\nProfileには実行ファイル、スクリプト、ショートカットへの参照が含まれることがあります。信頼できるProfileだけを読み込んでください。", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return; try { _app.ReplaceData(_app.ProfileService.Load(d.FileName)); RefreshData(); } catch (Exception ex) { MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private void Ok_Click(object sender, RoutedEventArgs e) => Hide();
    private void Commit(Action<OpenGepaData> action, string? selectedTabId = null, bool restoreFocus = false) { if (!_app.TryCommit(action, out var error)) MessageBox.Show(error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); RefreshData(selectedTabId, restoreFocus); }
}
