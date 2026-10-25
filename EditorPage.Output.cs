using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static HydroShiftRecorder.Tools;

namespace HydroShiftRecorder
{
    /// <summary>
    /// The OUTPUT section: HydroShift (2288 × 1048), the source's original size, an upscale that keeps the shape,
    /// or a custom size. The preview takes the output's shape, and still images hide the video-only controls.
    /// </summary>
    public partial class EditorPage
    {
        bool outSync;

        void SetupOutputControls()
        {
            OutPanel.Checked += (o, e) => SetOutput(OutputSpec.HydroShift);
            OutOrig.Checked += (o, e) => SetOutput(OutputSpec.Original);
            OutUp.Checked += (o, e) => SetOutput(OutputSpec.Upscale);
            OutCustom.Checked += (o, e) => SetOutput(OutputSpec.Custom);
            Up2.Checked += (o, e) => SetUpscale(2);
            Up4.Checked += (o, e) => SetUpscale(4);

            foreach (var preset in OutputSpec.Presets)
            {
                var size = preset.Value;
                var chip = new Button { Content = preset.Key, ToolTip = size[0] + " × " + size[1], Margin = new Thickness(0, 0, 6, 6), Height = 30, Padding = new Thickness(12, 0, 12, 0) };
                chip.SetResourceReference(FrameworkElement.StyleProperty, "Pill");
                chip.Click += (o, e) => SetCustomSize(size[0], size[1]);
                PresetChips.Children.Add(chip);
            }
            foreach (var box in new[] { CustomWBox, CustomHBox })
            {
                box.LostKeyboardFocus += (o, e) => ApplyTypedSize();
                box.KeyDown += (o, e) => { if (e.Key == Key.Enter) { ApplyTypedSize(); e.Handled = true; } };
            }
        }

        void SetOutput(string mode)
        {
            if (outSync || s == null || (s.OutputMode ?? OutputSpec.HydroShift) == mode) return;
            s.OutputMode = mode;
            if (mode == OutputSpec.Upscale && s.UpscaleFactor != 4) s.UpscaleFactor = 2;
            if (mode == OutputSpec.Custom && (s.CustomW < 16 || s.CustomH < 16)) { s.CustomW = 3840; s.CustomH = 2160; }
            if (mode == OutputSpec.Upscale && s.Enhance == null) s.Enhance = true;   // upscaling is what AI enhance is for
            OutputChanged();
        }

        void SetUpscale(int f)
        {
            if (outSync || s == null || s.UpscaleFactor == f) return;
            s.UpscaleFactor = f;
            OutputChanged();
        }

        void SetCustomSize(int w, int h)
        {
            if (s == null) return;
            s.CustomW = Math.Max(16, Math.Min(16384, w));
            s.CustomH = Math.Max(16, Math.Min(16384, h));
            OutputChanged();
        }

        void ApplyTypedSize()
        {
            if (outSync || s == null || s.OutputMode != OutputSpec.Custom) return;
            int w, h;
            if (!int.TryParse(CustomWBox.Text.Trim(), out w) || !int.TryParse(CustomHBox.Text.Trim(), out h)) { UpdateOutputUi(); return; }
            if (w == s.CustomW && h == s.CustomH) return;
            SetCustomSize(w, h);
        }

        /// <summary>The output changed: new preview shape and frames, new AI enhance suggestion, new notes.</summary>
        void OutputChanged()
        {
            Touched();
            sync = true;
            EnhanceSw.IsChecked = s.EnhanceOn;
            EnhanceOpts.Visibility = EnhanceSw.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            sync = false;
            UpdateOutputUi();
            UpdateEnhanceNote();
            UpdateSaveAs();
            LoadFrames();
        }

        /// <summary>Shows the session's output choice and a line describing it.</summary>
        void UpdateOutputUi()
        {
            if (s == null) return;
            outSync = true;
            string mode = s.OutputMode ?? OutputSpec.HydroShift;
            OutPanel.IsChecked = mode == OutputSpec.HydroShift;
            OutOrig.IsChecked = mode == OutputSpec.Original;
            OutUp.IsChecked = mode == OutputSpec.Upscale;
            OutCustom.IsChecked = mode == OutputSpec.Custom;
            Up2.IsChecked = s.UpscaleFactor != 4;
            Up4.IsChecked = s.UpscaleFactor == 4;
            UpRow.Visibility = mode == OutputSpec.Upscale ? Visibility.Visible : Visibility.Collapsed;
            CustomRow.Visibility = mode == OutputSpec.Custom ? Visibility.Visible : Visibility.Collapsed;
            if (!CustomWBox.IsKeyboardFocused) CustomWBox.Text = s.CustomW > 0 ? s.CustomW.ToString() : "";
            if (!CustomHBox.IsKeyboardFocused) CustomHBox.Text = s.CustomH > 0 ? s.CustomH.ToString() : "";
            outSync = false;

            var o = s.Output;
            string what;
            if (s.Width <= 0) what = "";
            else if (mode == OutputSpec.Original) what = o.SizeText + "  ·  keeps the original size and shape";
            else if (mode == OutputSpec.Upscale)
                what = o.SizeText + "  ·  " + (s.UpscaleFactor == 4 ? 4 : 2) + "× larger, same shape" +
                       (EnhanceSw.IsChecked == true ? "" : ". Turn on AI enhance below for real detail instead of a plain stretch.");
            else if (mode == OutputSpec.Custom) what = o.SizeText + "  ·  fills this size, cropping to fit";
            else what = o.SizeText + "  ·  fills the HydroShift, cropping to fit" + (s.IsImage ? "" : ", under L-Connect's 30 MB limit");
            if (!s.IsImage && (double)o.W * o.H > 8192.0 * 4320) what += ". Very large for a video: it may not play everywhere.";
            if (s.IsImage) what += (what.Length > 0 ? ".  " : "") + "Saved as PNG.";
            OutNote.Text = what;
        }

        /// <summary>A still image has no timeline, motion or frame-to-frame noise cleanup.</summary>
        void ShowImageMode(bool image)
        {
            var v = image ? Visibility.Collapsed : Visibility.Visible;
            StripRow.Visibility = v;
            TrimRow.Visibility = v;
            MotionTitle.Visibility = v;
            LoopRow.Visibility = v;
            SmoothRow.Visibility = v;
            CleanRow.Visibility = v;
            LayoutPreview();
        }

        /// <summary>New preview frame size: buffers and the bitmap the HydroShift side is drawn into.</summary>
        void SetPreviewSize(int w, int h)
        {
            W = w; H = h;
            oledBmp = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null);
            buf = new byte[W * H * 4];
            blendBuf = new byte[W * H * 3];
            OledImg.Source = oledBmp;
            LayoutPreview();
        }

        /// <summary>Sizes the preview to the output's shape, as large as fits above the filmstrip.</summary>
        void LayoutPreview()
        {
            double availW = Left.ActualWidth, availH = Left.ActualHeight;
            if (availW <= 0 || availH <= 0) return;
            if (StripRow.Visibility == Visibility.Visible) availH -= 130;   // filmstrip, its margin and the trim line below
            double aspect = (double)H / W;
            double w = Math.Max(100, Math.Min(availW, Math.Max(100, availH) / aspect));
            PreviewHost.Width = Math.Floor(w);
            PreviewHost.Height = Math.Floor(w * aspect);
            UpdateSplit();
        }
    }
}
