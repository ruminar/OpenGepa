using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using OpenGepa.Models;
using OpenGepa.Services;

namespace OpenGepa;

public abstract class ThemedDialogWindow : Window
{
    protected ThemedDialogWindow()
    {
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        SetResourceReference(ForegroundProperty, "ItemForegroundBrush");
    }
}

public sealed class DiagnosticDialog : ThemedDialogWindow
{
    public DiagnosticDialog(string title, string summary, string details)
    {
        Title = title; Width = 760; Height = 600; MinWidth = 560; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var detailText = new System.Windows.Controls.TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var copy = new Button { Content = "詳細をコピー", Width = 110 }; copy.Click += (_, _) => System.Windows.Clipboard.SetText(details);
        var close = new Button { Content = "閉じる", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsDefault = true, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) }; buttons.Children.Add(copy); buttons.Children.Add(close);
        var grid = new Grid { Margin = new Thickness(16) }; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }); Grid.SetRow(detailText, 1); grid.Children.Add(detailText); Grid.SetRow(buttons, 2); grid.Children.Add(buttons); Content = grid;
    }
}

public sealed class TextPromptDialog : ThemedDialogWindow
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

public sealed class ItemDialog : ThemedDialogWindow
{
    private readonly System.Windows.Controls.TextBox _name = new(); private readonly System.Windows.Controls.TextBox _target = new(); private readonly System.Windows.Controls.ComboBox? _destination;
    public string ItemName => _name.Text; public string Target => _target.Text; public string? DestinationId => (_destination?.SelectedItem as DestinationOption)?.GroupId;
    public ItemDialog(string title, string name, string target, bool targetRequired, IReadOnlyList<DestinationOption>? destinations = null, string? selectedDestinationId = null, bool showName = true)
    {
        Title = title; Width = 560; Height = (targetRequired ? 230 : 170) + (destinations is null ? 0 : 65) - (showName ? 0 : 55); ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _name.Text = name; _target.Text = target;
        var panel = new StackPanel { Margin = new Thickness(16) };
        if (showName) { panel.Children.Add(new TextBlock { Text = "表示名" }); _name.Margin = new Thickness(0, 5, 0, 10); panel.Children.Add(_name); }
        if (targetRequired) { panel.Children.Add(new TextBlock { Text = "対象" }); _target.Margin = new Thickness(0, 5, 0, 12); panel.Children.Add(_target); }
        if (destinations is not null)
        {
            panel.Children.Add(new TextBlock { Text = "登録先" });
            _destination = new System.Windows.Controls.ComboBox { ItemsSource = destinations, DisplayMemberPath = nameof(DestinationOption.DisplayPath), Margin = new Thickness(0, 5, 0, 12) };
            _destination.SelectedItem = destinations.FirstOrDefault(x => string.Equals(x.GroupId, selectedDestinationId, StringComparison.OrdinalIgnoreCase)) ?? destinations.FirstOrDefault();
            panel.Children.Add(_destination);
        }
        var ok = new Button { Content = "OK", Width = 90, IsDefault = true }; ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "キャンセル", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
    }
}

public sealed record DestinationOption(string? GroupId, string DisplayPath);

public static class DestinationOptions
{
    public static IReadOnlyList<DestinationOption> Build(LauncherTab tab)
    {
        var result = new List<DestinationOption> { new(null, "root") };
        AddGroups(tab.Children, "root", result);
        return result;
    }

    private static void AddGroups(IEnumerable<LauncherNode> nodes, string parentPath, List<DestinationOption> result)
    {
        foreach (var group in nodes.OfType<GroupNode>().OrderBy(x => x.Order))
        {
            var path = $"{parentPath} / {group.Name}";
            result.Add(new DestinationOption(group.Id, path));
            AddGroups(group.Children, path, result);
        }
    }
}

