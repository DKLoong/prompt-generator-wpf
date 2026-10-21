using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PromptGenerator.Controls;

/// <summary>为 TextBox 提供占位符（Watermark）的附加属性。</summary>
public static class WatermarkService
{
    public static readonly DependencyProperty WatermarkProperty =
        DependencyProperty.RegisterAttached(
            "Watermark",
            typeof(string),
            typeof(WatermarkService),
            new FrameworkPropertyMetadata(string.Empty, OnWatermarkChanged));

    public static string GetWatermark(DependencyObject obj) => (string)obj.GetValue(WatermarkProperty);
    public static void SetWatermark(DependencyObject obj, string value) => obj.SetValue(WatermarkProperty, value);

    private static void OnWatermarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        tb.Loaded -= OnLoaded;
        tb.Loaded += OnLoaded;
        if (tb.IsLoaded) OnLoaded(tb, new RoutedEventArgs());
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var tb = (TextBox)sender;
        tb.Loaded -= OnLoaded;
        var adornerLayer = AdornerLayer.GetAdornerLayer(tb);
        if (adornerLayer == null) return;
        adornerLayer.Add(new WatermarkAdorner(tb, GetWatermark(tb)));
    }

    private class WatermarkAdorner : Adorner
    {
        private static readonly SolidColorBrush PlaceholderBrush =
            new(Color.FromRgb(0x7d, 0x84, 0x8d));

        private readonly string _text;
        private readonly TextBox _tb;
        private readonly FormattedText _ft;

        public WatermarkAdorner(TextBox tb, string text) : base(tb)
        {
            _tb = tb;
            _text = text;
            _ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                tb.FontSize,
                PlaceholderBrush,
                VisualTreeHelper.GetDpi(tb).PixelsPerDip);
            IsHitTestVisible = false;
            tb.TextChanged += OnTextChanged;
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (string.IsNullOrEmpty(_tb.Text) && _text.Length > 0)
            {
                double top = (_tb.ActualHeight - _ft.Height) / 2;
                // 与输入内容左边距统一：内容起点实测恒为 22 物理px（200% DPI 下 11 DIP），
                // 不跟随 Padding（Wrap/NoWrap 下 Padding 偏移机制不一致），固定值保证两框提示语与内容完全对齐
                dc.DrawText(_ft, new Point(11, Math.Max(0, top)));
            }
        }
    }
}
