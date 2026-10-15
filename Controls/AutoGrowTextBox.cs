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
            if (Math.Abs(Height - MinHeightPx) > 0.5) Height = MinHeightPx;
            return;
        }
        // 等当前布局周期完成后，直接读取 ScrollViewer 的实际内容高度（ExtentHeight）。
        // 旧版用 FormattedText 手动测量，因 PART_ContentHost 内部是 TextBoxView 而非 TextBlock，
        // 导致换行点与实际渲染不一致，高度少算一行，首行被裁剪。
        // 改用 WPF 自己的布局结果，避免测量误差；同时重置垂直滚动偏移，防止视口变大后
        // 仍停留在“光标在底部”的位置而裁掉首行。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (GetTemplateChild("PART_ContentHost") is not ScrollViewer sv) return;
            if (sv.ExtentHeight <= 0) return;
            // Height = 内容高度 + Padding(经 ScrollViewer.Margin 生效) + BorderThickness 上下各 1
            // 末尾再加 4px 缓冲，吸收 DPI 舍入与 TextBoxView 内部边距误差，避免最后一行底部被裁。
            double needed = sv.ExtentHeight + Padding.Top + Padding.Bottom + 2 + 4;
            needed = Math.Max(MinHeightPx, Math.Min(MaxHeightPx, needed));
            if (Math.Abs(Height - needed) > 0.5)
            {
                Height = needed;
                sv.ScrollToVerticalOffset(0);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
}