public sealed class ScanCandidate : ObservableModel
{
    private bool _selected; private string _destinationPath = ""; private bool _hasConflict; private string? _conflictMessage;
    public string FullPath { get; init; } = "";
    public string DestinationPath { get => _destinationPath; set => SetField(ref _destinationPath, value); }
    public bool IsSelected { get => _selected; set => SetField(ref _selected, value); }
    public bool HasConflict { get => _hasConflict; set => SetField(ref _hasConflict, value); }
    public string? ConflictMessage { get => _conflictMessage; set => SetField(ref _conflictMessage, value); }
    public string? CachedIcon { get; set; }
}

public sealed record ScanValidationResult(string? Error, IReadOnlySet<string> ConflictPaths)
{
    public static ScanValidationResult Success { get; } = new(null, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

public static class DirectoryCandidateRules
{
    private static readonly string[] ScriptExtensions = [".bat", ".cmd", ".ps1"];
    public const string FileItemDialogFilter = "プログラムとショートカット|*.exe;*.lnk;*.bat;*.cmd;*.ps1|文書|*.pdf;*.txt;*.rtf;*.csv;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx|画像|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.ico|音声・動画|*.mp3;*.wav;*.flac;*.m4a;*.mp4;*.mkv;*.avi;*.wmv;*.mov";
    public static bool IsInitiallySelected(string path)
    {
        var extension = Path.GetExtension(path);
        if (ScriptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
        var pathSegments = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Any(segment => segment.Equals("x86", StringComparison.OrdinalIgnoreCase) || segment.Equals("x32", StringComparison.OrdinalIgnoreCase) || segment.Equals("win32", StringComparison.OrdinalIgnoreCase) || segment.Equals("32bit", StringComparison.OrdinalIgnoreCase) || segment.Equals("ia32", StringComparison.OrdinalIgnoreCase) || segment.Equals("i386", StringComparison.OrdinalIgnoreCase))) return false;
        var name = Path.GetFileNameWithoutExtension(path);
        var lower = name.ToLowerInvariant();
        if (lower is "setup" or "install" or "installer" or "uninstall" or "uninstaller" || lower.StartsWith("setup", StringComparison.Ordinal) || lower.StartsWith("unins", StringComparison.Ordinal) || lower.EndsWith("setup", StringComparison.Ordinal) || lower.EndsWith("installer", StringComparison.Ordinal)) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"(?:^|[^a-z0-9])(?:x86|x32|win32|32bit|ia32|i386)(?:[^a-z0-9]|$)") || System.Text.RegularExpressions.Regex.IsMatch(lower, @"32l?$")) return false;
        return true;
    }
    public static string DefaultDisplayName(string path) => Path.GetFileName(path);
}

