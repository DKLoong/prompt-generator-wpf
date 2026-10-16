using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PromptGenerator.Controls;

/// <summary>
/// 字段间隙插入条：高 8px，鼠标移入时以 opacity 渐变显示"+"（默认 opacity=0 保留占位，不跳动），
/// 点击在当前位置插入新字段行。位置与内容框插入"+"同列（左对齐）。
/// </summary>
public class RowGap : UserControl
{
    private readonly Button _addBtn;

    public event EventHandler? AddClicked;

    public RowGap()
    {
        Height = 8;
        Background = Brushes.Transparent;

        _addBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsTabStop = false,
            ToolTip = "在此插入字段",
            Opacity = 0,
            IsHitTestVisible = false
        };
        if (Application.Current.TryFindResource("IconBtnSoft") is Style s) _addBtn.Style = s;
        // 线性十字，颜色跟随按钮 Foreground（Muted → hover AccentHover），与内容框"+"同尺寸（12×12）
        // 数据坐标 1~11 中心对称：Stretch=None 布局盒=几何 bounds 最大值，视觉中心与按钮中心重合
        var cross = new Path
        {
            Data = Geometry.Parse("M 6,1 L 6,11 M 1,6 L 11,6"),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 12,
            Height = 12,
            Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false
        };
        cross.SetBinding(Shape.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = _addBtn });
        _addBtn.Content = cross;
        _addBtn.Click += (_, _) => AddClicked?.Invoke(this, EventArgs.Empty);

        var grid = new Grid { Background = Brushes.Transparent };
        grid.Children.Add(_addBtn);
        Content = grid;

        MouseEnter += (_, _) => { _addBtn.Opacity = 1; _addBtn.IsHitTestVisible = true; };
        MouseLeave += (_, _) => { _addBtn.Opacity = 0; _addBtn.IsHitTestVisible = false; };
    }
}
