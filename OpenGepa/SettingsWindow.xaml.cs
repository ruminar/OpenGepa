using System.Windows;
using Microsoft.Win32;
using OpenGepa.Models;
using OpenGepa.Services;

namespace OpenGepa;

public partial class SettingsWindow : Window
{
    private readonly AppService _app; private bool _refreshing;
    public SettingsWindow(AppService app) { InitializeComponent(); _app = app; }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); }
    private void Window_StateChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; Hide(); } }
    public void RefreshData() { _refreshing = true; StartupCheck.IsChecked = _app.StartupService.IsEnabled; TabsList.ItemsSource = _app.Data.Tabs.OrderBy(t => t.Order).ToList(); _refreshing = false; }
    private void StartupCheck_Changed(object sender, RoutedEventArgs e) { if (_refreshing) return; try { _app.StartupService.SetEnabled(StartupCheck.IsChecked == true); } catch (Exception ex) { MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); RefreshData(); } }
    private void Visibility_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LauncherTab tab) return; var visible = ((System.Windows.Controls.CheckBox)sender).IsChecked == true;
        Commit(data =>
        {
            data.Tabs.First(t => t.Id == tab.Id).IsVisible = visible;
            if (!visible && data.SelectedTabId == tab.Id) data.SelectedTabId = data.Tabs.Where(t => t.IsVisible).OrderBy(t => t.Order).FirstOrDefault()?.Id;
        });
    }
    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int delta) { if (TabsList.SelectedItem is not LauncherTab tab) return; var ordered = _app.Data.Tabs.OrderBy(x => x.Order).ToList(); var i = ordered.FindIndex(x => x.Id == tab.Id); var j = i + delta; if (j < 0 || j >= ordered.Count) return; var other = ordered[j]; Commit(data => { var a = data.Tabs.First(x => x.Id == tab.Id); var b = data.Tabs.First(x => x.Id == other.Id); (a.Order, b.Order) = (b.Order, a.Order); }); }
    private void Save_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "OpenGepa Profile|*.ogp", FileName = $"OpenGepaProfile_{DateTime.Now:yyyyMMdd_HHmmssff}.ogp" }; if (d.ShowDialog(this) == true) try { _app.ProfileService.Save(d.FileName); MessageBox.Show("Profileを保存しました。", "OpenGepa"); } catch (Exception ex) { MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private void Load_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "OpenGepa Profile|*.ogp" }; if (d.ShowDialog(this) != true || MessageBox.Show("現在の設定をProfileで置き換えますか？\n\nProfileには実行ファイル、スクリプト、ショートカットへの参照が含まれることがあります。信頼できるProfileだけを読み込んでください。", "OpenGepa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return; try { _app.ReplaceData(_app.ProfileService.Load(d.FileName)); RefreshData(); } catch (Exception ex) { MessageBox.Show(ex.Message, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private void Commit(Action<OpenGepaData> action) { if (!_app.TryCommit(action, out var error)) MessageBox.Show(error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Error); RefreshData(); }
}
