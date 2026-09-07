using OpenGepa.Models;

namespace OpenGepa.Services;

public static class LauncherTabDeletionRules
{
    public static bool CanDelete(LauncherTab tab) => !tab.IsSystemTab;

    public static bool TryDelete(OpenGepaData data, string tabId)
    {
        var tab = data.Tabs.FirstOrDefault(item => item.Id == tabId);
        if (tab is null || !CanDelete(tab)) return false;

        data.Tabs.Remove(tab);
        foreach (var (item, index) in data.Tabs.OrderBy(item => item.Order).Select((item, index) => (item, index))) item.Order = index;
        if (data.SelectedTabId == tabId) data.SelectedTabId = data.Tabs.Where(item => item.IsVisible).OrderBy(item => item.Order).FirstOrDefault()?.Id;
        return true;
    }

    public static string ConfirmationMessage(LauncherTab tab) => $"「{tab.Name}」を削除しますか？\n\n【警告】\nこのランチャータブに含まれるすべてのGroup、項目、区切り線、およびタブ設定が削除されます。\nこの操作は元に戻せません。";
}
