// Revit-free. 1.21.0: curves drawn as many short straight pieces. Surveys often draw a curved wall, a bow window or an
// arch as a chain of short lines (or the reader breaks a true curve into chords). Built as they are, they become a
// staircase of short walls and short pieces of detail that nobody can edit as a curve. This finds such chains, in any
// 2D frame (plan x/y or a facade's u/z), and fits one circular arc to each run that stays on a circle, so the tools
// can build one curved element instead.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public class CurveChain
    {
        public double Cx, Cy, R, Start, Sweep, RmsMm, MaxDevMm;
        /// <summary>Indices of the pieces (in the list given) that make up the arc, in order along it.</summary>
        public List<int> Members = new List<int>();
        public double Length => R * Sweep;
        public double SweepDeg => Sweep * 180 / Math.PI;
        public Pt PointAt(double t) => new Pt(Cx + R * Math.Cos(Start + Sweep * t), Cy + R * Math.Sin(Start + Sweep * t));
    }

    public class CurveChainOptions
    {
        /// <summary>Pieces longer than this are straight lines in their own right, never part of a curve (mm).</summary>
        public double MaxPieceMm = 2000;
        /// <summary>Ends closer than this are joined (mm).</summary>
        public double JoinMm = 2;
        /// <summary>Every vertex within this of the fitted circle (mm).</summary>
        public double TolMm = 5;
        /// <summary>Turn at each joint, degrees: below MinTurn the pieces are one straight line, above MaxTurn a corner.</summary>
        public double MinTurnDeg = 0.1, MaxTurnDeg = 25;
        public int MinPieces = 3;
        public double MinSweepDeg = 10, MinRadiusMm = 150, MaxRadiusMm = 200000;
    }

    public static class CurveChains
    {
        public static List<CurveChain> Find(IList<(Pt a, Pt b)> pieces, CurveChainOptions o = null)
        {
            o ??= new CurveChainOptions();
            var res = new List<CurveChain>();
            var cand = Enumerable.Range(0, pieces.Count).Where(i => { double l = pieces[i].a.Dist2D(pieces[i].b); return l >= 1 && l <= o.MaxPieceMm; }).ToList();
            if (cand.Count < o.MinPieces) return res;

            // joints: ends within JoinMm, by a hash of cells JoinMm wide
            var cell = new Dictionary<(long, long), List<(int piece, int end)>>();
            (long, long) Key(Pt p) => ((long)Math.Floor(p.X / o.JoinMm), (long)Math.Floor(p.Y / o.JoinMm));
            Pt End(int i, int e) => e == 0 ? pieces[i].a : pieces[i].b;
            var isCand = new HashSet<int>(cand);
            // every piece counts at a joint (a long wall meeting the curve ends it), only short ones make curves
            for (int i = 0; i < pieces.Count; i++)
                for (int e = 0; e < 2; e++)
                {
                    var k = Key(End(i, e));
                    if (!cell.TryGetValue(k, out var l)) cell[k] = l = new List<(int, int)>();
                    l.Add((i, e));
                }
            List<(int piece, int end)> At(int i, int e)
            {
                var p = End(i, e); var (kx, ky) = Key(p); var found = new List<(int, int)>();
                for (long x = kx - 1; x <= kx + 1; x++)
                    for (long y = ky - 1; y <= ky + 1; y++)
                        if (cell.TryGetValue((x, y), out var l))
                            foreach (var q in l) if (q.piece != i && End(q.piece, q.end).Dist2D(p) <= o.JoinMm) found.Add(q);
                return found;
            }
            // the next piece across a joint, only where exactly two pieces meet (a T or a cross ends the chain)
            (int piece, int end)? Next(int i, int e) { var n = At(i, e); return n.Count == 1 && isCand.Contains(n[0].piece) ? n[0] : ((int, int)?)null; }

            var used = new HashSet<int>();
            var chains = new List<List<(int piece, bool fwd)>>();
            void Walk(int start, bool fwd)
            {
                var chain = new List<(int, bool)>();
                int i = start; bool f = fwd;
                while (i >= 0 && used.Add(i))
                {
                    chain.Add((i, f));
                    var nx = Next(i, f ? 1 : 0);
                    if (nx == null) break;
                    i = nx.Value.piece; f = nx.Value.end == 0;
                }
                if (chain.Count >= o.MinPieces) chains.Add(chain);
            }
            // open chains from their ends first, then closed loops
            foreach (var i in cand)
                if (!used.Contains(i))
                {
                    if (Next(i, 0) == null) Walk(i, true);
                    else if (Next(i, 1) == null) Walk(i, false);
                }
            foreach (var i in cand) if (!used.Contains(i)) Walk(i, true);

            double minTurn = o.MinTurnDeg * Math.PI / 180, maxTurn = o.MaxTurnDeg * Math.PI / 180;
            foreach (var chain in chains)
            {
                var pts = new List<Pt> { chain[0].fwd ? pieces[chain[0].piece].a : pieces[chain[0].piece].b };
                foreach (var (p, f) in chain) pts.Add(f ? pieces[p].b : pieces[p].a);
                // turn at each joint (signed, left positive)
                var turn = new double[pts.Count];
                for (int j = 1; j + 1 < pts.Count; j++)
                {
                    double a1 = Math.Atan2(pts[j].Y - pts[j - 1].Y, pts[j].X - pts[j - 1].X), a2 = Math.Atan2(pts[j + 1].Y - pts[j].Y, pts[j + 1].X - pts[j].X);
                    double d = a2 - a1; while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI;
                    turn[j] = d;
                }
                bool Bends(int j, int sign) => Math.Abs(turn[j]) >= minTurn && Math.Abs(turn[j]) <= maxTurn && Math.Sign(turn[j]) == sign;
                int s = 0;
                while (s + o.MinPieces < pts.Count)
                {
                    int sign = Math.Sign(turn[s + 1]);
                    if (sign == 0 || !Bends(s + 1, sign)) { s++; continue; }
                    // grow while the joints bend the same way and the points stay on one circle
                    int e = s + 2; CurveChain best = null; int bestE = -1;
                    while (e < pts.Count)
                    {
                        if (e - 1 > s && !Bends(e - 1, sign)) break;
                        if (e - s >= o.MinPieces)
                        {
                            var fit = Fit(pts.GetRange(s, e - s + 1), sign);
                            if (fit == null || fit.MaxDevMm > o.TolMm) break;
                            best = fit; bestE = e;
                        }
                        e++;
                    }
                    if (best != null && best.SweepDeg >= o.MinSweepDeg && best.R >= o.MinRadiusMm && best.R <= o.MaxRadiusMm)
                    {
                        for (int j = s; j < bestE; j++) best.Members.Add(chain[j].piece);
                        res.Add(best);
                        s = bestE;
                    }
                    else s++;
                }
            }
            return res;
        }

        /// <summary>The circle through the points, as an arc from the first to the last (sign +1: turning left, anticlockwise).</summary>
        public static CurveChain Fit(IList<Pt> pts, int sign)
        {
            var c = CurvedFit.Circle(pts);
            if (c == null) return null;
            double maxDev = pts.Max(p => Math.Abs(Math.Sqrt((p.X - c.Cx) * (p.X - c.Cx) + (p.Y - c.Cy) * (p.Y - c.Cy)) - c.R));
            // the sweep: the angles walked point to point (each step the short way), summed
            double sweep = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                double d = Math.Atan2(pts[i].Y - c.Cy, pts[i].X - c.Cx) - Math.Atan2(pts[i - 1].Y - c.Cy, pts[i - 1].X - c.Cx);
                while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI;
                sweep += d;
            }
            if (Math.Sign(sweep) != sign) return null;
            double a0 = Math.Atan2(pts[0].Y - c.Cy, pts[0].X - c.Cx);
            double start = sweep > 0 ? a0 : a0 + sweep;
            return new CurveChain { Cx = c.Cx, Cy = c.Cy, R = c.R, Start = start, Sweep = Math.Abs(sweep), RmsMm = c.RmsMm, MaxDevMm = maxDev };
        }
    }
}
