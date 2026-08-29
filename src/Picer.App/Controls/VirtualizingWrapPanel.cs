using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Picer.Core.Diagnostics;

namespace Picer.App.Controls;

public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(180d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(220d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _itemsPerRow = 1;
    private bool _generatorRetryScheduled;

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        var count = owner?.Items.Count ?? 0;
        var width = double.IsInfinity(availableSize.Width) ? ItemWidth : Math.Max(ItemWidth, availableSize.Width);
        _itemsPerRow = Math.Max(1, (int)Math.Floor(width / ItemWidth));
        _viewport = new Size(width, double.IsInfinity(availableSize.Height) ? ItemHeight : availableSize.Height);
        _extent = new Size(width, Math.Ceiling((double)count / _itemsPerRow) * ItemHeight);
        ScrollOwner?.InvalidateScrollInfo();

        var range = VirtualizationMath.VisibleRange(count, width, _viewport.Height, _offset.Y, ItemWidth, ItemHeight);
        RealizeRange(range.FirstIndex, range.LastIndex, availableSize);
        return availableSize;
    }

    private void RealizeRange(int firstIndex, int lastIndex, Size availableSize)
    {
        var generator = ItemContainerGenerator;
        // WPF can measure an inactive/recycled tab before its item generator is
        // attached.  Deferring realization is safe; a subsequent measure will
        // run as soon as the ItemsControl becomes active.
        if (generator is null)
        {
            if (!_generatorRetryScheduled)
            {
                _generatorRetryScheduled = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    _generatorRetryScheduled = false;
                    InvalidateMeasure();
                });
            }
            return;
        }

        _generatorRetryScheduled = false;

        CleanupOutsideRange(generator, firstIndex, lastIndex);
        if (lastIndex < firstIndex) return;
        var start = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                if (generator.GenerateNext(out var newlyRealized) is not UIElement child)
                    break;
                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }
    }

    private void CleanupOutsideRange(IItemContainerGenerator generator, int firstIndex, int lastIndex)
    {
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var itemIndex = IndexFromContainer(InternalChildren[childIndex]);
            if (itemIndex < firstIndex || itemIndex > lastIndex)
            {
                generator.Remove(new GeneratorPosition(childIndex, 0), 1);
                RemoveInternalChildRange(childIndex, 1);
            }
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            var index = IndexFromContainer(child);
            var row = index / _itemsPerRow;
            var column = index % _itemsPerRow;
            child.Arrange(new Rect(column * ItemWidth, row * ItemHeight - _offset.Y, ItemWidth, ItemHeight));
        }
        return finalSize;
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }
    public void LineUp() => SetVerticalOffset(VerticalOffset - 32);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 32);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - ItemHeight);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + ItemHeight);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void PageLeft() { }
    public void PageRight() { }
    public void SetHorizontalOffset(double offset) { }
    public void SetVerticalOffset(double offset)
    {
        var value = Math.Max(0, Math.Min(offset, Math.Max(0, ExtentHeight - ViewportHeight)));
        if (Math.Abs(value - _offset.Y) < 0.1) return;
        _offset.Y = value;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is UIElement element)
        {
            var index = IndexFromContainer(element);
            if (index >= 0)
            {
                var itemTop = index / _itemsPerRow * ItemHeight;
                var itemBottom = itemTop + ItemHeight;
                if (itemTop < VerticalOffset)
                    SetVerticalOffset(itemTop);
                else if (itemBottom > VerticalOffset + ViewportHeight)
                    SetVerticalOffset(itemBottom - ViewportHeight);
            }
        }
        return rectangle;
    }

    private int IndexFromContainer(DependencyObject container) =>
        ItemsControl.GetItemsOwner(this)?.ItemContainerGenerator.IndexFromContainer(container) ?? -1;
}
