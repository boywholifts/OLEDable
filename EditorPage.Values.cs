using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static OLEDable.Tools;

namespace OLEDable
{
    /// <summary>
    /// Typing exact values: every slider's value label becomes a small text box when clicked (or when the
    /// slider is double-clicked). Enter or clicking away applies, Esc cancels, the arrow keys nudge (Shift for
    /// 10×). The mouse wheel over a slider nudges it too. The trim times can be typed the same way.
    /// </summary>
    public partial class EditorPage
    {
        /// <summary>One editable slider: how its value is shown in the box and turned back into a slider value.</summary>
        void Editable(Slider slider, TextBlock label, Func<double, double> toShown, Func<double, double> fromShown, string unit, double step, int decimals = 0)
        {
            Action begin = MakeEditable(label,
                () => Math.Round(toShown(slider.Value), decimals).ToString(Inv) + unit,
                text =>
                {
                    double v;
                    if (!ParseNumber(text, out v)) return false;
                    slider.Value = Clamp(fromShown(v), slider.Minimum, slider.Maximum);
                    return true;
                },
                n => slider.Value = Clamp(slider.Value + n * step, slider.Minimum, slider.Maximum));
            slider.MouseDoubleClick += (o, e) => { if (slider.IsEnabled) begin(); };
            slider.PreviewMouseWheel += (o, e) =>
            {
                if (!slider.IsEnabled) return;
                int n = e.Delta > 0 ? 1 : -1;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) n *= 10;
                slider.Value = Clamp(slider.Value + n * step, slider.Minimum, slider.Maximum);
                e.Handled = true;
            };
        }

        void SetupValueEditing()
        {
            // what the label shows ⇄ the slider's own units
            Editable(BlackSlider, BlackVal, v => v / 255 * 100, p => p * 255 / 100, "%", 1);
            Editable(ShadowSlider, ShadowVal, v => (v - 1) * 100, p => 1 + p / 100, "%", 0.01);
            Editable(ColorSlider, ColorVal, v => (v - 1) * 100, p => 1 + p / 100, "%", 0.01);
            Editable(BrightSlider, BrightVal, v => v * 100, p => p / 100, "%", 0.01);
            Editable(HighSlider, HighVal, v => v * 100, p => p / 100, "%", 0.01);
            Editable(GlowSlider, GlowVal, v => v * 100, p => p / 100, "%", 0.01);
            Editable(GlowSizeSlider, GlowSizeVal, v => v, p => p, " px", 1);
            Editable(StrengthSlider, StrengthVal, v => v * 100, p => p / 100, "%", 0.01);

            // trim: "0:04.5 – 0:20"
            MakeEditable(TrimText,
                () => s == null ? "" : FmtTenths(s.UsedStart) + " – " + FmtTenths(s.UsedEnd),
                text =>
                {
                    if (s == null || s.Duration <= 0) return false;
                    var parts = text.Split(new[] { '–', '-', '—' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2) return false;
                    double a, b;
                    if (!ParseTime(parts[0], out a) || !ParseTime(parts[1], out b)) return false;
                    a = Math.Round(Clamp(a, 0, Math.Max(0, s.Duration - 1)), 1);
                    b = Math.Round(Clamp(b, a + 1, s.Duration), 1);
                    s.TrimStart = a;
                    s.TrimEnd = b >= s.Duration - 0.05 ? 0 : b;
                    UpdateTrim();
                    Touched();
                    return true;
                },
                null);
        }

        /// <summary>
        /// Puts a text box over <paramref name="label"/> that opens on click. Returns the action that opens it.
        /// <paramref name="apply"/> returns false for text it can't read (the old value stays).
        /// </summary>
        Action MakeEditable(TextBlock label, Func<string> current, Func<string, bool> apply, Action<int> nudge)
        {
            var parent = (Panel)label.Parent;
            int idx = parent.Children.IndexOf(label);
            parent.Children.RemoveAt(idx);
            var host = new Grid
            {
                HorizontalAlignment = label.HorizontalAlignment,
                VerticalAlignment = label.VerticalAlignment,
                Margin = label.Margin,
            };
            DockPanel.SetDock(host, DockPanel.GetDock(label));
            label.Margin = new Thickness(0);

            var box = new TextBox
            {
                Visibility = Visibility.Collapsed,
                MinWidth = 64,
                FontSize = label.FontSize,
                HorizontalContentAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 1, 4, 1),
                BorderThickness = new Thickness(1),
            };
            box.SetResourceReference(Control.BackgroundProperty, "Btn2");
            box.SetResourceReference(Control.ForegroundProperty, "Text");
            box.SetResourceReference(Control.BorderBrushProperty, "Accent");
            box.SetResourceReference(TextBox.CaretBrushProperty, "Text");
            box.SetResourceReference(TextBox.SelectionBrushProperty, "Accent");
            host.Children.Add(label);
            host.Children.Add(box);
            parent.Children.Insert(idx, host);

            label.Cursor = Cursors.IBeam;
            label.ToolTip = "Click to type a value";

            Action begin = () =>
            {
                if (s == null) return;
                box.Text = current();
                label.Visibility = Visibility.Hidden;
                box.Visibility = Visibility.Visible;
                box.Focus();
                Keyboard.Focus(box);
                box.SelectAll();
            };
            Action<bool> end = commit =>
            {
                if (box.Visibility != Visibility.Visible) return;
                box.Visibility = Visibility.Collapsed;   // first, so losing focus below doesn't run this again
                label.Visibility = Visibility.Visible;
                if (commit) apply(box.Text);
            };

            label.MouseLeftButtonUp += (o, e) => { begin(); e.Handled = true; };
            box.PreviewKeyDown += (o, e) =>
            {
                if (e.Key == Key.Enter) { end(true); e.Handled = true; }
                else if (e.Key == Key.Escape) { end(false); e.Handled = true; }
                else if ((e.Key == Key.Up || e.Key == Key.Down) && nudge != null)
                {
                    // apply what's typed so far, then step from there
                    apply(box.Text);
                    int n = (e.Key == Key.Up ? 1 : -1) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1);
                    nudge(n);
                    box.Text = current();
                    box.SelectAll();
                    e.Handled = true;
                }
            };
            box.LostKeyboardFocus += (o, e) => end(true);
            return begin;
        }

        /// <summary>Reads "18", "18%", "+8", "−8 %", "20px", "1,5".</summary>
        static bool ParseNumber(string text, out double v)
        {
            v = 0;
            var t = (text ?? "").Replace('−', '-').Replace(',', '.');
            var m = Regex.Match(t, "[-+]?\\d*\\.?\\d+");
            return m.Success && double.TryParse(m.Value, System.Globalization.NumberStyles.Float, Inv, out v);
        }

        /// <summary>Reads "1:04.5", "0:20", "64.5" (seconds).</summary>
        static bool ParseTime(string text, out double sec)
        {
            sec = 0;
            var t = (text ?? "").Trim().Replace(',', '.');
            var m = Regex.Match(t, "^(\\d+):(\\d{1,2}(?:\\.\\d+)?)$");
            if (m.Success)
            {
                sec = int.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value, Inv);
                return true;
            }
            return double.TryParse(t.TrimEnd('s', ' '), System.Globalization.NumberStyles.Float, Inv, out sec) && sec >= 0;
        }

        static string FmtTenths(double sec)
        {
            sec = Math.Max(0, sec);
            int m = (int)(sec / 60);
            return string.Format(Inv, "{0}:{1:00.0}", m, sec - m * 60);
        }
    }
}
