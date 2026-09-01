using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace OpenGepa;

public sealed class TreeDropInsertionAdorner : Adorner
{
    private readonly bool _after;
    public TreeDropInsertionAdorner(UIElement adornedElement, bool after) : base(adornedElement) => _after = after;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var y = _after ? AdornedElement.RenderSize.Height - 1 : 1;
        drawingContext.DrawLine(new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, 2), new System.Windows.Point(0, y), new System.Windows.Point(AdornedElement.RenderSize.Width, y));
    }
}
