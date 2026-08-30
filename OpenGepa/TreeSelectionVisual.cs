using System.Windows;

namespace OpenGepa;

public static class TreeSelectionVisual
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected", typeof(bool), typeof(TreeSelectionVisual), new FrameworkPropertyMetadata(false));

    public static bool GetIsSelected(DependencyObject value) => (bool)value.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject value, bool selected) => value.SetValue(IsSelectedProperty, selected);
}

public sealed record TreeSelectionUpdate(IReadOnlySet<string> Selected, string? AnchorId, string? PrimaryId);

public static class TreeSelectionLogic
{
    public static TreeSelectionUpdate Apply(IEnumerable<string> current, IReadOnlyList<string> visible, string? anchorId, string clickedId, bool extendRange, bool toggle)
    {
        var selected = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (extendRange && anchorId is not null)
        {
            var anchorIndex = IndexOf(visible, anchorId); var clickedIndex = IndexOf(visible, clickedId);
            if (!toggle) selected.Clear();
            if (anchorIndex >= 0 && clickedIndex >= 0) for (var i = Math.Min(anchorIndex, clickedIndex); i <= Math.Max(anchorIndex, clickedIndex); i++) selected.Add(visible[i]);
            else selected.Add(clickedId);
            return new TreeSelectionUpdate(selected, anchorId, clickedId);
        }
        if (toggle)
        {
            if (!selected.Add(clickedId)) selected.Remove(clickedId);
            return new TreeSelectionUpdate(selected, clickedId, selected.Contains(clickedId) ? clickedId : selected.LastOrDefault());
        }
        return new TreeSelectionUpdate(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { clickedId }, clickedId, clickedId);
    }

    private static int IndexOf(IReadOnlyList<string> values, string target)
    { for (var i = 0; i < values.Count; i++) if (values[i].Equals(target, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
}
