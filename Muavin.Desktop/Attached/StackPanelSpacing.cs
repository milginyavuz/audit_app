// Muavin.Desktop/Attached/StackPanelSpacing.cs
using System.Windows;
using System.Windows.Controls;

namespace Muavin.Desktop.Attached
{
    public static class StackPanelSpacing
    {
        public static readonly DependencyProperty SpacingProperty =
            DependencyProperty.RegisterAttached(
                "Spacing",
                typeof(double),
                typeof(StackPanelSpacing),
                new PropertyMetadata(0.0, OnSpacingChanged));

        public static double GetSpacing(DependencyObject obj)
            => (double)obj.GetValue(SpacingProperty);

        public static void SetSpacing(DependencyObject obj, double value)
            => obj.SetValue(SpacingProperty, value);

        private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Panel panel) return;
            panel.Loaded -= PanelOnLoaded;
            panel.Loaded += PanelOnLoaded;
            Apply(panel);
        }

        private static void PanelOnLoaded(object sender, RoutedEventArgs e)
            => Apply((Panel)sender);

        private static void Apply(Panel panel)
        {
            var spacing = (double)panel.GetValue(SpacingProperty);
            if (spacing < 0) spacing = 0;

            if (panel is StackPanel sp)
            {
                bool vertical = sp.Orientation == Orientation.Vertical;

                for (int i = 0; i < sp.Children.Count; i++)
                {
                    if (sp.Children[i] is FrameworkElement fe)
                    {
                        if (vertical)
                        {
                            fe.Margin = new Thickness(
                                fe.Margin.Left, fe.Margin.Top, fe.Margin.Right,
                                i < sp.Children.Count - 1 ? spacing : 0);
                        }
                        else
                        {
                            fe.Margin = new Thickness(
                                fe.Margin.Left, fe.Margin.Top,
                                i < sp.Children.Count - 1 ? spacing : 0,
                                fe.Margin.Bottom);
                        }
                    }
                }
            }
            else   // diğer Panel türleri için basit sağ boşluk
            {
                for (int i = 0; i < panel.Children.Count; i++)
                {
                    if (panel.Children[i] is FrameworkElement fe)
                    {
                        fe.Margin = new Thickness(
                            fe.Margin.Left, fe.Margin.Top,
                            i < panel.Children.Count - 1 ? spacing : 0,
                            fe.Margin.Bottom);
                    }
                }
            }
        }
    }
}
