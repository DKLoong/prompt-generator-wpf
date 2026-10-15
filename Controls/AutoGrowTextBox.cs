using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PromptGenerator.Controls;

/// <summary>
/// 自动增高 TextBox：单行起（32px），随内容换行增高，上限 200px。
/// 仅当实际内容需要更高时才增高（占位符为空时保持单行）。
/// </summary>
public class AutoGrowTextBox : TextBox
{
    public const double MinHeightPx = 32;
    public const double MaxHeightPx = 200;

    /// <summary>上次算出的目标高度，用于过滤 ExtentHeight 的微小波动，避免多行时每次输入都跳动。</summary>
    private double _lastNeeded;

    static AutoGrowTextBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(AutoGrowTextBox),
            new FrameworkPropertyMetadata(typeof(TextBox)));
    }

    public AutoGrowTextBox()
    {
        TextWrapping = TextWrapping.Wrap;
        AcceptsReturn = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        MinHeight = MinHeightPx;
        MaxHeight = MaxHeightPx;
        Height = MinHeightPx;
        VerticalContentAlignment = VerticalAlignment.Center;
        Padding = new Thickness(4, 3, 4, 3);
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        ResizeToContent();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.WidthChanged) ResizeToContent();
    }

    private void ResizeToContent()
    {
        if (ActualWidth <= 20) return;
        if (string.IsNullOrEmpty(Text))
        {
            _lastNeeded = 0;
            if (Math.Abs(Height - MinHeightPx) > 0.5) Height = MinHeightPx;
            return;
        }
        // 等当前布局周期完成后，直接读取 ScrollViewer 的实际内容高度（ExtentHeight）。
        Dispatcher.BeginInvoke(new Action(() => SyncResize()),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 同步根据 ScrollViewer.ExtentHeight 计算并设置高度。
    /// 由 RenderFields 在 UpdateLayout() 后调用，避免重建后先 32px 再异步长高的闪烁。
    /// </summary>
    public void SyncResize()
    {
        if (ActualWidth <= 20 || string.IsNullOrEmpty(Text)) return;
        if (GetTemplateChild("PART_ContentHost") is not ScrollViewer sv) return;
        if (sv.ExtentHeight <= 0) return;
        double needed = sv.ExtentHeight + Padding.Top + Padding.Bottom + 2 + 8;
        needed = Math.Max(MinHeightPx, Math.Min(MaxHeightPx, needed));
        if (_lastNeeded <= 0 || Math.Abs(needed - _lastNeeded) >= 2.0)
        {
            _lastNeeded = needed;
            if (Math.Abs(Height - needed) > 0.5)
            {
                Height = needed;
                sv.ScrollToVerticalOffset(0);
            }
        }
    }
}
