// Ported from the mcp-servers-for-revit survey pipeline (survey-pipeline.patch, base 86cf705). Revit-free.
using System;
using System.Collections.Generic;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>3D point in millimetres. Revit-independent so the survey pipeline can be unit tested.</summary>
    public struct Pt
    {
        public double X, Y, Z;
        public Pt(double x, double y, double z = 0) { X = x; Y = y; Z = z; }
        public double Dist2D(Pt o) { double dx = X - o.X, dy = Y - o.Y; return Math.Sqrt(dx * dx + dy * dy); }
        public double Dist3D(Pt o) { double dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z; return Math.Sqrt(dx * dx + dy * dy + dz * dz); }
        public bool IsFinite() { return IsOk(X) && IsOk(Y) && IsOk(Z); }
        private static bool IsOk(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
        public override string ToString() { return string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z); }
    }

    /// <summary>
    /// Dictionary key for a pair of integers (grid cell / segment pair). Packing two ints into one long is a trap:
    /// long.GetHashCode() XORs the halves, so neighbouring cells/indices collide massively. This hashes properly.
    /// </summary>
    public struct GridKey : IEquatable<GridKey>
    {
        public readonly long A, B;
        public GridKey(long a, long b) { A = a; B = b; }
        public bool Equals(GridKey o) { return A == o.A && B == o.B; }
        public override bool Equals(object obj) { return obj is GridKey && Equals((GridKey)obj); }
        public override int GetHashCode() { unchecked { return (int)(A * 73856093L) ^ (int)(B * 19349663L) ^ (int)((A * 83492791L) >> 16); } }
    }

    public enum CadKind { Line, Polyline, Point, Block }

    /// <summary>One CAD entity read from a DWG import/link (arcs/splines are tessellated to polylines by the reader).</summary>
    public class CadEntity
    {
        public int Index;
        public string Layer = "";
        public CadKind Kind;
        public List<Pt> Points = new List<Pt>();
        public bool Closed;
        /// <summary>For Kind=Block: the block (symbol) name.</summary>
        public string BlockName;
        /// <summary>For Kind=Block: rotation about Z in radians.</summary>
        public double RotationRad;
    }

    public struct Seg
    {
        public Pt A, B;
        public int Source;
        public Seg(Pt a, Pt b, int source) { A = a; B = b; Source = source; }
        public double Length2D { get { return A.Dist2D(B); } }
    }

    public static class GeoMath
    {
        public static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

        /// <summary>Signed area (positive = counter-clockwise) of a closed ring given WITHOUT repeated first point.</summary>
        public static double SignedArea(IList<Pt> ring)
        {
            double a = 0; int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                Pt p = ring[i], q = ring[(i + 1) % n];
                a += p.X * q.Y - q.X * p.Y;
            }
            return a / 2.0;
        }

        public static bool PointInPolygon(Pt p, IList<Pt> ring)
        {
            bool inside = false; int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Pt a = ring[i], b = ring[j];
                if (((a.Y > p.Y) != (b.Y > p.Y)) && (p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X))
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>True if segments ab and cd properly cross (shared endpoints and touching do NOT count).</summary>
        public static bool SegmentsCross(Pt a, Pt b, Pt c, Pt d)
        {
            double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static double Cross(Pt o, Pt a, Pt b) { return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X); }

        /// <summary>True if a closed ring (no repeated first point) intersects itself.</summary>
        public static bool RingSelfIntersects(IList<Pt> ring)
        {
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                Pt a = ring[i], b = ring[(i + 1) % n];
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue; // adjacent via wrap-around
                    Pt c = ring[j], d = ring[(j + 1) % n];
                    if (SegmentsCross(a, b, c, d)) return true;
                }
            }
            return false;
        }

        public static void BoundingBox(IEnumerable<Pt> pts, out Pt min, out Pt max)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;
            foreach (var p in pts)
            {
                if (!p.IsFinite()) continue;
                any = true;
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y; if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y; if (p.Z > maxZ) maxZ = p.Z;
            }
            if (!any) { min = new Pt(0, 0, 0); max = new Pt(0, 0, 0); return; }
            min = new Pt(minX, minY, minZ); max = new Pt(maxX, maxY, maxZ);
        }

        /// <summary>Stable, culture-independent key for a rounded coordinate (used for traceability keys).</summary>
        public static string Key(Pt p, double roundMm)
        {
            double r = roundMm <= 0 ? 1 : roundMm;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}_{1}_{2}",
                (long)Math.Round(p.X / r), (long)Math.Round(p.Y / r), (long)Math.Round(p.Z / r));
        }
    }
}
