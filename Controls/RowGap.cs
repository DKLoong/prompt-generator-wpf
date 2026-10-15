using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PromptGenerator.Controls;

/// <summary>行间插入条：鼠标移入时显示"+"，点击在当前位置插入新字段行。</summary>
public class RowGap : UserControl
{
    private readonly Button _addBtn;

    public event EventHandler? AddClicked;

    public RowGap()
    {
        Height = 20;
        Background = Brushes.Transparent;

        _addBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(0),
            Visibility = Visibility.Collapsed,
            IsTabStop = false,
            ToolTip = "在此插入字段"
        };
        if (Application.Current.TryFindResource("IconBtn") is Style s) _addBtn.Style = s;
        // 用 Path 画十字，保证水平垂直绝对居中（字符"+"受字体基线影响会偏上）
        var cross = new Path
        {
            Data = Geometry.Parse("M 4.5,10 L 15.5,10 M 10,4.5 L 10,15.5"),
            Stroke = Brushes.White,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 20,
            Height = 20,
            Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false
        };
        _addBtn.Content = cross;
        _addBtn.Click += (_, _) => AddClicked?.Invoke(this, EventArgs.Empty);

        var grid = new Grid { Background = Brushes.Transparent };
        grid.Children.Add(_addBtn);
        Content = grid;

        MouseEnter += (_, _) => _addBtn.Visibility = Visibility.Visible;
        MouseLeave += (_, _) => _addBtn.Visibility = Visibility.Collapsed;
    }
}