public static class DirectoryScanRootRules
{
    public static string GetRootGroupName(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return (Path.GetPathRoot(root) ?? "root").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static ObservableCollection<LauncherNode> GetOrCreateRootGroup(ObservableCollection<LauncherNode> destination, string root)
    {
        var name = GetRootGroupName(root);
        var normalized = NameRules.Normalize(name);
        var existing = destination.FirstOrDefault(x => string.Equals(NameRules.Normalize(DataValidator.NodeLabel(x)), normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is GroupNode group) return group.Children;
        if (existing is not null) throw new InvalidDataException($"登録先には「{name}」というGroup以外の項目が存在します。");
        var created = new GroupNode { Name = name, Order = destination.Count };
        destination.Add(created);
        return created.Children;
    }
}

public sealed class OperationProgressDialog : ThemedDialogWindow
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TextBlock _status = new();
    private readonly System.Windows.Controls.ProgressBar _progress = new();
    private readonly Button _cancel = new();
    private bool _completed;
    public CancellationToken Token => _cancellation.Token;
    public OperationProgressDialog(string title, string initialStatus, bool indeterminate)
    {
        Title = title; Width = 460; Height = 170; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _status.Text = initialStatus; _status.Margin = new Thickness(0, 0, 0, 12); _status.TextWrapping = TextWrapping.Wrap;
        _progress.IsIndeterminate = indeterminate; _progress.Height = 10; _progress.Minimum = 0; _progress.Maximum = 1; _progress.Margin = new Thickness(0, 0, 0, 12);
        _cancel.Content = "キャンセル"; _cancel.Width = 100; _cancel.HorizontalAlignment = HorizontalAlignment.Right; _cancel.Click += (_, _) => { _cancellation.Cancel(); _cancel.IsEnabled = false; _status.Text = "キャンセルしています…"; };
        var panel = new StackPanel { Margin = new Thickness(16) }; panel.Children.Add(_status); panel.Children.Add(_progress); panel.Children.Add(_cancel); Content = panel;
    }
    public void Report(string status, int current, int total)
    {
        Dispatcher.Invoke(() => { _status.Text = status; _progress.IsIndeterminate = total <= 0; if (total > 0) { _progress.Maximum = total; _progress.Value = Math.Clamp(current, 0, total); } });
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { if (!_completed) _cancellation.Cancel(); base.OnClosing(e); }
    public void Complete() { _completed = true; if (IsVisible) Close(); }
}

public sealed class ScanPreviewDialog : ThemedDialogWindow
{
    private readonly System.Windows.Controls.DataGrid _grid = new();
    public IReadOnlyList<ScanCandidate> Selected => ((ObservableCollection<ScanCandidate>)_grid.ItemsSource).Where(x => x.IsSelected).ToList();
    public ScanPreviewDialog(string root, IEnumerable<string> files, int skipped, Func<IReadOnlyList<ScanCandidate>, ScanValidationResult> validate)
    {
        Title = "ディレクトリから一括登録"; Width = 760; Height = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var candidates = new ObservableCollection<ScanCandidate>(files.Select(f => new ScanCandidate { FullPath = f, DestinationPath = Path.GetRelativePath(root, f), IsSelected = DirectoryCandidateRules.IsInitiallySelected(f) }));
        var rowStyle = new Style(typeof(DataGridRow)); rowStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(ScanCandidate.ConflictMessage))));
        var conflictTrigger = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(ScanCandidate.HasConflict)), Value = true }; conflictTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 243, 176)))); rowStyle.Triggers.Add(conflictTrigger); _grid.RowStyle = rowStyle;
        _grid.ItemsSource = candidates; _grid.AutoGenerateColumns = false; _grid.CanUserAddRows = false; _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "登録", Binding = new System.Windows.Data.Binding(nameof(ScanCandidate.IsSelected)) }); _grid.Columns.Add(new DataGridTextColumn { Header = "登録先相対パス（編集可）", Binding = new System.Windows.Data.Binding(nameof(ScanCandidate.DestinationPath)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var ok = new Button { Content = "選択項目を登録", Width = 140, IsDefault = true }; ok.Click += (_, _) => { _grid.CommitEdit(DataGridEditingUnit.Row, true); var selected = Selected; foreach (var candidate in candidates) { candidate.HasConflict = false; candidate.ConflictMessage = null; } if (selected.Count == 0) { MessageBox.Show("登録する候補を選択してください。", "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Information); return; } var result = validate(selected); foreach (var candidate in candidates.Where(x => result.ConflictPaths.Contains(x.FullPath))) { candidate.HasConflict = true; candidate.ConflictMessage = result.Error; } if (result.Error is not null) { MessageBox.Show("登録先の競合を解決してください。\n黄色の行が競合中の候補です。\n\n" + result.Error, "OpenGepa", MessageBoxButton.OK, MessageBoxImage.Warning); return; } DialogResult = true; };
        var cancel = new Button { Content = "キャンセル", Width = 100, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) }; buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var grid = new Grid { Margin = new Thickness(12) }; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var info = new TextBlock { Text = $"候補 {candidates.Count}件（読み飛ばし {skipped}件）", Margin = new Thickness(0, 0, 0, 8) }; grid.Children.Add(info); Grid.SetRow(_grid, 1); grid.Children.Add(_grid); Grid.SetRow(buttons, 2); grid.Children.Add(buttons); Content = grid;
    }
}
