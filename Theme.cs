using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace OLEDable
{
    /// <summary>
    /// Light, Dark and True black themes plus the accent colour. Every brush the UI uses is an app-level resource
    /// referenced with DynamicResource, so switching replaces the brushes and the whole window recolours instantly.
    /// </summary>
    public static class Theme
    {
        static readonly Dictionary<string, Dictionary<string, string>> Palettes = new Dictionary<string, Dictionary<string, string>>
        {
            ["light"] = new Dictionary<string, string>
            {
                ["Bg"] = "#F6F6F8", ["Side"] = "#FFFFFF", ["Card"] = "#FFFFFF", ["Raised"] = "#F1F1F5", ["Hover"] = "#ECECF1",
                ["NavActive"] = "#EEE8FB", ["NavActiveText"] = "#4C1D95", ["Line"] = "#E4E4EA", ["Line2"] = "#D3D3DC", ["Dashed"] = "#C4C4CF",
                ["Text"] = "#16161B", ["Sub"] = "#5C5C6B", ["Btn2"] = "#FFFFFF", ["ChipText"] = "#3A3A46",
                ["ChipOnBg"] = "#16161B", ["ChipOnText"] = "#FFFFFF", ["Track"] = "#D3D3DC", ["Overlay"] = "#B3000000",
            },
            ["dark"] = new Dictionary<string, string>
            {
                ["Bg"] = "#0E0E11", ["Side"] = "#131318", ["Card"] = "#16161C", ["Raised"] = "#1F1F27", ["Hover"] = "#1C1C23",
                ["NavActive"] = "#1F1F27", ["NavActiveText"] = "#F2F2F5", ["Line"] = "#23232B", ["Line2"] = "#2E2E38", ["Dashed"] = "#34343F",
                ["Text"] = "#F2F2F5", ["Sub"] = "#A3A3B2", ["Btn2"] = "#17171D", ["ChipText"] = "#C8C8D2",
                ["ChipOnBg"] = "#F2F2F5", ["ChipOnText"] = "#0E0E11", ["Track"] = "#3A3A44", ["Overlay"] = "#B3000000",
            },
            ["black"] = new Dictionary<string, string>
            {
                ["Bg"] = "#000000", ["Side"] = "#000000", ["Card"] = "#0A0A0A", ["Raised"] = "#141414", ["Hover"] = "#101010",
                ["NavActive"] = "#161616", ["NavActiveText"] = "#EDEDED", ["Line"] = "#1A1A1A", ["Line2"] = "#2A2A2A", ["Dashed"] = "#2E2E2E",
                ["Text"] = "#EDEDED", ["Sub"] = "#9E9E9E", ["Btn2"] = "#000000", ["ChipText"] = "#BDBDBD",
                ["ChipOnBg"] = "#EDEDED", ["ChipOnText"] = "#000000", ["Track"] = "#2A2A2A", ["Overlay"] = "#B3000000",
            },
        };

        /// <summary>Accent → (bright, for rings/sliders; deep, for filled buttons; text on dark; text on light).</summary>
        static readonly Dictionary<string, string[]> Accents = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["#A970FF"] = new[] { "#A970FF", "#7C3AED", "#C9A6FF", "#6D28D9" },
            ["#FF4D5E"] = new[] { "#FF4D5E", "#C81E3A", "#FF9AA4", "#B4152F" },
            ["#22E0D0"] = new[] { "#22E0D0", "#0E7C74", "#7FEDE4", "#0B6B64" },
        };

        public static readonly string[] AccentChoices = { "#A970FF", "#FF4D5E", "#22E0D0" };

        public static string Current { get; private set; }
        public static event Action Changed;

        /// <summary>The theme to show for a setting: "follow Windows" maps to light or dark.</summary>
        public static string Effective(Settings s)
        {
            if (s.FollowWindows) return WindowsUsesLight() ? "light" : "dark";
            return s.Theme;
        }

        public static void Apply(string theme, string accent)
        {
            Dictionary<string, string> p;
            if (!Palettes.TryGetValue(theme ?? "", out p)) { theme = "dark"; p = Palettes["dark"]; }
            string[] a;
            if (!Accents.TryGetValue(accent ?? "", out a)) a = Accents["#A970FF"];
            var res = Application.Current.Resources;
            foreach (var kv in p) res[kv.Key] = Brush(kv.Value);
            res["Accent"] = Brush(a[0]);
            res["AccentDark"] = Brush(a[1]);
            res["AccentText"] = Brush(theme == "light" ? a[3] : a[2]);
            Current = theme;
            var h = Changed; if (h != null) h();
        }

        public static SolidColorBrush Brush(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public static bool WindowsUsesLight()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        /// <summary>Palette colours for drawing a theme's mini preview in Settings.</summary>
        public static Color Preview(string theme, string key)
        {
            return (Color)ColorConverter.ConvertFromString(Palettes[theme][key]);
        }
    }
}
