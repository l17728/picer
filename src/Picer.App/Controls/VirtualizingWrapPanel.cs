using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Picer.App.ViewModels;

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
    private bool _cleanupInProgress;
    private Rect[] _layouts = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        var count = owner?.Items.Count ?? 0;
        var ownerWidth = owner?.ActualWidth ?? 0;
        var width = double.IsFinite(availableSize.Width)
            ? Math.Max(ItemWidth, availableSize.Width)
            : Math.Max(ItemWidth, double.IsFinite(ownerWidth) ? ownerWidth : ItemWidth);
        _itemsPerRow = Math.Max(1, (int)Math.Floor(width / ItemWidth));
        _layouts = BuildLayouts(owner, count, width);
        var extentHeight = _layouts.Length == 0 ? 0 : _layouts.Max(rect => rect.Bottom);
        _extent = new Size(width, extentHeight);
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : _extent.Height;
        _viewport = new Size(width, height);
        ScrollOwner?.InvalidateScrollInfo();

        var first = Array.FindIndex(_layouts, rect => rect.Bottom >= _offset.Y - ItemHeight);
        var last = Array.FindLastIndex(_layouts, rect => rect.Top <= _offset.Y + _viewport.Height + ItemHeight);
        var range = (FirstIndex: Math.Max(0, first), LastIndex: last);
        RealizeRange(range.FirstIndex, range.LastIndex, new Size(width, height));
        return new Size(width, _extent.Height);
    }

    private Rect[] BuildLayouts(ItemsControl? owner, int count, double width)
    {
        var layouts = new Rect[count];
        var rowTop = 0d;
        var column = 0;
        const double anchorHeight = 48d;
        for (var index = 0; index < count; index++)
        {
            if (owner?.Items[index] is TimelineAnchorItemViewModel)
            {
                if (column > 0) { rowTop += ItemHeight; column = 0; }
                layouts[index] = new Rect(0, rowTop, width, anchorHeight);
                rowTop += anchorHeight;
                continue;
            }
            layouts[index] = new Rect(column * ItemWidth, rowTop, ItemWidth, ItemHeight);
            column++;
            if (column >= _itemsPerRow) { column = 0; rowTop += ItemHeight; }
        }
        return layouts;
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
                var layout = itemIndex < _layouts.Length ? _layouts[itemIndex] : new Rect(0, 0, ItemWidth, ItemHeight);
                child.Measure(layout.Size);
            }
        }
    }

    private void CleanupOutsideRange(IItemContainerGenerator generator, int firstIndex, int lastIndex)
    {
        if (_cleanupInProgress) return;
        _cleanupInProgress = true;
        try
        {
            for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
            {
                var itemIndex = IndexFromContainer(InternalChildren[childIndex]);
                if (itemIndex >= firstIndex && itemIndex <= lastIndex) continue;

                // A collection change can detach a visual before the next measure.
                // In that case it no longer has a generator position and only the
                // stale visual must be removed.  childIndex is not a generator
                // position when the realized range starts after item zero.
                var position = itemIndex >= 0
                    ? generator.GeneratorPositionFromIndex(itemIndex)
                    : new GeneratorPosition(-1, 0);
                if (position.Index >= 0 && position.Offset == 0)
                    generator.Remove(position, 1);
                RemoveInternalChildRange(childIndex, 1);
            }
        }
        finally { _cleanupInProgress = false; }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            var index = IndexFromContainer(child);
            if (index >= 0 && index < _layouts.Length)
            {
                var layout = _layouts[index];
                child.Arrange(new Rect(layout.X, layout.Y - _offset.Y, layout.Width, layout.Height));
            }
        }
        return finalSize;
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= _layouts.Length) return;
        SetVerticalOffset(_layouts[index].Top);
        InvalidateMeasure();
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
                var layout = index < _layouts.Length ? _layouts[index] : Rect.Empty;
                var itemTop = layout.Top;
                var itemBottom = layout.Bottom;
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
