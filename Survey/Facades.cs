// Revit-free. Finds the external facades: which walls have the outside on one side (rays from that side escape
// the building), then groups those on one line, facing the same way, into one run from the ground up. Each run gets
// a stable id (F1...) and its line ordered left to right as seen from outside.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BarrGazetas.RevitMcp.Survey
{
    public class FacadeWall
    {
        public long Id;
        public string Level;
        public Pt A, B;
        public double ThicknessMm, BaseZ, TopZ;
    }

    public class FacadeRun
    {
        public string Id;
        public List<long> WallIds = new List<long>();
        public List<string> Levels = new List<string>();
        /// <summary>The outer face line, left to right as seen from outside (model mm).</summary>
        public Pt A, B;
        /// <summary>Outward normal (model axes).</summary>
        public double OutX, OutY;
        public double LengthMm, BaseZ, TopZ;
        public string Name;
    }

    public static class Facades
    {
        private const int Rays = 16;
        private const double RayMm = 100000;

        /// <summary>
        /// The external walls of one floor and the side the outside is on: +1 when it is the wall's left (the side
        /// the normal (-dy, dx) points to), -1 the right. Walls with outside on both sides, or neither, are left out.
        /// </summary>
        public static Dictionary<long, int> OutsideSide(IList<FacadeWall> floor)
        {
            var result = new Dictionary<long, int>();
            foreach (var w in floor)
            {
                double L = w.A.Dist2D(w.B);
                if (L < 300) continue;
                double ux = (w.B.X - w.A.X) / L, uy = (w.B.Y - w.A.Y) / L, nx = -uy, ny = ux;
                int[] esc = new int[2];
                for (int k = 0; k < 2; k++)
                {
                    int side = k == 0 ? 1 : -1;
                    // three points along the wall, just off its face
                    foreach (double t in new[] { 0.25, 0.5, 0.75 })
                    {
                        double px = w.A.X + (w.B.X - w.A.X) * t + nx * side * (w.ThicknessMm / 2 + 300);
                        double py = w.A.Y + (w.B.Y - w.A.Y) * t + ny * side * (w.ThicknessMm / 2 + 300);
                        for (int r = 0; r < Rays; r++)
                        {
                            double ang = 2 * Math.PI * (r + 0.5) / Rays;
                            double dx = Math.Cos(ang), dy = Math.Sin(ang);
                            // only rays clearly away from this wall: one skimming along it would slip past its corner
                            if ((dx * nx + dy * ny) * side < 0.3) continue;
                            if (!floor.Any(o => o.Id != w.Id && Hits(px, py, dx, dy, o))) esc[k]++;
                        }
                    }
                }
                // escapes counted over 3 points x the rays pointing away from the wall
                // 3 points x about 6 rays a side: outside when a third escape, inside when nearly none do
                bool left = esc[0] >= 6, right = esc[1] >= 6;
                bool leftIn = esc[0] <= 3, rightIn = esc[1] <= 3;
                if (left && rightIn) result[w.Id] = 1;
                else if (right && leftIn) result[w.Id] = -1;
            }
            return result;
        }

        private static bool Hits(double px, double py, double dx, double dy, FacadeWall o)
        {
            // ray p + s d (0 < s < RayMm) against segment a + t (b - a), 0..1; walls given some width by trying both faces
            double ex = o.B.X - o.A.X, ey = o.B.Y - o.A.Y;
            double den = dx * ey - dy * ex;
            if (Math.Abs(den) < 1e-9) return false;
            double ax = o.A.X - px, ay = o.A.Y - py;
            double s = (ax * ey - ay * ex) / den, t = (ax * dy - ay * dx) / den;
            double pad = 150 / Math.Max(1, Math.Sqrt(ex * ex + ey * ey)); // walls meet at corners: close tiny gaps
            return s > 0 && s < RayMm && t >= -pad && t <= 1 + pad;
        }

        /// <summary>
        /// External walls of all floors grouped into runs: outward normals within 2 degrees, outer faces within
        /// 400 mm of one line, and less than 2 m apart along it. Runs shorter than minLengthMm are dropped.
        /// Ids F1... go round from grid north clockwise (northAngleRad turns model axes to grid: the project's angle).
        /// </summary>
        public static List<FacadeRun> Runs(IList<FacadeWall> walls, double northAngleRad = 0, double minLengthMm = 1500)
        {
            var ext = new List<(FacadeWall w, double nx, double ny, double c)>();
            foreach (var floor in walls.GroupBy(w => w.Level))
            {
                var list = floor.ToList();
                foreach (var kv in OutsideSide(list))
                {
                    var w = list.First(x => x.Id == kv.Key);
                    double L = w.A.Dist2D(w.B), nx = -(w.B.Y - w.A.Y) / L * kv.Value, ny = (w.B.X - w.A.X) / L * kv.Value;
                    double c = ((w.A.X + w.B.X) / 2) * nx + ((w.A.Y + w.B.Y) / 2) * ny + w.ThicknessMm / 2;
                    ext.Add((w, nx, ny, c));
                }
            }

            // union-find over "same facade line"
            int n = ext.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var a = ext[i]; var b = ext[j];
                    if (a.nx * b.nx + a.ny * b.ny < Math.Cos(2 * Math.PI / 180)) continue;
                    double cb = ((b.w.A.X + b.w.B.X) / 2) * a.nx + ((b.w.A.Y + b.w.B.Y) / 2) * a.ny + b.w.ThicknessMm / 2;
                    if (Math.Abs(cb - a.c) > 400) continue;
                    // along a's line (d = n turned -90 degrees): the two walls' spans, at most 2 m apart
                    double dx = a.ny, dy = -a.nx;
                    double a0 = Math.Min(a.w.A.X * dx + a.w.A.Y * dy, a.w.B.X * dx + a.w.B.Y * dy), a1 = Math.Max(a.w.A.X * dx + a.w.A.Y * dy, a.w.B.X * dx + a.w.B.Y * dy);
                    double b0 = Math.Min(b.w.A.X * dx + b.w.A.Y * dy, b.w.B.X * dx + b.w.B.Y * dy), b1 = Math.Max(b.w.A.X * dx + b.w.A.Y * dy, b.w.B.X * dx + b.w.B.Y * dy);
                    if (Math.Max(a0, b0) - Math.Min(a1, b1) > 2000) continue;
                    parent[Find(i)] = Find(j);
                }

            var runs = new List<FacadeRun>();
            foreach (var g in Enumerable.Range(0, n).GroupBy(Find))
            {
                var members = g.Select(i => ext[i]).ToList();
                // the run's normal and line: length-weighted
                double wsum = members.Sum(m => m.w.A.Dist2D(m.w.B));
                double nx = members.Sum(m => m.nx * m.w.A.Dist2D(m.w.B)) / wsum, ny = members.Sum(m => m.ny * m.w.A.Dist2D(m.w.B)) / wsum;
                double nl = Math.Sqrt(nx * nx + ny * ny); nx /= nl; ny /= nl;
                double c = members.Sum(m => (((m.w.A.X + m.w.B.X) / 2) * nx + ((m.w.A.Y + m.w.B.Y) / 2) * ny + m.w.ThicknessMm / 2) * m.w.A.Dist2D(m.w.B)) / wsum;
                double dx = -ny, dy = nx; // left to right seen from outside (looking along -n, right is (-ny, nx))
                var ts = members.SelectMany(m => new[] { m.w.A.X * dx + m.w.A.Y * dy, m.w.B.X * dx + m.w.B.Y * dy }).ToList();
                double t0 = ts.Min(), t1 = ts.Max();
                if (t1 - t0 < minLengthMm) continue;
                runs.Add(new FacadeRun
                {
                    WallIds = members.Select(m => m.w.Id).Distinct().ToList(),
                    Levels = members.Select(m => m.w.Level).Distinct().ToList(),
                    A = new Pt(nx * c + dx * t0, ny * c + dy * t0), B = new Pt(nx * c + dx * t1, ny * c + dy * t1),
                    OutX = nx, OutY = ny, LengthMm = Math.Round(t1 - t0),
                    BaseZ = members.Min(m => m.w.BaseZ), TopZ = members.Max(m => m.w.TopZ),
                });
            }

            // stable ids: clockwise from grid north by outward bearing (5 degree bins), then left to right
            int k = 1;
            foreach (var r in runs.OrderBy(r => Math.Round(Bearing(r.OutX, r.OutY, northAngleRad) / 5)).ThenBy(r => r.A.X * r.OutY - r.A.Y * r.OutX))
                r.Id = "F" + k++;
            return runs.OrderBy(r => int.Parse(r.Id.Substring(1))).ToList();
        }

        /// <summary>Bearing of a model direction from grid north, degrees clockwise, after turning model axes by the project angle.</summary>
        public static double Bearing(double x, double y, double northAngleRad)
        {
            double c = Math.Cos(northAngleRad), s = Math.Sin(northAngleRad);
            double e = x * c - y * s, nn = x * s + y * c;
            double deg = Math.Atan2(e, nn) * 180 / Math.PI;
            return (deg + 360) % 360;
        }

        public static string Compass(double bearing)
        {
            string[] names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
            return names[(int)Math.Round(bearing / 45) % 8];
        }

        // a street name: words ending in a street word ("Swallow Place", "REGENT STREET"); not drawing notes such as
        // "Basement Yard" or "Security Mesh Over Basement Yard"
        private static readonly Regex Street = new Regex(@"^\s*[A-Za-z'\.\- ]{2,40}\s(street|st|road|rd|place|pl|lane|mews|square|sq|row|avenue|ave|terrace|gardens|walk|hill|way|passage|alley|close|crescent)\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NotStreet = new Regex(@"\b(basement|mesh|over|light|well|roof|plant|store|bin|bike|cycle|stair|lift|void|level|floor)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool LooksLikeStreet(string s) => !string.IsNullOrWhiteSpace(s) && Street.IsMatch(s) && !NotStreet.IsMatch(s) && !s.Any(char.IsDigit);

        /// <summary>A street name from the drawings: text naming a street, outside the run and within 25 m of it.</summary>
        public static string StreetNear(FacadeRun r, IEnumerable<DwgText> texts)
        {
            double mx = (r.A.X + r.B.X) / 2, my = (r.A.Y + r.B.Y) / 2, dx = -r.OutY, dy = r.OutX;
            var best = texts.Where(t => LooksLikeStreet(t.Value))
                .Select(t => (t, outDist: (t.At.X - mx) * r.OutX + (t.At.Y - my) * r.OutY, along: Math.Abs((t.At.X - mx) * dx + (t.At.Y - my) * dy)))
                .Where(x => x.outDist > 0 && x.outDist < 25000 && x.along < r.LengthMm / 2 + 10000)
                .OrderBy(x => x.outDist + x.along / 2).FirstOrDefault();
            if (best.t == null) return null;
            var name = Regex.Replace(best.t.Value.Trim(), @"\s+", " ");
            return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        }
    }
}
