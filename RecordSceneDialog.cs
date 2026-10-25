using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HydroShiftRecorder
{
    /// <summary>
    /// "Record animation" for a Wallpaper Engine scene: pick the size, length and frame rate, then Record.
    /// Themed like the app's other pop-ups (see <see cref="Dialog"/>).
    /// </summary>
    public static class RecordSceneDialog
    {
        public class Choice { public int W, H, Seconds, Fps; }

        /// <summary>Shows the dialog; null when cancelled. Starts from the Record page's length and frame rate.</summary>
        public static Choice Show(Window owner, string name, int seconds, int fps)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight,
                Title = "Record animation", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            if (Application.Current != null && Application.Current.TryFindResource("UiFont") is FontFamily font) w.FontFamily = font;

            var card = new Border
            {
                CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(24, 20, 24, 20),
                Margin = new Thickness(16), Width = 460,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35 },
            };
            card.SetResourceReference(Border.BackgroundProperty, "Card");
            card.SetResourceReference(Border.BorderBrushProperty, "Line");
            var stack = new StackPanel();

            stack.Children.Add(Text("Record “" + name + "”", 16, true, "Text", new Thickness(0, 0, 0, 6)));
            stack.Children.Add(Text("Wallpaper Engine plays it in a window at this size and the app records that window. " +
                                    "Your desktop and the HydroShift aren't changed.", 12, false, "Sub", new Thickness(0, 0, 0, 16)));

            // size
            stack.Children.Add(Text("SIZE", 11, true, "Sub", new Thickness(0, 0, 0, 8)));
            var sizes = new WrapPanel();
            RadioButton first = null;
            foreach (var s in SceneRecorder.Sizes)
            {
                var rb = new RadioButton { Content = s.Item1, GroupName = "recsize", Tag = s, Margin = new Thickness(0, 0, 6, 6) };
                rb.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
                if (first == null) { first = rb; rb.IsChecked = true; }
                sizes.Children.Add(rb);
            }
            stack.Children.Add(sizes);

            // length
            var lenRow = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            var lenVal = Text("", 13, false, "Sub", new Thickness(0));
            DockPanel.SetDock(lenVal, Dock.Right);
            lenRow.Children.Add(lenVal);
            lenRow.Children.Add(Text("Length", 14, false, "Text", new Thickness(0)));
            stack.Children.Add(lenRow);
            var len = new Slider { Minimum = 5, Maximum = Math.Max(300, seconds), Value = seconds, Margin = new Thickness(0, 6, 0, 0) };
            len.SetResourceReference(FrameworkElement.StyleProperty, "ThinSlider");
            Action showLen = () =>
            {
                int v = (int)Math.Max(5, Math.Round(len.Value / 5) * 5);
                lenVal.Text = v < 60 ? v + " s" : Tools.Fmt(v);
            };
            len.ValueChanged += (o, e) => showLen();
            showLen();
            stack.Children.Add(len);

            // frame rate
            var fpsRow = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            var seg = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(2) };
            seg.SetResourceReference(Border.BackgroundProperty, "Bg");
            var segs = new StackPanel { Orientation = Orientation.Horizontal };
            var f30 = new RadioButton { Content = "30", GroupName = "recfps" };
            var f60 = new RadioButton { Content = "60", GroupName = "recfps" };
            foreach (var r in new[] { f30, f60 }) { r.SetResourceReference(FrameworkElement.StyleProperty, "Seg"); segs.Children.Add(r); }
            (fps == 30 ? f30 : f60).IsChecked = true;
            seg.Child = segs;
            DockPanel.SetDock(seg, Dock.Right);
            fpsRow.Children.Add(seg);
            fpsRow.Children.Add(Text("Frame rate", 14, false, "Text", new Thickness(0)));
            stack.Children.Add(fpsRow);

            // buttons
            Choice result = null;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            var cancel = new Button { Content = "Cancel", Height = 34, MinWidth = 88, Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
            cancel.SetResourceReference(FrameworkElement.StyleProperty, "Pill");
            cancel.Click += (o, e) => w.Close();
            var rec = new Button { Content = "Record", Height = 34, MinWidth = 88, IsDefault = true };
            rec.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryPill");
            rec.Click += (o, e) =>
            {
                Tuple<string, int, int> size = null;
                foreach (RadioButton rb in sizes.Children) if (rb.IsChecked == true) size = (Tuple<string, int, int>)rb.Tag;
                if (size == null) return;
                result = new Choice
                {
                    W = size.Item2, H = size.Item3,
                    Seconds = (int)Math.Max(5, Math.Round(len.Value / 5) * 5),
                    Fps = f30.IsChecked == true ? 30 : 60,
                };
                w.Close();
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(rec);
            stack.Children.Add(buttons);

            card.Child = stack;
            card.MouseLeftButtonDown += (o, e) => { if (e.ButtonState == MouseButtonState.Pressed && !(e.OriginalSource is Thumb)) try { w.DragMove(); } catch { } };
            w.Content = card;
            w.ShowDialog();
            return result;
        }

        static TextBlock Text(string text, double size, bool bold, string brush, Thickness margin)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }
    }
}
