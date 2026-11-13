// Revit-free. 1.30.0: what a drawing's lines are, from their geometry alone, not their layer names (surveyors name layers
// differently; the shapes are the same everywhere). Wall faces come in parallel pairs a wall's thickness apart; hatching is
// a family of many parallel lines at a steady close pitch; furniture and fittings are small closed shapes on their own;
// ticks and arrows are tiny. Per layer the shares say what the layer holds, so a layer named for walls that holds hatching
// (or a wall layer named something else) is found and can be set right in the job's layers.txt.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public class LayerRoleGuess
    {
        public string Layer, Guess;
        public int Lines;
        public double LengthM;
        public Dictionary<string, double> Shares = new Dictionary<string, double>();
    }

    public static class LineRoles
    {
        public const string WallFace = "wall face", Hatch = "hatch", Fitting = "furniture/fitting", Tick = "tick/annotation", Other = "other";

        public static string[] Classify(IList<DwgLine> lines, double minWallMm = 80, double maxWallMm = 800, double hatchPitchMm = 400)
        {
            int n = lines.Count;
            var role = new string[n];
            var ang = new double[n]; var len = new double[n]; var off = new double[n]; var t0 = new double[n]; var t1 = new double[n];
            for (int i = 0; i < n; i++)
            {
                var l = lines[i]; double dx = l.B.X - l.A.X, dy = l.B.Y - l.A.Y;
                len[i] = Math.Sqrt(dx * dx + dy * dy);
                double a = Math.Atan2(dy, dx); if (a < 0) a += Math.PI; if (a >= Math.PI) a -= Math.PI; ang[i] = a;
                double ux = Math.Cos(a), uy = Math.Sin(a);
                off[i] = -uy * l.A.X + ux * l.A.Y;
                double s0 = ux * l.A.X + uy * l.A.Y, s1 = ux * l.B.X + uy * l.B.Y; t0[i] = Math.Min(s0, s1); t1[i] = Math.Max(s0, s1);
                if (len[i] < 50) role[i] = Tick;
            }
            // parallel families: 1 degree buckets (with the neighbours), sorted by offset
            var buckets = Enumerable.Range(0, n).Where(i => role[i] == null).GroupBy(i => (int)Math.Floor(ang[i] * 180 / Math.PI) % 180).ToDictionary(g => g.Key, g => g.ToList());
            var pair = new bool[n]; var hatch = new bool[n];
            foreach (var kv in buckets)
            {
                var fam = new List<int>(kv.Value);
                foreach (var nb in new[] { (kv.Key + 1) % 180, (kv.Key + 179) % 180 }) if (buckets.TryGetValue(nb, out var x)) fam.AddRange(x);
                var mine = new HashSet<int>(kv.Value);
                fam = fam.Where(i => Math.Abs(Wrap(ang[i] - ang[kv.Value[0]])) <= 1.5 * Math.PI / 180).OrderBy(i => off[i]).ToList();
                for (int p = 0; p < fam.Count; p++)
                {
                    int i = fam[p]; if (!mine.Contains(i)) continue;
                    int close = 0; var pitches = new List<double>();
                    for (int q = p + 1; q < fam.Count && off[fam[q]] - off[i] <= maxWallMm; q++) Look(fam[q]);
                    for (int q = p - 1; q >= 0 && off[i] - off[fam[q]] <= maxWallMm; q--) Look(fam[q]);
                    void Look(int j)
                    {
                        double d = Math.Abs(off[j] - off[i]);
                        double ov = Math.Min(t1[i], t1[j]) - Math.Max(t0[i], t0[j]);
                        if (ov < 0.5 * Math.Min(len[i], len[j])) return;
                        if (d >= minWallMm && len[i] >= 300) pair[i] = true;
                        if (d > 1 && d <= hatchPitchMm) { close++; pitches.Add(d); }
                    }
                    // hatching: four or more neighbours at whole multiples of one small pitch
                    if (close >= 4)
                    {
                        double pitch = pitches.Min();
                        if (pitch >= 20 && pitches.Count(d => Math.Abs(d / pitch - Math.Round(d / pitch)) < 0.08) >= 0.8 * pitches.Count) hatch[i] = true;
                    }
                }
            }
            // small closed shapes on their own: clusters joined end to end whose extent is small
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            var ends = new Dictionary<(long, long), List<int>>();
            for (int i = 0; i < n; i++)
                foreach (var p in new[] { lines[i].A, lines[i].B })
                {
                    var k = ((long)Math.Round(p.X / 5), (long)Math.Round(p.Y / 5));
                    if (!ends.TryGetValue(k, out var l)) ends[k] = l = new List<int>();
                    foreach (var j in l) { int a = Find(i), b = Find(j); if (a != b) parent[a] = b; }
                    l.Add(i);
                }
            var clusters = Enumerable.Range(0, n).GroupBy(Find).ToList();
            foreach (var c in clusters)
            {
                var ids = c.ToList();
                double w = ids.Max(i => Math.Max(lines[i].A.X, lines[i].B.X)) - ids.Min(i => Math.Min(lines[i].A.X, lines[i].B.X));
                double h = ids.Max(i => Math.Max(lines[i].A.Y, lines[i].B.Y)) - ids.Min(i => Math.Min(lines[i].A.Y, lines[i].B.Y));
                bool closed = ids.Count >= 3 && ids.SelectMany(i => new[] { lines[i].A, lines[i].B }).GroupBy(p => ((long)Math.Round(p.X / 5), (long)Math.Round(p.Y / 5))).All(g => g.Count() >= 2);
                if (closed && Math.Max(w, h) <= 2500 && ids.Count(i => pair[i]) <= ids.Count / 2)
                    foreach (var i in ids) if (role[i] == null) role[i] = Fitting;
            }
            for (int i = 0; i < n; i++)
                if (role[i] == null) role[i] = hatch[i] ? Hatch : pair[i] ? WallFace : Other;
            return role;
        }

        public static List<LayerRoleGuess> ByLayer(IList<DwgLine> lines, string[] roles, double minShare = 0.5)
        {
            return Enumerable.Range(0, lines.Count).GroupBy(i => lines[i].Layer ?? "").Select(g =>
            {
                double tot = g.Sum(i => lines[i].A.Dist2D(lines[i].B));
                var sh = g.GroupBy(i => roles[i]).ToDictionary(x => x.Key, x => Math.Round(x.Sum(i => lines[i].A.Dist2D(lines[i].B)) / Math.Max(1e-9, tot), 2));
                var top = sh.OrderByDescending(x => x.Value).First();
                return new LayerRoleGuess { Layer = g.Key, Lines = g.Count(), LengthM = Math.Round(tot / 1000, 1), Shares = sh, Guess = top.Value >= minShare ? top.Key : "mixed" };
            }).OrderByDescending(x => x.LengthM).ToList();
        }

        private static double Wrap(double a) { while (a > Math.PI / 2) a -= Math.PI; while (a < -Math.PI / 2) a += Math.PI; return a; }
    }
}
