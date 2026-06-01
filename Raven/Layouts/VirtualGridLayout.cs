using System.Collections.Frozen;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Raven.layouts;

public enum UniformGridLayoutItemsJustification
{
    Start,
    Center,
    End,
    SpaceBetween,
    SpaceAround,
    SpaceEvenly,
}

public enum UniformGridLayoutItemsStretch
{
    None,
    Fill,
    Uniform,
}

public sealed class VirtualGridLayout : VirtualizingLayout
{
    // Dependency properties
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth),
        typeof(double),
        typeof(VirtualGridLayout),
        new PropertyMetadata(191.0, OnMeasurePropertyChanged)
    );

    public static readonly DependencyProperty MinItemHeightProperty = DependencyProperty.Register(
        nameof(MinItemHeight),
        typeof(double),
        typeof(VirtualGridLayout),
        new PropertyMetadata(224.0, OnMeasurePropertyChanged)
    );

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns),
        typeof(int),
        typeof(VirtualGridLayout),
        new PropertyMetadata(int.MaxValue, OnMeasurePropertyChanged)
    );

    public static readonly DependencyProperty MinColumnSpacingProperty =
        DependencyProperty.Register(
            nameof(MinColumnSpacing),
            typeof(double),
            typeof(VirtualGridLayout),
            new PropertyMetadata(17.0, OnMeasurePropertyChanged)
        );

    public static readonly DependencyProperty MinRowSpacingProperty = DependencyProperty.Register(
        nameof(MinRowSpacing),
        typeof(double),
        typeof(VirtualGridLayout),
        new PropertyMetadata(32.0, OnMeasurePropertyChanged)
    );

    public static readonly DependencyProperty ItemsJustificationProperty =
        DependencyProperty.Register(
            nameof(ItemsJustification),
            typeof(UniformGridLayoutItemsJustification),
            typeof(VirtualGridLayout),
            new PropertyMetadata(
                UniformGridLayoutItemsJustification.SpaceEvenly,
                OnArrangePropertyChanged
            )
        );

    public static readonly DependencyProperty ItemsStretchProperty = DependencyProperty.Register(
        nameof(ItemsStretch),
        typeof(UniformGridLayoutItemsStretch),
        typeof(VirtualGridLayout),
        new PropertyMetadata(UniformGridLayoutItemsStretch.Fill, OnArrangePropertyChanged)
    );

    // Public properties
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double MinItemHeight
    {
        get => (double)GetValue(MinItemHeightProperty);
        set => SetValue(MinItemHeightProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double MinColumnSpacing
    {
        get => (double)GetValue(MinColumnSpacingProperty);
        set => SetValue(MinColumnSpacingProperty, value);
    }

    public double MinRowSpacing
    {
        get => (double)GetValue(MinRowSpacingProperty);
        set => SetValue(MinRowSpacingProperty, value);
    }

    public UniformGridLayoutItemsJustification ItemsJustification
    {
        get => (UniformGridLayoutItemsJustification)GetValue(ItemsJustificationProperty);
        set => SetValue(ItemsJustificationProperty, value);
    }

    public UniformGridLayoutItemsStretch ItemsStretch
    {
        get => (UniformGridLayoutItemsStretch)GetValue(ItemsStretchProperty);
        set => SetValue(ItemsStretchProperty, value);
    }

    // Most recent column count computed during layout. Exposed so the host can map a scroll
    // offset to a first-visible item index (and back) for scroll-position persistence, which
    // must stay correct when the column count reflows on resize.
    public int LastColumnCount { get; private set; }

    // Vertical distance between the tops of two consecutive rows (uniform geometry).
    public double RowPitch => Math.Max(MinItemHeight, MinValidDimension) + MinRowSpacing;

    // Column count for a given content width, matching CalculateColumnCount's formula.
    public int GetColumnCountForWidth(double availableWidth)
    {
        if (double.IsInfinity(availableWidth) || availableWidth <= 0)
            return 1;

        double itemPlusSpacing = MinItemWidth + MinColumnSpacing;
        if (itemPlusSpacing <= MinValidDimension)
            return 1;

        int cols = (int)Math.Floor((availableWidth + MinColumnSpacing) / itemPlusSpacing);
        return Math.Clamp(cols, 1, MaxColumns);
    }

    // Constants and helpers
    private static readonly Size s_zeroSize = new(0, 0);

    private const int ExtraBufferItems = 1;
    private const int MaxCacheSize = 17;
    private const int CacheFreezeTrigger = 9;
    private const double MinValidDimension = 1e-6;
    private const double SignificantWidthChangeThreshold = 100.0;
    private const double CacheKeyPrecision = 100.0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SizeEquals(Size a, Size b)
    {
        const double eps = 1e-9;
        return Math.Abs(a.Width - b.Width) < eps && Math.Abs(a.Height - b.Height) < eps;
    }

    // Per-host state stored on the context
    private sealed class State
    {
        public double CellWidth;
        public double CellHeight;
        public int Columns;
        public Size LastAvailableSize;
        public bool LayoutInvalid = true;
        public int LastItemCount = -1;
        public int CachedRowCount;
        public bool SignificantSizeChange;
        public int PreviousColumns;
        public int CacheOps;

        // Track last-used spacing to invalidate row-metrics cache when MinColumnSpacing changes.
        public double LastMinColumnSpacing;

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential,
            Pack = 8
        )]
        public struct LayoutCache
        {
            public double RowPitch; // cellHeight + rowSpacing
            public double TotalSpacing; // (columns-1)*colSpacing
            public double ItemPlusSpacing; // MinItemWidth + MinColumnSpacing
            public double CellPitch; // cellWidth + colSpacing
            public double CellWidth;
            public double CellHeight;
            public Size ItemSize;
            public bool IsValid;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Invalidate() => IsValid = false;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void UpdateFor(
                double cellWidth,
                double cellHeight,
                double colSpacing,
                double rowSpacing,
                int columns,
                double minItemWidth
            )
            {
                CellWidth = cellWidth;
                CellHeight = cellHeight;
                CellPitch = cellWidth + colSpacing;
                TotalSpacing = columns > 1 ? (columns - 1) * colSpacing : 0.0;
                RowPitch = cellHeight + rowSpacing;
                ItemPlusSpacing = minItemWidth + colSpacing;
                ItemSize = new Size(cellWidth, cellHeight);
                IsValid = true;
            }
        }

        public LayoutCache LCache;

        public FrozenDictionary<RowMetricsKey, (double offset, double spacing)>? FrozenRowMetrics;
        public Dictionary<RowMetricsKey, (double offset, double spacing)>? WorkingRowMetrics;
    }

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential
    )]
    private readonly struct RowMetricsKey : IEquatable<RowMetricsKey>
    {
        private readonly ulong _packed;

        public RowMetricsKey(
            int itemsInRow,
            double freeSpace,
            UniformGridLayoutItemsJustification justification
        )
        {
            ulong itemsPart = (ulong)Math.Clamp(itemsInRow, 0, 0xFFFF);
            ulong spacePart = (ulong)
                Math.Clamp((long)(Math.Max(0, freeSpace) * CacheKeyPrecision), 0, 0xFFFFFFFFFFL);
            ulong justPart = (ulong)justification;
            _packed = (itemsPart << 48) | (spacePart << 4) | justPart;
        }

        public bool Equals(RowMetricsKey other) => _packed == other._packed;

        public override bool Equals(object? obj) => obj is RowMetricsKey other && Equals(other);

        public override int GetHashCode() => _packed.GetHashCode();

        public static bool operator ==(RowMetricsKey left, RowMetricsKey right) =>
            left.Equals(right);

        public static bool operator !=(RowMetricsKey left, RowMetricsKey right) =>
            !left.Equals(right);
    }

    // Lifecycle: allocate and clear per-host state
    protected override void InitializeForContextCore(VirtualizingLayoutContext context)
    {
        if (context.LayoutState is null)
            context.LayoutState = new State(); // one state per host/container per docs [1][2]
    }

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context)
    {
        if (context.LayoutState is State s)
        {
            s.WorkingRowMetrics?.Clear();
            s.FrozenRowMetrics = null;
            context.LayoutState = null; // host releases state on detach per docs [1][2]
        }
    }

    // Measure/Arrange
    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var s = (State)context.LayoutState!;
        int count = context.ItemCount;
        if (count == 0)
        {
            s.LastItemCount = 0;
            return s_zeroSize;
        }

        bool sizeChanged = !SizeEquals(s.LastAvailableSize, availableSize);
        int newColumns = CalculateColumnCount(ref s, availableSize.Width);

        s.SignificantSizeChange =
            sizeChanged && ShouldTriggerSignificantChange(ref s, newColumns, availableSize.Width);

        if (sizeChanged || s.LayoutInvalid)
        {
            s.LastAvailableSize = availableSize;
            CalculateLayoutParameters(ref s, availableSize);
            s.LayoutInvalid = false;
            s.PreviousColumns = s.Columns;
        }

        int rows = RecalculateRowCountIfNeeded(ref s, count);
        var visibleRange = GetVisibleRange(ref s, context.RealizationRect, rows);
        MeasureVisibleItems(ref s, context, visibleRange, count);

        double totalHeight = CalculateTotalHeight(ref s, rows);
        return new Size(availableSize.Width, totalHeight);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        var s = (State)context.LayoutState!;
        if (context.ItemCount == 0)
            return finalSize;

        if (!SizeEquals(s.LastAvailableSize, finalSize) || s.LayoutInvalid)
        {
            s.LastAvailableSize = finalSize;
            CalculateLayoutParameters(ref s, finalSize);
            s.LayoutInvalid = false;
        }

        int rows = RecalculateRowCountIfNeeded(ref s, context.ItemCount);
        bool processAll = s.SignificantSizeChange;
        s.SignificantSizeChange = false;

        var visibleRange = GetVisibleRange(ref s, context.RealizationRect, rows, processAll);
        ArrangeRows(ref s, context, visibleRange, finalSize.Width);

        return finalSize;
    }

    protected override void OnItemsChangedCore(
        VirtualizingLayoutContext context,
        object source,
        NotifyCollectionChangedEventArgs args
    )
    {
        var s = (State)context.LayoutState!;
        s.LastItemCount = -1;

        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                s.LCache.Invalidate();
                s.LayoutInvalid = true;
                s.SignificantSizeChange = true;
                InvalidateMeasure();
                break;

            case NotifyCollectionChangedAction.Remove when args.OldItems?.Count > 5:
                s.LCache.Invalidate();
                s.LayoutInvalid = true;
                s.SignificantSizeChange = true;
                InvalidateMeasure();
                break;

            case NotifyCollectionChangedAction.Add when args.NewItems?.Count > 3:
                InvalidateMeasure();
                break;

            case NotifyCollectionChangedAction.Move:
                InvalidateArrange();
                break;

            default:
                InvalidateMeasure();
                break;
        }

        base.OnItemsChangedCore(context, source, args);
    }

    // Layout helpers
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldTriggerSignificantChange(ref State s, int newColumns, double newWidth)
    {
        return (s.PreviousColumns != 0 && Math.Abs(newColumns - s.PreviousColumns) > 1)
            || Math.Abs(s.LastAvailableSize.Width - newWidth) > SignificantWidthChangeThreshold;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RecalculateRowCountIfNeeded(ref State s, int itemCount)
    {
        if (itemCount != s.LastItemCount || s.SignificantSizeChange)
        {
            s.CachedRowCount = s.Columns > 0 ? (int)Math.Ceiling((double)itemCount / s.Columns) : 0;
            s.LastItemCount = itemCount;
        }
        return s.CachedRowCount;
    }

    private void CalculateLayoutParameters(ref State s, Size available)
    {
        int newColumns = CalculateColumnCount(ref s, available.Width);

        // Invalidate row-metrics cache if either columns changed or MinColumnSpacing changed.
        bool colChanged = s.Columns != newColumns;
        bool spacingChanged =
            !s.LCache.IsValid || Math.Abs(s.LastMinColumnSpacing - MinColumnSpacing) > 1e-9;
        if (colChanged || spacingChanged)
        {
            s.Columns = newColumns;
            InvalidateRowMetricsCache(ref s);
            s.LCache.Invalidate();
            s.LastMinColumnSpacing = MinColumnSpacing;
        }

        s.CellHeight = Math.Max(MinItemHeight, MinValidDimension);

        double totalSpacing = s.Columns > 1 ? (s.Columns - 1) * MinColumnSpacing : 0.0;
        s.CellWidth =
            s.Columns > 0
                ? Math.Max((available.Width - totalSpacing) / s.Columns, MinValidDimension)
                : MinValidDimension;

        s.LCache.UpdateFor(
            s.CellWidth,
            s.CellHeight,
            MinColumnSpacing,
            MinRowSpacing,
            s.Columns,
            MinItemWidth
        );

        LastColumnCount = s.Columns;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CalculateColumnCount(ref State s, double availableWidth)
    {
        if (double.IsInfinity(availableWidth) || availableWidth <= 0)
            return 1;

        double itemPlusSpacing = s.LCache.IsValid
            ? s.LCache.ItemPlusSpacing
            : (MinItemWidth + MinColumnSpacing);
        if (itemPlusSpacing <= MinValidDimension)
            return 1;

        int cols = (int)Math.Floor((availableWidth + MinColumnSpacing) / itemPlusSpacing);
        return Math.Clamp(cols, 1, MaxColumns);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double CalculateTotalHeight(ref State s, int rows) =>
        rows > 0 ? (rows * s.CellHeight) + ((rows - 1) * MinRowSpacing) : 0;

    // Virtualization helpers
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static RowRange GetVisibleRange(
        ref State s,
        Rect realizationRect,
        int totalRows,
        bool processAll = false
    )
    {
        if (processAll || totalRows <= 0)
            return new RowRange(0, Math.Max(0, totalRows - 1));

        if (
            realizationRect.Height < 1
            || !s.LCache.IsValid
            || s.LCache.RowPitch <= MinValidDimension
        )
            return new RowRange(0, Math.Max(0, totalRows - 1));

        int startRow = Math.Max(0, (int)(realizationRect.Y / s.LCache.RowPitch) - ExtraBufferItems);
        int endRow = Math.Min(
            totalRows - 1,
            (int)(realizationRect.Bottom / s.LCache.RowPitch) + ExtraBufferItems
        );
        return new RowRange(startRow, endRow);
    }

    private static void MeasureVisibleItems(
        ref State s,
        VirtualizingLayoutContext context,
        RowRange visible,
        int itemCount
    )
    {
        if (!s.LCache.IsValid || itemCount == 0 || !visible.IsValid || s.Columns <= 0)
            return;

        var itemSize = s.LCache.ItemSize;
        int endRow =
            s.Columns > 0 ? Math.Min(visible.EndRow, (itemCount - 1) / s.Columns) : visible.EndRow;

        for (int row = visible.StartRow; row <= endRow; row++)
        {
            int baseIndex = row * s.Columns;
            int itemsInRow = Math.Min(s.Columns, itemCount - baseIndex);

            for (int col = 0; col < itemsInRow; col++)
            {
                context.GetOrCreateElementAt(baseIndex + col).Measure(itemSize);
            }
        }
    }

    private void ArrangeRows(
        ref State s,
        VirtualizingLayoutContext context,
        RowRange visible,
        double availableWidth
    )
    {
        if (!s.LCache.IsValid || s.Columns <= 0 || !visible.IsValid)
            return;

        for (int row = visible.StartRow; row <= visible.EndRow; row++)
        {
            int baseIndex = row * s.Columns;
            int itemsInRow = Math.Min(s.Columns, context.ItemCount - baseIndex);
            if (itemsInRow <= 0)
                continue;

            ArrangeRow(ref s, context, row, itemsInRow, availableWidth);
        }
    }

    private void ArrangeRow(
        ref State s,
        VirtualizingLayoutContext context,
        int rowIndex,
        int itemsInRow,
        double availableWidth
    )
    {
        double totalRowWidth = (itemsInRow * s.LCache.CellPitch) - MinColumnSpacing;
        double freeSpace = Math.Max(0, availableWidth - totalRowWidth);
        (double rowOffset, double interItemSpacing) = GetCachedRowMetrics(
            ref s,
            itemsInRow,
            freeSpace
        );

        double y = rowIndex * s.LCache.RowPitch;
        int baseIndex = rowIndex * s.Columns;
        double increment = s.LCache.CellWidth + interItemSpacing;
        bool fill = ItemsStretch == UniformGridLayoutItemsStretch.Fill;

        double x = rowOffset;
        for (int col = 0; col < itemsInRow; col++, x += increment)
        {
            ArrangeElement(ref s, context, baseIndex + col, x, y, fill);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ArrangeElement(
        ref State s,
        VirtualizingLayoutContext context,
        int itemIndex,
        double x,
        double y,
        bool fill
    )
    {
        var element = context.GetOrCreateElementAt(itemIndex);
        var cell = new Rect(x, y, s.LCache.CellWidth, s.LCache.CellHeight);
        var finalRect = fill ? cell : CalculateItemRect(cell);
        element.Arrange(finalRect);
    }

    // Newly added: compute the item rectangle inside a cell for Fill/Uniform/None.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Rect CalculateItemRect(Rect cell)
    {
        if (ItemsStretch == UniformGridLayoutItemsStretch.Fill)
            return cell;

        double itemWidth = MinItemWidth;
        double itemHeight = MinItemHeight;

        if (
            ItemsStretch == UniformGridLayoutItemsStretch.Uniform
            && MinItemHeight > MinValidDimension
        )
        {
            double cellAR = cell.Width / cell.Height;
            double itemAR = MinItemWidth / MinItemHeight;

            if (cellAR > itemAR)
            {
                itemHeight = cell.Height;
                itemWidth = itemHeight * itemAR;
            }
            else
            {
                itemWidth = cell.Width;
                itemHeight = itemWidth / itemAR;
            }
        }

        double x = cell.X + (cell.Width - itemWidth) * 0.5;
        double y = cell.Y + (cell.Height - itemHeight) * 0.5;
        return new Rect(x, y, itemWidth, itemHeight);
    }

    private (double Offset, double Spacing) GetCachedRowMetrics(
        ref State s,
        int itemsInRow,
        double freeSpace
    )
    {
        var key = new RowMetricsKey(itemsInRow, freeSpace, ItemsJustification);

        if (s.FrozenRowMetrics?.TryGetValue(key, out var frozen) == true)
            return frozen;

        s.WorkingRowMetrics ??= new Dictionary<RowMetricsKey, (double, double)>(MaxCacheSize);

        if (s.WorkingRowMetrics.TryGetValue(key, out var cached))
            return cached;

        var result = CalculateRowMetrics(itemsInRow, freeSpace);

        if (s.WorkingRowMetrics.Count < MaxCacheSize)
        {
            s.WorkingRowMetrics[key] = result;

            if (++s.CacheOps >= CacheFreezeTrigger)
            {
                s.FrozenRowMetrics = s.WorkingRowMetrics.ToFrozenDictionary();
                s.WorkingRowMetrics.Clear();
                s.CacheOps = 0;
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (double Offset, double Spacing) CalculateRowMetrics(int itemsInRow, double freeSpace)
    {
        double offset = 0;
        double spacing = MinColumnSpacing;

        switch (ItemsJustification)
        {
            case UniformGridLayoutItemsJustification.Start:
                break;

            case UniformGridLayoutItemsJustification.Center:
                offset = freeSpace * 0.5;
                break;

            case UniformGridLayoutItemsJustification.End:
                offset = freeSpace;
                break;

            case UniformGridLayoutItemsJustification.SpaceBetween when itemsInRow > 1:
                spacing += freeSpace / (itemsInRow - 1);
                break;

            case UniformGridLayoutItemsJustification.SpaceAround when itemsInRow > 0:
                spacing += freeSpace / itemsInRow;
                offset = (spacing - MinColumnSpacing) * 0.5;
                break;

            case UniformGridLayoutItemsJustification.SpaceEvenly when itemsInRow > 0:
                double even = freeSpace / (itemsInRow + 1);
                spacing += even;
                offset = even;
                break;
        }

        return (offset, spacing);
    }

    // Cache + invalidation
    private static void InvalidateRowMetricsCache(ref State s)
    {
        s.WorkingRowMetrics?.Clear();
        s.FrozenRowMetrics = null;
        s.CacheOps = 0;
    }

    private static void OnMeasurePropertyChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e
    )
    {
        if (d is VirtualGridLayout layout)
            layout.InvalidateMeasure(); // per docs: measurement-affecting DPs should invalidate measure [2][1]
    }

    private static void OnArrangePropertyChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e
    )
    {
        if (d is VirtualGridLayout layout)
            layout.InvalidateArrange(); // per docs: arrangement-affecting DPs should invalidate arrange [2][1]
    }

    private readonly struct RowRange
    {
        public int StartRow { get; }
        public int EndRow { get; }

        public RowRange(int start, int end)
        {
            StartRow = start;
            EndRow = end;
        }

        public bool IsValid => StartRow >= 0 && EndRow >= StartRow;
    }
}
