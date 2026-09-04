using System.Collections.ObjectModel;
using OpenGepa.Models;

namespace OpenGepa;

/// <summary>同じ階層内の項目と縦タブだけを安全に並び替えます。</summary>
public static class LauncherReorderRules
{
    public static int CircularIndex(int current, int delta, int count)
    {
        if (count <= 0) return -1;
        if (current < 0) return delta > 0 ? 0 : count - 1;
        return ((current + delta) % count + count) % count;
    }

    public static bool MoveSibling(ObservableCollection<LauncherNode> nodes, string sourceId, string targetId, bool after)
    {
        var source = nodes.FirstOrDefault(node => node.Id == sourceId); var target = nodes.FirstOrDefault(node => node.Id == targetId);
        if (source is null || target is null || ReferenceEquals(source, target)) return false;
        nodes.Remove(source); var index = nodes.IndexOf(target); if (after) index++; nodes.Insert(index, source); Normalize(nodes); return true;
    }
    public static bool MoveTab(OpenGepaData data, string sourceId, string targetId, bool after)
    {
        var ordered = data.Tabs.OrderBy(tab => tab.Order).ToList(); var source = ordered.FirstOrDefault(tab => tab.Id == sourceId); var target = ordered.FirstOrDefault(tab => tab.Id == targetId);
        if (source is null || target is null || ReferenceEquals(source, target)) return false;
        ordered.Remove(source); var index = ordered.IndexOf(target); if (after) index++; ordered.Insert(index, source);
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        return true;
    }
    private static void Normalize(ObservableCollection<LauncherNode> nodes)
    {
        for (var i = 0; i < nodes.Count; i++) { nodes[i].Order = i; if (nodes[i] is GroupNode group) Normalize(group.Children); }
    }
}
