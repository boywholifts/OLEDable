using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HydroShiftRecorder
{
    /// <summary>
    /// The app's own message box: follows the theme (light, dark, true black) and accent instead of the
    /// white Windows one. Rounded card, title, message, and one or two pill buttons.
    /// </summary>
    public static class Dialog
    {
        /// <summary>Shows a question; returns true when the primary button was clicked.</summary>
        public static bool Ask(Window owner, string title, string message, string primary, string secondary = "Cancel")
        {
            return Show(owner, title, message, primary, secondary);
        }

        /// <summary>Shows a message with a single OK button.</summary>
        public static void Tell(Window owner, string title, string message)
        {
            Show(owner, title, message, "OK", null);
        }

        static bool Show(Window owner, string title, string message, string primary, string secondary)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Title = title,
            };
            if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (Application.Current != null && Application.Current.TryFindResource("UiFont") is FontFamily font) w.FontFamily = font;

            var card = new Border
            {
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24, 20, 24, 20),
                Margin = new Thickness(16),   // room for the shadow
                Width = 420,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35 },
            };
            card.SetResourceReference(Border.BackgroundProperty, "Card");
            card.SetResourceReference(Border.BorderBrushProperty, "Line");

            var stack = new StackPanel();
            var head = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
            head.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var body = new TextBlock { Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
            body.SetResourceReference(TextBlock.ForegroundProperty, "Sub");
            stack.Children.Add(head);
            stack.Children.Add(body);

            bool result = false;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            if (secondary != null)
            {
                var no = new Button { Content = secondary, Height = 34, MinWidth = 88, Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
                no.SetResourceReference(FrameworkElement.StyleProperty, "Pill");
                no.Click += (s, e) => { result = false; w.Close(); };
                buttons.Children.Add(no);
            }
            var yes = new Button { Content = primary, Height = 34, MinWidth = 88, IsDefault = true, IsCancel = secondary == null };
            yes.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryPill");
            yes.Click += (s, e) => { result = true; w.Close(); };
            buttons.Children.Add(yes);
            stack.Children.Add(buttons);

            card.Child = stack;
            card.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { w.DragMove(); } catch { } };
            w.Content = card;
            w.ShowDialog();
            return result;
        }
    }
}
