using System.Windows;
using System.Windows.Media;

namespace CuinProcessEase.App.Controls;

/// <summary>
/// 轻量折线图（Phase 8）：DrawingContext 自绘，无第三方图表框架。
/// </summary>
/// <remarks>
/// - <see cref="Values"/> 为等距采样序列（最新在最右）；null 表示该秒无数据，折线断开；
/// - 自动以窗口内最大非空值为 100% 基线（至少 MaxFloor，避免近零曲线顶格抖动）；
/// - 值变化才重绘（InvalidateVisual），60 点折线绘制开销可忽略。
/// </remarks>
public sealed class SparklineControl : FrameworkElement
{
    /// <summary>最小纵轴基线（CPU 用 5%，内存用 MB 级），避免平缓曲线顶满。</summary>
    public static readonly DependencyProperty MaxFloorProperty = DependencyProperty.Register(
        nameof(MaxFloor), typeof(double), typeof(SparklineControl),
        new PropertyMetadata(1.0, OnDataChanged));

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double?>), typeof(SparklineControl),
        new PropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(SparklineControl),
        new PropertyMetadata(Brushes.DodgerBlue, OnVisualPropertyChanged));

    public double MaxFloor
    {
        get => (double)GetValue(MaxFloorProperty);
        set => SetValue(MaxFloorProperty, value);
    }

    public IReadOnlyList<double?>? Values
    {
        get => (IReadOnlyList<double?>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    static SparklineControl()
    {
        // 尺寸由布局给定（详情面板固定宽度 + 固定高度），无需自定义测量
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SparklineControl), new FrameworkPropertyMetadata(typeof(FrameworkElement)));
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SparklineControl)d).InvalidateVisual();

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SparklineControl)d).InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // 背景基线
        drawingContext.DrawLine(
            new Pen(Brushes.Transparent, 0), new Point(0, 0), new Point(0, 0));

        IReadOnlyList<double?>? values = Values;
        if (values is not { Count: > 0 })
        {
            DrawPlaceholder(drawingContext, width, height);
            return;
        }

        double max = MaxFloor;
        foreach (double? v in values)
        {
            if (v is { } value && value > max)
            {
                max = value;
            }
        }

        var pen = new Pen(LineBrush, 1.5);
        pen.Freeze();

        double stepX = values.Count == 1 ? 0 : width / (values.Count - 1);
        bool previousValid = false;
        var previous = new Point();
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } value)
            {
                previousValid = false;
                continue;
            }

            var point = new Point(i * stepX, height - (value / max) * height);
            if (previousValid)
            {
                drawingContext.DrawLine(pen, previous, point);
            }

            previous = point;
            previousValid = true;
        }
    }

    private void DrawPlaceholder(DrawingContext drawingContext, double width, double height)
    {
        // 无数据（首个采样周期 / 全部读取失败）：底部一条暗色直线占位
        var placeholder = new Pen(new SolidColorBrush(Color.FromArgb(64, 128, 128, 128)), 1);
        placeholder.Freeze();
        drawingContext.DrawLine(placeholder, new Point(0, height - 0.75), new Point(width, height - 0.75));
    }
}
