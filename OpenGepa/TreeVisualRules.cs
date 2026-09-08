using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace OpenGepa;

public static class TreeVisualRules
{
    public static DependencyObject? ParentOf(DependencyObject value) => value switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(value),
        ContentElement content => ContentOperations.GetParent(content) ?? LogicalTreeHelper.GetParent(content),
        _ => LogicalTreeHelper.GetParent(value)
    };

    public static T? FindAncestor<T>(DependencyObject? value) where T : DependencyObject
    {
        while (value is not null && value is not T) value = ParentOf(value);
        return value as T;
    }
}
