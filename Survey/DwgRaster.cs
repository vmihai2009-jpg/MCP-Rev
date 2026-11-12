// Revit-free. 1.12.0 (dwg_image): a picture of part of a DWG, so Claude can look at a window, a bay or a detail
// instead of reading thousands of line coordinates. Lines and texts are drawn into an RGB canvas (lines clipped to the
// view, texts in a small built-in 5x7 font), with rulers and a light grid in drawing (or model) units so things can be
// measured off the picture, and optional red rectangles (the model's windows over an elevation). Encoded as a PNG
// here too (zlib from the framework, no imaging library), so it runs and is tested the same in and out of Revit.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>What to draw: the box in drawing mm (x to the right, y up), the picture's width, how to label the rulers.</summary>
    public class RasterSpec
    {
        public double X0, Y0, X1, Y1;
        public int WidthPx = 1200;
        public int MaxHeightPx = 2000;
        /// <summary>Ruler labels; default: the drawing value in mm.</summary>
        public Func<double, string> XLabel, YLabel;
        public string XTitle, YTitle;
        public bool Grid = true;
        /// <summary>Layers drawn in blue instead of black (e.g. the window layer).</summary>
        public Func<string, bool> Highlight;
        /// <summary>Line thickness in px (1 or 2).</summary>
        public int LinePx = 1;
    }

    /// <summary>A rectangle drawn over the drawing in red (in the same mm as the box), with an optional label.</summary>
    public class RasterMark
    {
        public double X0, Y0, X1, Y1;
        public string Label;
    }

    public class RasterResult
    {
        public byte[] Png;
        public int Width, Height;
        /// <summary>Drawing mm per pixel.</summary>
        public double MmPerPx;
        /// <summary>Pixel of the box's left (x0) and top (y1) corner: px = Left + (x - x0) / MmPerPx, py = Top + (y1 - y) / MmPerPx.</summary>
        public int Left, Top;
        public int LinesDrawn, TextsDrawn, TextsTooSmall;
        public double X0, Y1;
        /// <summary>The raw RGB pixels (row by row, 3 bytes each), for tests.</summary>
        public byte[] Rgb;
        public (int px, int py) ToPx(double x, double y) => ((int)Math.Round(Left + (x - X0) / MmPerPx), (int)Math.Round(Top + (Y1 - y) / MmPerPx));
    }

    public static class DwgRaster
    {
        private static readonly byte[] Black = { 20, 20, 20 }, Blue = { 20, 70, 200 }, Red = { 220, 30, 30 }, TextBlue = { 30, 60, 140 }, GridGrey = { 228, 228, 228 }, RulerGrey = { 90, 90, 90 };
        private const int MarginLeft = 64, MarginBottom = 30, MarginTop = 6, MarginRight = 10;

        public static RasterResult Render(IEnumerable<DwgLine> lines, IEnumerable<DwgText> texts, RasterSpec spec, IEnumerable<RasterMark> marks = null)
        {
            double x0 = Math.Min(spec.X0, spec.X1), x1 = Math.Max(spec.X0, spec.X1), y0 = Math.Min(spec.Y0, spec.Y1), y1 = Math.Max(spec.Y0, spec.Y1);
            if (!(x1 - x0 > 0.001) || !(y1 - y0 > 0.001)) throw new ArgumentException("The box to draw has no size.");
            int wantW = Math.Clamp(spec.WidthPx, 200, 4096), maxH = Math.Clamp(spec.MaxHeightPx, 200, 4096);
            double mmPerPx = Math.Max((x1 - x0) / (wantW - MarginLeft - MarginRight), (y1 - y0) / (maxH - MarginTop - MarginBottom));
            int plotW = Math.Max(1, (int)Math.Ceiling((x1 - x0) / mmPerPx - 1e-6)), plotH = Math.Max(1, (int)Math.Ceiling((y1 - y0) / mmPerPx - 1e-6));
            int W = plotW + MarginLeft + MarginRight, H = plotH + MarginTop + MarginBottom;
            var c = new Canvas(W, H);
            var r = new RasterResult { Width = W, Height = H, MmPerPx = mmPerPx, Left = MarginLeft, Top = MarginTop, X0 = x0, Y1 = y1 };
            double Px(double x) => MarginLeft + (x - x0) / mmPerPx;
            double Py(double y) => MarginTop + (y1 - y) / mmPerPx;
            int plotL = MarginLeft, plotR = MarginLeft + plotW, plotT = MarginTop, plotB = MarginTop + plotH;

            // grid and rulers
            double step = NiceStep(Math.Max(x1 - x0, y1 - y0) / 10);
            Func<double, string> xl = spec.XLabel ?? (v => v.ToString("0", CultureInfo.InvariantCulture));
            Func<double, string> yl = spec.YLabel ?? (v => v.ToString("0", CultureInfo.InvariantCulture));
            for (double v = Math.Ceiling(x0 / step) * step; v <= x1 + 1e-9; v += step)
            {
                int px = (int)Math.Round(Px(v));
                if (spec.Grid) c.Line(px, plotT, px, plotB, GridGrey, 1, plotL, plotT, plotR, plotB);
                c.Line(px, plotB, px, plotB + 4, RulerGrey, 1);
                string s = xl(v + 0.0);
                c.Text(px - Font.Width(s, 1) / 2, plotB + 7, s, 1, RulerGrey);
            }
            for (double v = Math.Ceiling(y0 / step) * step; v <= y1 + 1e-9; v += step)
            {
                int py = (int)Math.Round(Py(v));
                if (spec.Grid) c.Line(plotL, py, plotR, py, GridGrey, 1, plotL, plotT, plotR, plotB);
                c.Line(plotL - 4, py, plotL, py, RulerGrey, 1);
                string s = yl(v + 0.0);
                c.Text(Math.Max(1, plotL - 6 - Font.Width(s, 1)), py - 3, s, 1, RulerGrey);
            }
            if (spec.XTitle != null) c.Text(plotR - Font.Width(spec.XTitle, 1), plotB + 18, spec.XTitle, 1, RulerGrey);
            if (spec.YTitle != null) c.Text(2, Math.Max(0, plotT), spec.YTitle, 1, RulerGrey);
            c.Rect(plotL, plotT, plotR, plotB, RulerGrey);

            // the drawing
            int t = Math.Clamp(spec.LinePx, 1, 3);
            foreach (var l in lines ?? Enumerable.Empty<DwgLine>())
            {
                if (Math.Max(l.A.X, l.B.X) < x0 || Math.Min(l.A.X, l.B.X) > x1 || Math.Max(l.A.Y, l.B.Y) < y0 || Math.Min(l.A.Y, l.B.Y) > y1) continue;
                var col = spec.Highlight != null && spec.Highlight(l.Layer) ? Blue : Black;
                if (c.LineF(Px(l.A.X), Py(l.A.Y), Px(l.B.X), Py(l.B.Y), col, t, plotL, plotT, plotR, plotB)) r.LinesDrawn++;
            }
            foreach (var tx in texts ?? Enumerable.Empty<DwgText>())
            {
                if (tx.At.X < x0 || tx.At.X > x1 || tx.At.Y < y0 || tx.At.Y > y1 || string.IsNullOrWhiteSpace(tx.Value)) continue;
                double hPx = (tx.HeightMm > 0 ? tx.HeightMm : 2.5) / mmPerPx;
                if (hPx < 4) { r.TextsTooSmall++; continue; }
                int scale = Math.Max(1, (int)Math.Round(hPx / 7));
                string s = tx.Value.Replace("\n", " ").Trim();
                if (s.Length > 40) s = s.Substring(0, 40);
                // a DWG text's insertion point is usually its bottom left
                c.Text((int)Math.Round(Px(tx.At.X)), (int)Math.Round(Py(tx.At.Y)) - 7 * scale, s, scale, TextBlue, plotL, plotT, plotR, plotB);
                r.TextsDrawn++;
            }
            foreach (var m in marks ?? Enumerable.Empty<RasterMark>())
            {
                int a = (int)Math.Round(Px(Math.Min(m.X0, m.X1))), b = (int)Math.Round(Py(Math.Max(m.Y0, m.Y1)));
                int e = (int)Math.Round(Px(Math.Max(m.X0, m.X1))), d = (int)Math.Round(Py(Math.Min(m.Y0, m.Y1)));
                c.Line(a, b, e, b, Red, 2, plotL, plotT, plotR, plotB); c.Line(e, b, e, d, Red, 2, plotL, plotT, plotR, plotB);
                c.Line(e, d, a, d, Red, 2, plotL, plotT, plotR, plotB); c.Line(a, d, a, b, Red, 2, plotL, plotT, plotR, plotB);
                if (!string.IsNullOrEmpty(m.Label)) c.Text(a + 3, b + 3, m.Label, 1, Red, plotL, plotT, plotR, plotB);
            }
            r.Rgb = c.Rgb;
            r.Png = Png.Encode(W, H, c.Rgb);
            return r;
        }

        /// <summary>1, 2 or 5 times a power of ten, at or above the value.</summary>
        public static double NiceStep(double raw)
        {
            if (!(raw > 0)) return 1;
            double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            foreach (var k in new[] { 1.0, 2, 5, 10 }) if (k * p >= raw) return k * p;
            return 10 * p;
        }

        private sealed class Canvas
        {
            public readonly int W, H;
            public readonly byte[] Rgb;
            public Canvas(int w, int h) { W = w; H = h; Rgb = new byte[w * h * 3]; for (int i = 0; i < Rgb.Length; i++) Rgb[i] = 255; }

            public void Set(int x, int y, byte[] col, int cl = 0, int ct = 0, int cr = int.MaxValue, int cb = int.MaxValue)
            {
                if (x < 0 || y < 0 || x >= W || y >= H || x < cl || x > cr || y < ct || y > cb) return;
                int i = (y * W + x) * 3; Rgb[i] = col[0]; Rgb[i + 1] = col[1]; Rgb[i + 2] = col[2];
            }

            public void Rect(int a, int b, int c, int d, byte[] col) { Line(a, b, c, b, col, 1); Line(c, b, c, d, col, 1); Line(c, d, a, d, col, 1); Line(a, d, a, b, col, 1); }

            public void Line(int xa, int ya, int xb, int yb, byte[] col, int t, int cl = 0, int ct = 0, int cr = int.MaxValue, int cb = int.MaxValue) =>
                LineF(xa, ya, xb, yb, col, t, cl, ct, cr, cb);

            /// <summary>A line clipped to the box (Liang-Barsky), then stepped pixel by pixel. False when nothing of it is inside.</summary>
            public bool LineF(double xa, double ya, double xb, double yb, byte[] col, int t, int cl, int ct, int cr, int cb)
            {
                double l = Math.Max(cl, 0), tp = Math.Max(ct, 0), rr = Math.Min(cr, W - 1), bb = Math.Min(cb, H - 1);
                double dx = xb - xa, dy = yb - ya, u0 = 0, u1 = 1;
                bool Clip(double p, double q)
                {
                    if (Math.Abs(p) < 1e-12) return q >= 0;
                    double u = q / p;
                    if (p < 0) { if (u > u1) return false; if (u > u0) u0 = u; }
                    else { if (u < u0) return false; if (u < u1) u1 = u; }
                    return true;
                }
                if (!Clip(-dx, xa - l) || !Clip(dx, rr - xa) || !Clip(-dy, ya - tp) || !Clip(dy, bb - ya)) return false;
                int x = (int)Math.Round(xa + u0 * dx), y = (int)Math.Round(ya + u0 * dy), x2 = (int)Math.Round(xa + u1 * dx), y2 = (int)Math.Round(ya + u1 * dy);
                int sx = x < x2 ? 1 : -1, sy = y < y2 ? 1 : -1, ex = Math.Abs(x2 - x), ey = -Math.Abs(y2 - y), err = ex + ey;
                int lo = -(t - 1) / 2, hi = t / 2;
                while (true)
                {
                    for (int i = lo; i <= hi; i++) for (int j = lo; j <= hi; j++) Set(x + i, y + j, col, cl, ct, cr, cb);
                    if (x == x2 && y == y2) break;
                    int e2 = 2 * err;
                    if (e2 >= ey) { err += ey; x += sx; }
                    if (e2 <= ex) { err += ex; y += sy; }
                }
                return true;
            }

            public void Text(int x, int y, string s, int scale, byte[] col, int cl = 0, int ct = 0, int cr = int.MaxValue, int cb = int.MaxValue)
            {
                foreach (char ch in s)
                {
                    var g = Font.Glyph(ch);
                    for (int row = 0; row < 7; row++)
                        for (int bit = 0; bit < 5; bit++)
                            if ((g[row] & (1 << (4 - bit))) != 0)
                                for (int i = 0; i < scale; i++) for (int j = 0; j < scale; j++) Set(x + bit * scale + i, y + row * scale + j, col, cl, ct, cr, cb);
                    x += 6 * scale;
                }
            }
        }

        /// <summary>A 5x7 font: digits, capitals (small letters drawn as capitals) and the punctuation survey labels use.</summary>
        internal static class Font
        {
            private static readonly Dictionary<char, byte[]> Glyphs = Build();
            public static int Width(string s, int scale) => s.Length * 6 * scale - scale;

            public static byte[] Glyph(char ch)
            {
                if (Glyphs.TryGetValue(char.ToUpperInvariant(ch), out var g)) return g;
                return Glyphs['\u0000'];
            }

            private static Dictionary<char, byte[]> Build()
            {
                var rows = new Dictionary<char, string>
                {
                    ['0'] = "01110 10001 10011 10101 11001 10001 01110", ['1'] = "00100 01100 00100 00100 00100 00100 01110",
                    ['2'] = "01110 10001 00001 00010 00100 01000 11111", ['3'] = "11111 00010 00100 00010 00001 10001 01110",
                    ['4'] = "00010 00110 01010 10010 11111 00010 00010", ['5'] = "11111 10000 11110 00001 00001 10001 01110",
                    ['6'] = "00110 01000 10000 11110 10001 10001 01110", ['7'] = "11111 00001 00010 00100 01000 01000 01000",
                    ['8'] = "01110 10001 10001 01110 10001 10001 01110", ['9'] = "01110 10001 10001 01111 00001 00010 01100",
                    ['A'] = "01110 10001 10001 11111 10001 10001 10001", ['B'] = "11110 10001 10001 11110 10001 10001 11110",
                    ['C'] = "01110 10001 10000 10000 10000 10001 01110", ['D'] = "11100 10010 10001 10001 10001 10010 11100",
                    ['E'] = "11111 10000 10000 11110 10000 10000 11111", ['F'] = "11111 10000 10000 11110 10000 10000 10000",
                    ['G'] = "01110 10001 10000 10111 10001 10001 01111", ['H'] = "10001 10001 10001 11111 10001 10001 10001",
                    ['I'] = "01110 00100 00100 00100 00100 00100 01110", ['J'] = "00111 00010 00010 00010 00010 10010 01100",
                    ['K'] = "10001 10010 10100 11000 10100 10010 10001", ['L'] = "10000 10000 10000 10000 10000 10000 11111",
                    ['M'] = "10001 11011 10101 10101 10001 10001 10001", ['N'] = "10001 10001 11001 10101 10011 10001 10001",
                    ['O'] = "01110 10001 10001 10001 10001 10001 01110", ['P'] = "11110 10001 10001 11110 10000 10000 10000",
                    ['Q'] = "01110 10001 10001 10001 10101 10010 01101", ['R'] = "11110 10001 10001 11110 10100 10010 10001",
                    ['S'] = "01111 10000 10000 01110 00001 00001 11110", ['T'] = "11111 00100 00100 00100 00100 00100 00100",
                    ['U'] = "10001 10001 10001 10001 10001 10001 01110", ['V'] = "10001 10001 10001 10001 10001 01010 00100",
                    ['W'] = "10001 10001 10001 10101 10101 10101 01010", ['X'] = "10001 10001 01010 00100 01010 10001 10001",
                    ['Y'] = "10001 10001 10001 01010 00100 00100 00100", ['Z'] = "11111 00001 00010 00100 01000 10000 11111",
                    [' '] = "00000 00000 00000 00000 00000 00000 00000", ['.'] = "00000 00000 00000 00000 00000 01100 01100",
                    [','] = "00000 00000 00000 00000 01100 00100 01000", ['-'] = "00000 00000 00000 11111 00000 00000 00000",
                    ['+'] = "00000 00100 00100 11111 00100 00100 00000", [':'] = "00000 01100 01100 00000 01100 01100 00000",
                    ['/'] = "00000 00001 00010 00100 01000 10000 00000", ['('] = "00010 00100 01000 01000 01000 00100 00010",
                    [')'] = "01000 00100 00010 00010 00010 00100 01000", ['%'] = "11000 11001 00010 00100 01000 10011 00011",
                    ['='] = "00000 00000 11111 00000 11111 00000 00000", ['\''] = "01100 00100 01000 00000 00000 00000 00000",
                    ['"'] = "01010 01010 00000 00000 00000 00000 00000", ['#'] = "01010 01010 11111 01010 11111 01010 01010",
                    ['_'] = "00000 00000 00000 00000 00000 00000 11111", ['*'] = "00000 00100 10101 01110 10101 00100 00000",
                    ['°'] = "01100 10010 10010 01100 00000 00000 00000", ['<'] = "00010 00100 01000 10000 01000 00100 00010",
                    ['>'] = "01000 00100 00010 00001 00010 00100 01000", ['['] = "01110 01000 01000 01000 01000 01000 01110",
                    [']'] = "01110 00010 00010 00010 00010 00010 01110", ['&'] = "01100 10010 10100 01000 10101 10010 01101",
                    ['\u0000'] = "11111 10001 10001 10001 10001 10001 11111",
                };
                return rows.ToDictionary(kv => kv.Key, kv => kv.Value.Split(' ').Select(r => Convert.ToByte(r, 2)).ToArray());
            }
        }
    }

    /// <summary>A plain RGB PNG: signature, IHDR, one zlib IDAT (filter 0 on every row), IEND.</summary>
    public static class Png
    {
        private static readonly uint[] CrcTable = MakeTable();

        public static byte[] Encode(int width, int height, byte[] rgb)
        {
            if (rgb.Length != width * height * 3) throw new ArgumentException("rgb is not width x height x 3 bytes");
            using var ms = new MemoryStream();
            ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            var ihdr = new byte[13];
            Be(ihdr, 0, (uint)width); Be(ihdr, 4, (uint)height);
            ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
            Chunk(ms, "IHDR", ihdr);
            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
                    for (int y = 0; y < height; y++) { z.WriteByte(0); z.Write(rgb, y * width * 3, width * 3); }
                Chunk(ms, "IDAT", raw.ToArray());
            }
            Chunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        /// <summary>The RGB pixels back from a PNG this class wrote (for tests).</summary>
        public static (int w, int h, byte[] rgb) Decode(byte[] png)
        {
            int pos = 8, w = 0, h = 0;
            var idat = new MemoryStream();
            while (pos < png.Length)
            {
                int len = (int)Rd(png, pos); string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                if (type == "IHDR") { w = (int)Rd(png, pos + 8); h = (int)Rd(png, pos + 12); }
                if (type == "IDAT") idat.Write(png, pos + 8, len);
                pos += 12 + len;
            }
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            using var outp = new MemoryStream();
            z.CopyTo(outp);
            var rows = outp.ToArray();
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(rows, y * (w * 3 + 1) + 1, rgb, y * w * 3, w * 3);
            return (w, h, rgb);
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; Be(len, 0, (uint)data.Length); s.Write(len, 0, 4);
            var td = new byte[4 + data.Length];
            System.Text.Encoding.ASCII.GetBytes(type, 0, 4, td, 0);
            Buffer.BlockCopy(data, 0, td, 4, data.Length);
            s.Write(td, 0, td.Length);
            var crc = new byte[4]; Be(crc, 0, Crc(td)); s.Write(crc, 0, 4);
        }

        private static void Be(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        private static uint Rd(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

        public static uint Crc(byte[] data)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFF;
        }

        private static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }
    }
}
