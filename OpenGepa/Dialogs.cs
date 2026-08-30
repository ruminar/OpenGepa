using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using OpenGepa.Models;

namespace OpenGepa;

public sealed class TextPromptDialog : Window
{
    private readonly System.Windows.Controls.TextBox _text = new();
    public string Value => _text.Text;
    public TextPromptDialog(string title, string label, string initial = "")
    {
        Title = title; Width = 420; Height = 160; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _text.Text = initial; _text.Margin = new Thickness(0, 6, 0, 12); _text.SelectAll();
        var ok = new Button { Content = "OK", Width = 90, IsDefault = true }; ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "キャンセル", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) }; panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(_text); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => _text.Focus();
    }
}

public sealed class ItemDialog : Window
{
    private readonly System.Windows.Controls.TextBox _name = new(); private readonly System.Windows.Controls.TextBox _target = new();
    public string ItemName => _name.Text; public string Target => _target.Text;
    public ItemDialog(string title, string name, string target, bool targetRequired)
    {
        Title = title; Width = 560; Height = targetRequired ? 230 : 170; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _name.Text = name; _target.Text = target;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "表示名" }); _name.Margin = new Thickness(0, 5, 0, 10); panel.Children.Add(_name);
        if (targetRequired) { panel.Children.Add(new TextBlock { Text = "対象" }); _target.Margin = new Thickness(0, 5, 0, 12); panel.Children.Add(_target); }
        var ok = new Button { Content = "OK", Width = 90, IsDefault = true }; ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "キャンセル", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
    }
}

public sealed class ScanCandidate : ObservableModel
{
    private bool _selected; private string _destinationPath = "";
    public string FullPath { get; init; } = "";
    public string DestinationPath { get => _destinationPath; set => SetField(ref _destinationPath, value); }
    public bool IsSelected { get => _selected; set => SetField(ref _selected, value); }
    public string? CachedIcon { get; set; }
}

public sealed class ScanProgressDialog : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    public CancellationToken Token => _cancellation.Token;
    public ScanProgressDialog()
    {
        Title = "ディレクトリ走査中"; Width = 420; Height = 150; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new Button { Content = "キャンセル", Width = 100, HorizontalAlignment = HorizontalAlignment.Right }; cancel.Click += (_, _) => _cancellation.Cancel();
        var panel = new StackPanel { Margin = new Thickness(16) }; panel.Children.Add(new TextBlock { Text = "候補を走査しています…", Margin = new Thickness(0, 0, 0, 12) }); panel.Children.Add(new System.Windows.Controls.ProgressBar { IsIndeterminate = true, Height = 8, Margin = new Thickness(0, 0, 0, 12) }); panel.Children.Add(cancel); Content = panel;
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { if (IsVisible) _cancellation.Cancel(); base.OnClosing(e); }
    public void Complete() { if (IsVisible) Close(); }
}

public sealed class ScanPreviewDialog : Window
{
    private readonly System.Windows.Controls.DataGrid _grid = new();
    public IReadOnlyList<ScanCandidate> Selected => ((ObservableCollection<ScanCandidate>)_grid.ItemsSource).Where(x => x.IsSelected).ToList();
    public ScanPreviewDialog(string root, IEnumerable<string> files, int skipped, Func<IReadOnlyList<ScanCandidate>, string?> validate)
    {
        Title = "ディレクトリから一括登録"; Width = 760; Height = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var candidates = new ObservableCollection<ScanCandidate>(files.Select(f => new ScanCandidate { FullPath = f, DestinationPath = Path.GetRelativePath(root, f), IsSelected = Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(f).Equals(".lnk", StringComparison.OrdinalIgnoreCase) }));
        _grid.ItemsSource = candidates; _grid.AutoGenerateColumns = false; _grid.CanUserAddRows = false; _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "登録", Binding = new System.Windows.Data.Binding(nameof(ScanCandidate.IsSelected)) }); _grid.Columns.Add(new DataGridTextColumn { Header = "登録先相対パス（編集可）", Binding = new System.Windows.Data.Binding(nameof(ScanCandidate.DestinationPath)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var ok = new Button { Content = "選択項目を登録", Width = 140, IsDefault = true }; ok.Click += (_, _) => { _grid.CommitEdit(); var selected = Selected; if (selected.Count == 0) { MessageBox.Show("登録する候補を選択してください。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Information); return; } var error = validate(selected); if (error is not null) { MessageBox.Show("登録先の競合を解決してください。\n\n" + error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Warning); return; } DialogResult = true; };
        var cancel = new Button { Content = "キャンセル", Width = 100, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) }; buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var grid = new Grid { Margin = new Thickness(12) }; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var info = new TextBlock { Text = $"候補 {candidates.Count}件（読み飛ばし {skipped}件）", Margin = new Thickness(0, 0, 0, 8) }; grid.Children.Add(info); Grid.SetRow(_grid, 1); grid.Children.Add(_grid); Grid.SetRow(buttons, 2); grid.Children.Add(buttons); Content = grid;
    }
}
