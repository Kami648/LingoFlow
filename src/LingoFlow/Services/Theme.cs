using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace LingoFlow
{
public sealed class ColorSettings
{
    public string Background { get; set; } = "#0C1020";
    public string Panel { get; set; } = "#141B30";
    public string Text { get; set; } = "#EEF1FF";
    public string Accent { get; set; } = "#8B9BFF";
    public string SubtitleBackground { get; set; } = "#0E1428";
    public string Original { get; set; } = "#ABB8D7";
    public string Translation { get; set; } = "#F0F3FF";
    public ColorSettings Copy() => (ColorSettings)MemberwiseClone();
    public static ColorSettings Light() => new() { Background = "#EFF2F8", Panel = "#FFFFFF", Text = "#20283C", Accent = "#4B59CB", SubtitleBackground = "#F7F9FF", Original = "#536176", Translation = "#182139" };
}
}

namespace LingoFlow.Services
{
    public static class Theme
    {
        public static readonly string[] Keys = ["Background", "Panel", "Text", "Accent", "SubtitleBackground", "Original", "Translation"];
        public static void Normalize(ColorSettings colors)
        {
            var defaults = new ColorSettings();
            foreach (string key in Keys)
            {
                var property = typeof(ColorSettings).GetProperty(key)!;
                if (property.GetValue(colors) is not string value || !Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$")) property.SetValue(colors, property.GetValue(defaults));
            }
        }
        public static Color Color(string value) => (Color)ColorConverter.ConvertFromString(value);
        public static SolidColorBrush Brush(string value) => new(Color(value));
        private static Color Mix(Color a, Color b, double amount) => System.Windows.Media.Color.FromRgb((byte)(a.R + (b.R - a.R) * amount), (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount));
        public static void Apply(ColorSettings colors)
        {
            Normalize(colors);
            var bg = Color(colors.Background); var panel = Color(colors.Panel); var text = Color(colors.Text); var accent = Color(colors.Accent);
            void Set(string key, Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); Application.Current.Resources[key] = brush; }
            Set("Bg", bg); Set("Panel", panel); Set("Text", text); Set("Accent", accent);
            Set("Muted", Mix(panel, text, .68)); Set("Border", Mix(panel, text, .20));
            Set("Sidebar", Mix(bg, panel, .65)); Set("Input", Mix(bg, panel, .4));
            Set("Inset", Mix(panel, accent, .12)); Set("Selected", Mix(panel, accent, .27));
            Set("OnAccent", accent.R * .299 + accent.G * .587 + accent.B * .114 > 160 ? System.Windows.Media.Color.FromRgb(12, 16, 35) : Colors.White);
            Set("OriginalColor", Color(colors.Original)); Set("TranslationColor", Color(colors.Translation));
        }
    }
}
