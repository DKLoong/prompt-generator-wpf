using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PromptGenerator.Controls;

public enum ActionMode { Copy, Submit }

/// <summary>
/// 底部"生成并复制/生成并提交"分割按钮：
/// 左半为执行区（当前模式文案），右半 36px 正方形触发区（▼ 箭头 17px，垂直居中）点击展开菜单。
/// 选中菜单项即切换模式并执行。
/// </summary>
public class SplitButton : UserControl
{
    public event EventHandler<ActionMode>? Executed;

    private ActionMode _mode = ActionMode.Copy;

    public SplitButton()
    {
        var root = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x7c, 0x6c, 0xf0)),
            Height = 36,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        _leftBtn = new Button
        {
            Content = ModeText(ActionMode.Copy),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _leftBtn.Click += (_, _) => Executed?.Invoke(this, _mode);

        _rightBtn = new Button
        {
            Content = "▼",
            FontSize = 12,
            Foreground = Brushes.White,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _rightBtn.Click += (_, _) => ShowMenu();

        grid.Children.Add(_leftBtn);
        grid.Children.Add(_rightBtn);
        Grid.SetColumn(_rightBtn, 1);

        root.Child = grid;
        Content = root;
    }

    private readonly Button _leftBtn;
    private readonly Button _rightBtn;

    private static string ModeText(ActionMode m) => m == ActionMode.Copy ? "生成并复制" : "生成并提交";

    private void ShowMenu()
    {
        var menu = new ContextMenu();
        var miCopy = new MenuItem { Header = "生成并复制", IsCheckable = true, IsChecked = _mode == ActionMode.Copy };
        var miSubmit = new MenuItem { Header = "生成并提交", IsCheckable = true, IsChecked = _mode == ActionMode.Submit };
        miCopy.Click += (_, _) => { _mode = ActionMode.Copy; _leftBtn.Content = ModeText(_mode); Executed?.Invoke(this, _mode); };
        miSubmit.Click += (_, _) => { _mode = ActionMode.Submit; _leftBtn.Content = ModeText(_mode); Executed?.Invoke(this, _mode); };
        menu.Items.Add(miCopy);
        menu.Items.Add(miSubmit);
        menu.PlacementTarget = _rightBtn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
