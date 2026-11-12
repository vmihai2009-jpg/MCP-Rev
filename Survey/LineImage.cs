// Revit-free. Draws lines into a PNG with no imaging library (a plain RGB buffer, zlib through
// System.IO.Compression), so the section overlay works the same in Revit and in the tests.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public class LineImage
    {
        public static readonly (byte r, byte g, byte b) Ink = (40, 40, 40), Red = (220, 30, 30), Blue = (20, 90, 220), Grey = (170, 170, 170);

        private readonly int _width;
        private readonly List<(double x0, double y0, double x1, double y1, (byte, byte, byte) c, int w)> _lines = new List<(double, double, double, double, (byte, byte, byte), int)>();
        private readonly List<(double x, double y, double r, (byte, byte, byte) c)> _rings = new List<(double, double, double, (byte, byte, byte))>();

        /// <param name="widthPx">Image width; the height follows the drawing's proportions (at most 2.5 times the width).</param>
        public LineImage(int widthPx = 1600) { _width = Math.Clamp(widthPx, 64, 4000); }

        public int Count => _lines.Count;

        /// <summary>A line in drawing units (y up).</summary>
        public void Add(double x0, double y0, double x1, double y1, (byte, byte, byte) colour, int widthPx = 1)
        {
            if (double.IsFinite(x0) && double.IsFinite(y0) && double.IsFinite(x1) && double.IsFinite(y1)) _lines.Add((x0, y0, x1, y1, colour, Math.Clamp(widthPx, 1, 5)));
        }

        /// <summary>A circle of radius r drawing units round a point (to mark an issue).</summary>
        public void Ring(double x, double y, double r, (byte, byte, byte) colour) => _rings.Add((x, y, r, colour));

        public byte[] Png()
        {
            int w = _width, h;
            double minX = 0, maxX = 1, minY = 0, maxY = 1;
            var pts = _lines.SelectMany(l => new[] { (l.x0, l.y0), (l.x1, l.y1) }).Concat(_rings.Select(r => (r.x, r.y))).ToList();
            if (pts.Count > 0)
            {
                // the middle 99.6%, so one far-off line does not shrink the drawing to a dot
                var xs = pts.Select(p => p.Item1).OrderBy(v => v).ToList(); var ys = pts.Select(p => p.Item2).OrderBy(v => v).ToList();
                int lo = (int)(xs.Count * 0.002), hi = Math.Max(lo, xs.Count - 1 - lo);
                minX = xs[lo]; maxX = xs[hi]; minY = ys[lo]; maxY = ys[hi];
            }
            double spanX = Math.Max(1, maxX - minX), spanY = Math.Max(1, maxY - minY);
            double pad = 0.03;
            minX -= spanX * pad; maxX += spanX * pad; minY -= spanY * pad; maxY += spanY * pad;
            spanX = maxX - minX; spanY = maxY - minY;
            double k = (w - 1) / spanX;
            h = (int)Math.Ceiling(spanY * k) + 1;
            if (h > w * 2.5) { h = (int)(w * 2.5); k = (h - 1) / spanY; }
            h = Math.Max(h, 16);
            var px = new byte[w * h * 3];
            for (int i = 0; i < px.Length; i++) px[i] = 255;

            void Dot(int x, int y, (byte r, byte g, byte b) c, int size)
            {
                int half = size / 2;
                for (int dx = -half; dx <= size - 1 - half; dx++)
                    for (int dy = -half; dy <= size - 1 - half; dy++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int o = (yy * w + xx) * 3; px[o] = c.r; px[o + 1] = c.g; px[o + 2] = c.b;
                    }
            }
            (double, double) P(double x, double y) => ((x - minX) * k, (maxY - y) * k);
            void Seg(double ax, double ay, double bx, double by, (byte, byte, byte) c, int size)
            {
                var (x0, y0) = P(ax, ay); var (x1, y1) = P(bx, by);
                double len = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
                if (len > 20000) return;
                int n = Math.Max(1, (int)Math.Ceiling(len));
                for (int i = 0; i <= n; i++) Dot((int)Math.Round(x0 + (x1 - x0) * i / n), (int)Math.Round(y0 + (y1 - y0) * i / n), c, size);
            }
            // thin lines first, so the wider (model) lines stay on top
            foreach (var l in _lines.OrderBy(l => l.w)) Seg(l.x0, l.y0, l.x1, l.y1, l.c, l.w);
            foreach (var r in _rings)
                for (int i = 0; i < 48; i++)
                {
                    double a0 = 2 * Math.PI * i / 48, a1 = 2 * Math.PI * (i + 1) / 48;
                    Seg(r.x + r.r * Math.Cos(a0), r.y + r.r * Math.Sin(a0), r.x + r.r * Math.Cos(a1), r.y + r.r * Math.Sin(a1), r.c, 2);
                }
            return Encode(px, w, h);
        }

        /// <summary>RGB bytes (row by row, top first) as a PNG.</summary>
        public static byte[] Encode(byte[] rgb, int w, int h)
        {
            var raw = new byte[(w * 3 + 1) * h];
            for (int y = 0; y < h; y++) { raw[y * (w * 3 + 1)] = 0; Buffer.BlockCopy(rgb, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3); }
            byte[] z;
            using (var ms = new MemoryStream())
            {
                using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, true)) zs.Write(raw, 0, raw.Length);
                z = ms.ToArray();
            }
            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var ihdr = new byte[13];
                Be(ihdr, 0, (uint)w); Be(ihdr, 4, (uint)h); ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                Chunk(png, "IHDR", ihdr); Chunk(png, "IDAT", z); Chunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        private static void Be(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; Be(len, 0, (uint)data.Length); s.Write(len, 0, 4);
            var td = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
            Buffer.BlockCopy(data, 0, td, 4, data.Length);
            s.Write(td, 0, td.Length);
            var crc = new byte[4]; Be(crc, 0, Crc(td)); s.Write(crc, 0, 4);
        }

        private static uint[] _table;
        private static uint Crc(byte[] d)
        {
            if (_table == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
                _table = t;
            }
            uint crc = 0xFFFFFFFFu;
            foreach (var b in d) crc = _table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
