using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BarrGazetas.RevitMcp
{
    /// <summary>Ribbon icons drawn when Revit starts, so no image files are shipped.</summary>
    internal static class Icons
    {
        private static readonly Color On = Color.FromRgb(0x1E, 0x8E, 0x3E), Off = Color.FromRgb(0x5F, 0x63, 0x68), Brand = Color.FromRgb(0x00, 0x54, 0x93);

        /// <summary>The switch: green with "MCP" when on, grey when off.</summary>
        public static ImageSource Make(int size, bool on) =>
            Draw(size, on ? On : Off, size >= 32 ? "MCP" : "M", new FontFamily("Segoe UI"), size >= 32 ? size * 0.36 : size * 0.7, FontWeights.Bold);

        /// <summary>A symbol from Windows' own icon font on the Barr Gazetas blue.</summary>
        public static ImageSource Glyph(int size, string glyph) =>
            Draw(size, Brand, glyph, new FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons, Segoe UI Symbol"), size * 0.56, FontWeights.Normal);

        private static ImageSource Draw(int size, Color background, string text, FontFamily font, double fontSize, FontWeight weight)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                double r = size / 6.0;
                dc.DrawRoundedRectangle(new SolidColorBrush(background), null, new Rect(0.5, 0.5, size - 1, size - 1), r, r);
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(font, FontStyles.Normal, weight, FontStretches.Normal), fontSize, Brushes.White, 1.0);
                dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
