// Ported from the mcp-servers-for-revit survey pipeline (survey-pipeline.patch, base 86cf705). Revit-free.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BarrGazetas.RevitMcp.Survey
{
    public class CleanStats
    {
        public int NonFiniteRemoved;
        public int ZeroLengthRemoved;
        public int TinySegmentsMerged;
        public int CollinearRemoved;
        public int AutoClosed;
        public int DuplicatesRemoved;
        public int PointsSnapped;
        public int Dropped;
    }

    public static class GeometryCleaner
    {
        /// <summary>
        /// Cleans one polyline: removes non-finite and consecutive near-duplicate points, drops collinear mid-points,
        /// closes near-closed rings. Returns null if nothing usable remains.
        /// </summary>
        public static List<Pt> CleanPolyline(IList<Pt> input, ref bool closed, double minSegMm, double closeGapMm, double collinearDeg, CleanStats stats)
        {
            var pts = new List<Pt>(input.Count);
            foreach (var p in input)
            {
                if (!p.IsFinite()) { stats.NonFiniteRemoved++; continue; }
                if (pts.Count > 0 && pts[pts.Count - 1].Dist2D(p) < minSegMm && Math.Abs(pts[pts.Count - 1].Z - p.Z) < minSegMm)
                { stats.TinySegmentsMerged++; continue; }
                pts.Add(p);
            }

            // auto-close an almost closed ring, or drop the duplicated closing vertex of an explicitly closed one
            if (pts.Count >= 3)
            {
                double gap = pts[0].Dist2D(pts[pts.Count - 1]);
                if (gap < minSegMm) { pts.RemoveAt(pts.Count - 1); closed = true; }
                else if (!closed && gap <= closeGapMm) { closed = true; stats.AutoClosed++; }
            }

            if (collinearDeg > 0 && pts.Count >= 3) RemoveCollinear(pts, closed, collinearDeg, stats);

            if (pts.Count < (closed ? 3 : 2)) { stats.ZeroLengthRemoved++; return null; }
            return pts;
        }

        private static void RemoveCollinear(List<Pt> pts, bool closed, double tolDeg, CleanStats stats)
        {
            double sinTol = Math.Sin(tolDeg * Math.PI / 180.0);
            bool changed = true;
            while (changed && pts.Count > (closed ? 3 : 2))
            {
                changed = false;
                int i = closed ? 0 : 1;
                while (pts.Count > (closed ? 3 : 2) && i < (closed ? pts.Count : pts.Count - 1))
                {
                    Pt prev = pts[(i - 1 + pts.Count) % pts.Count], cur = pts[i], next = pts[(i + 1) % pts.Count];
                    double ax = cur.X - prev.X, ay = cur.Y - prev.Y, bx = next.X - cur.X, by = next.Y - cur.Y;
                    double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
                    bool drop = false;
                    if (la >= 1e-9 && lb >= 1e-9)
                    {
                        double cross = (ax * by - ay * bx) / (la * lb), dot = ax * bx + ay * by;
                        // keep the Z profile: only drop if Z is also (near) linear
                        double zExpected = prev.Z + (next.Z - prev.Z) * (la / (la + lb));
                        drop = Math.Abs(cross) <= sinTol && dot > 0 && Math.Abs(cur.Z - zExpected) < 1.0;
                    }
                    if (drop) { pts.RemoveAt(i); stats.CollinearRemoved++; changed = true; }
                    else i++;
                }
            }
        }

        /// <summary>
        /// Snaps all points of all polylines onto shared canonical points (2D tolerance). Mutates the lists.
        /// Keeps the first-seen point's coordinates (stable and order independent enough for survey data).
        /// </summary>
        public static void SnapPoints(IEnumerable<List<Pt>> polylines, double tolMm, CleanStats stats)
        {
            if (tolMm <= 0) return;
            var grid = new Dictionary<GridKey, List<Pt>>();
            foreach (var pl in polylines)
            {
                for (int i = 0; i < pl.Count; i++)
                {
                    Pt p = pl[i];
                    if (!p.IsFinite()) continue;
                    long cx = (long)Math.Floor(p.X / tolMm), cy = (long)Math.Floor(p.Y / tolMm);
                    Pt? found = null;
                    for (long dx = -1; dx <= 1 && found == null; dx++)
                        for (long dy = -1; dy <= 1 && found == null; dy++)
                        {
                            List<Pt> bucket;
                            if (!grid.TryGetValue(new GridKey(cx + dx, cy + dy), out bucket)) continue;
                            foreach (var c in bucket)
                                if (c.Dist2D(p) <= tolMm && Math.Abs(c.Z - p.Z) <= Math.Max(tolMm, 1.0)) { found = c; break; }
                        }
                    if (found.HasValue)
                    {
                        if (found.Value.X != p.X || found.Value.Y != p.Y) stats.PointsSnapped++;
                        pl[i] = new Pt(found.Value.X, found.Value.Y, p.Z);
                    }
                    else
                    {
                        var k = new GridKey(cx, cy);
                        List<Pt> b; if (!grid.TryGetValue(k, out b)) { b = new List<Pt>(); grid[k] = b; }
                        b.Add(p);
                    }
                }
            }
        }

        /// <summary>
        /// Canonical key of a (snapped) polyline independent of direction (and start vertex if closed).
        /// Used to detect the classic CAD "same line drawn twice" problem.
        /// </summary>
        public static string CanonicalKey(IList<Pt> pts, bool closed, double roundMm)
        {
            var keys = new List<string>(pts.Count);
            foreach (var p in pts) keys.Add(GeoMath.Key(p, roundMm));
            var rev = new List<string>(keys); rev.Reverse();
            string best = null;
            Action<List<string>, int> consider = (list, rot) =>
            {
                var sb = new StringBuilder();
                for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append('|'); sb.Append(list[(i + rot) % list.Count]); }
                string s2 = sb.ToString();
                if (best == null || string.CompareOrdinal(s2, best) < 0) best = s2;
            };
            if (!closed) { consider(keys, 0); consider(rev, 0); }
            else
            {
                // only rotations that start at the smallest vertex key can be canonical
                string minOrd = keys.Aggregate((a, b) => string.CompareOrdinal(a, b) <= 0 ? a : b);
                for (int r = 0; r < keys.Count; r++) if (keys[r] == minOrd) consider(keys, r);
                for (int r = 0; r < rev.Count; r++) if (rev[r] == minOrd) consider(rev, r);
            }
            return (closed ? "C:" : "O:") + best;
        }

        /// <summary>Removes duplicate entities (same layer + same canonical geometry). Returns the kept entities.</summary>
        public static List<CadEntity> RemoveDuplicates(IEnumerable<CadEntity> entities, double roundMm, CleanStats stats)
        {
            var seen = new HashSet<string>();
            var kept = new List<CadEntity>();
            foreach (var e in entities)
            {
                string key = e.Layer + "#" + e.Kind + "#" + (e.Kind == CadKind.Block ? e.BlockName + "#" : "") + CanonicalKey(e.Points, e.Closed, roundMm);
                if (seen.Add(key)) kept.Add(e); else stats.DuplicatesRemoved++;
            }
            return kept;
        }

        /// <summary>Full clean of an entity list (non-point kinds): clean, snap, de-duplicate. Returns new list.</summary>
        public static List<CadEntity> CleanAll(IEnumerable<CadEntity> input, Tolerances tol, CleanStats stats)
        {
            var result = new List<CadEntity>();
            foreach (var e in input)
            {
                if (e.Kind == CadKind.Point || e.Kind == CadKind.Block)
                {
                    if (e.Points.Count > 0 && e.Points[0].IsFinite()) result.Add(e); else stats.NonFiniteRemoved++;
                    continue;
                }
                bool closed = e.Closed;
                var cleaned = CleanPolyline(e.Points, ref closed, tol.MinSegmentMm, tol.CloseGapMm, tol.CollinearDeg, stats);
                if (cleaned == null) { stats.Dropped++; continue; }
                result.Add(new CadEntity { Index = e.Index, Layer = e.Layer, Kind = cleaned.Count == 2 && !closed ? CadKind.Line : CadKind.Polyline, Points = cleaned, Closed = closed });
            }
            SnapPoints(result.Where(r => r.Kind == CadKind.Line || r.Kind == CadKind.Polyline).Select(r => r.Points), tol.SnapMm, stats);
            return RemoveDuplicates(result, Math.Max(tol.SnapMm, 0.001), stats);
        }
    }

    public class Tolerances
    {
        public double SnapMm = 5;
        public double MinSegmentMm = 10;
        public double CloseGapMm = 50;
        public double CollinearDeg = 0.25;
    }
}
