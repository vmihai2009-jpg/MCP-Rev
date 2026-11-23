// Revit-free. 1.37.0: enclosures from plan line work, and the plan self-score that needs no reference model.
//
// 1. Gap closing (CloseGaps): a plan's wall faces stop at every door and window, so no room is closed. A face end with
//    nothing joined to it is bridged to the facing end of a parallel face on the same line within max_gap_mm (the far
//    jamb), and two loose ends whose extensions meet within corner_mm are run to their corner (corners drawn short).
//    Every bridge is reported, with "opening" when the plan's window/door lines sit in the gap, so a person sees what
//    was assumed. Nothing is written into the drawing: the bridges only close the rooms for the count.
// 2. Enclosures (Faces): the closed line work is cut where lines cross, loose ends are pruned, and the closed areas are
//    traced (planar faces). A thin face (2 x area / perimeter under wall_max_mm) is a wall's body; one that a bridge
//    bounds is an opening's passage, and the faces on each side of its bridges are the rooms it joins (room-opening
//    adjacency). The rest are rooms. The outside of the largest connected line work is the footprint.
// 3. Compare: the model's walls become face lines the same way (centre line +/- half the width, run on to meet), so the
//    model's enclosures and the survey's are measured alike: footprint area and overlap (IoU), and each survey room
//    paired with the model room over it (area difference, overlap). A survey room with no model room is a wall missing
//    or a room merged; a model room with no survey room is a wall that is not on the plan.
// Arcs are cut into short chords here only to trace areas; the model's curved walls stay arcs (nothing here is built).
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public class EnclosureOptions
    {
        /// <summary>Line ends this close are one point (mm).</summary>
        public double SnapMm { get; set; } = 5;
        /// <summary>The widest gap a bridge closes (a door or window), mm.</summary>
        public double MaxGapMm { get; set; } = 2500;
        /// <summary>How far off its line the far end may be, mm.</summary>
        public double AlignMm { get; set; } = 30;
        /// <summary>How far two loose ends are run on to meet at a corner, mm.</summary>
        public double CornerMm { get; set; } = 200;
        /// <summary>Faces thinner than this (2 x area / perimeter) are a wall's body, not a room, mm.</summary>
        public double WallMaxMm { get; set; } = 650;
        /// <summary>Rooms smaller than this are left out (m2): risers, ducts, drawing slivers.</summary>
        public double MinRoomM2 { get; set; } = 0.5;
        /// <summary>Opening lines within this of a bridge make it an opening (mm).</summary>
        public double OpeningNearMm { get; set; } = 400;
        /// <summary>Chord length arcs are cut into for tracing, mm.</summary>
        public double ArcStepMm { get; set; } = 150;
    }

    public class Bridge
    {
        public Pt A, B;
        /// <summary>"opening" (window/door lines in the gap), "gap" (nothing drawn there) or "corner" (ends run on to meet).</summary>
        public string Kind;
        public double LengthMm => A.Dist2D(B);
    }

    public class Enclosure
    {
        public int Id;
        /// <summary>room, wall (a wall's body), opening (a passage a bridge closes), small.</summary>
        public string Kind;
        public List<Pt> Ring = new List<Pt>();
        public double AreaM2, PerimeterM, WidthMm;
        public Pt Centroid;
        public string Name;
        /// <summary>For an opening: the rooms on either side of its bridges.</summary>
        public List<int> Joins = new List<int>();
    }

    public class EnclosureSet
    {
        public List<Enclosure> Faces = new List<Enclosure>();
        public List<Bridge> Bridges = new List<Bridge>();
        public List<Pt> Footprint = new List<Pt>();
        public double FootprintM2;
        public int Segments, Nodes, Pruned, LooseEnds;
        public IEnumerable<Enclosure> Rooms => Faces.Where(f => f.Kind == "room");
    }

    public class RoomPair
    {
        public int Survey { get; set; }
        public int? Model { get; set; }
        public string Name { get; set; }
        public double SurveyM2 { get; set; }
        public double? ModelM2 { get; set; }
        public double? AreaDiffPct { get; set; }
        public double? Iou { get; set; }
        /// <summary>The plan room's centre, model mm.</summary>
        public double[] At { get; set; }
    }

    public class EnclosureCompare
    {
        public double SurveyFootprintM2 { get; set; }
        public double ModelFootprintM2 { get; set; }
        public double FootprintDiffPct { get; set; }
        public double FootprintIou { get; set; }
        public int SurveyRooms { get; set; }
        public int ModelRooms { get; set; }
        public int Paired { get; set; }
        public int MissingInModel { get; set; }
        public int ExtraInModel { get; set; }
        /// <summary>Paired rooms whose overlap is at least 0.9.</summary>
        public int PairedWithin { get; set; }
        public double MeanIou { get; set; }
        public List<RoomPair> Rooms { get; set; } = new List<RoomPair>();
        public List<int> ModelOnly { get; set; } = new List<int>();
        public List<string> Notes { get; set; } = new List<string>();
    }

    public static class Enclosures
    {
        // ---------- inputs ----------

        /// <summary>An arc (centre x, y, radius, start and end angle in degrees, anticlockwise) as chords no longer than step.</summary>
        public static List<Seg> ArcChords(double[] arc, double step)
        {
            var list = new List<Seg>();
            if (arc == null || arc.Length < 5 || arc[2] <= 0) return list;
            double a0 = arc[3] * Math.PI / 180, a1 = arc[4] * Math.PI / 180;
            while (a1 <= a0) a1 += 2 * Math.PI;
            return Chords(arc[0], arc[1], arc[2], a0, a1 - a0, step);
        }

        private static List<Seg> Chords(double cx, double cy, double r, double start, double sweep, double step)
        {
            var list = new List<Seg>();
            int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) * r / Math.Max(10, step)));
            Pt prev = new Pt(cx + r * Math.Cos(start), cy + r * Math.Sin(start));
            for (int i = 1; i <= n; i++)
            {
                double a = start + sweep * i / n;
                var p = new Pt(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
                list.Add(new Seg(prev, p, -1)); prev = p;
            }
            return list;
        }

        /// <summary>
        /// The model's walls as face lines: each straight wall's centre line offset by half its width both sides and run
        /// on by half its width at each end (so faces meet at corners and tees; what sticks out is pruned as a loose end).
        /// </summary>
        public static List<Seg> WallFaceLines(IEnumerable<FitWall> walls, IEnumerable<ArcWall> arcs = null, double arcStep = 150)
        {
            var list = new List<Seg>();
            foreach (var w in walls ?? Enumerable.Empty<FitWall>())
            {
                double len = w.A.Dist2D(w.B); if (len < 1 || w.ThicknessMm <= 0) continue;
                double ux = (w.B.X - w.A.X) / len, uy = (w.B.Y - w.A.Y) / len, h = w.ThicknessMm / 2, run = h + 1;
                foreach (int side in new[] { -1, 1 })
                {
                    double ox = -uy * h * side, oy = ux * h * side;
                    list.Add(new Seg(new Pt(w.A.X + ox - ux * run, w.A.Y + oy - uy * run), new Pt(w.B.X + ox + ux * run, w.B.Y + oy + uy * run), (int)w.Id));
                }
            }
            foreach (var a in arcs ?? Enumerable.Empty<ArcWall>())
            {
                if (a.Radius <= 0 || a.ThicknessMm <= 0) continue;
                double h = a.ThicknessMm / 2, ext = Math.Min(0.2, (h + 1) / Math.Max(1, a.Radius)) * Math.Sign(a.Sweep == 0 ? 1 : a.Sweep);
                foreach (double r in new[] { a.Radius - h, a.Radius + h })
                    if (r > 0) list.AddRange(Chords(a.Cx, a.Cy, r, a.Start - ext, a.Sweep + 2 * ext, arcStep));
            }
            return list;
        }

        // ---------- 1. gap closing ----------

        public static List<Bridge> CloseGaps(IList<Seg> faces, IList<Seg> openings, EnclosureOptions o)
        {
            o = o ?? new EnclosureOptions();
            var bridges = new List<Bridge>();
            var segs = faces.Where(s => s.Length2D > o.SnapMm).ToList();
            var index = new SegIndex(segs, Math.Max(500, o.MaxGapMm / 2));
            // open ends: an end where its line does not run on (no line in line with it continues from there). A door's
            // jamb joins the face to the other face, but the face itself still stops: it is open toward the far jamb.
            // Loose ends (nothing at all within snap) are kept apart for the corner pass.
            var loose = new List<(int seg, Pt p, double dx, double dy)>();
            var free = new HashSet<int>();
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i]; double l = s.Length2D, ux = (s.B.X - s.A.X) / l, uy = (s.B.Y - s.A.Y) / l;
                foreach (var (p, dx, dy) in new[] { (s.A, -ux, -uy), (s.B, ux, uy) })
                {
                    bool runsOn = false, touched = false;
                    foreach (int j in index.Near(p, o.SnapMm))
                    {
                        if (j == i || DistToSeg(p, segs[j]) > o.SnapMm) continue;
                        touched = true;
                        var t = segs[j]; double tl = t.Length2D, tx = (t.B.X - t.A.X) / tl, ty = (t.B.Y - t.A.Y) / tl;
                        if (Math.Abs(tx * dx + ty * dy) < 0.995) continue;
                        // in line: it runs on when it reaches past this end
                        double far = Math.Max((t.A.X - p.X) * dx + (t.A.Y - p.Y) * dy, (t.B.X - p.X) * dx + (t.B.Y - p.Y) * dy);
                        if (far > o.SnapMm) { runsOn = true; break; }
                    }
                    if (runsOn) continue;
                    if (!touched) free.Add(loose.Count);
                    loose.Add((i, p, dx, dy));
                }
            }
            var looseIndex = new Dictionary<GridKey, List<int>>();
            double cell = Math.Max(500, o.MaxGapMm);
            GridKey Key(Pt p) => new GridKey((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));
            for (int k = 0; k < loose.Count; k++) { var key = Key(loose[k].p); if (!looseIndex.TryGetValue(key, out var lst)) looseIndex[key] = lst = new List<int>(); lst.Add(k); }
            IEnumerable<int> LooseNear(Pt p)
            {
                var c = Key(p);
                for (long x = c.A - 1; x <= c.A + 1; x++) for (long y = c.B - 1; y <= c.B + 1; y++)
                        if (looseIndex.TryGetValue(new GridKey(x, y), out var lst)) foreach (int k in lst) yield return k;
            }
            const double cosTol = 0.995; // about 6 degrees
            // straight bridges: best facing partner, kept only when each is the other's best
            var best = new int[loose.Count]; var bestD = new double[loose.Count];
            for (int k = 0; k < loose.Count; k++)
            {
                best[k] = -1; bestD[k] = double.MaxValue;
                var (si, p, dx, dy) = loose[k];
                foreach (int m in LooseNear(p))
                {
                    if (m == k || loose[m].seg == si) continue;
                    var q = loose[m];
                    double vx = q.p.X - p.X, vy = q.p.Y - p.Y, d = Math.Sqrt(vx * vx + vy * vy);
                    if (d <= o.SnapMm || d > o.MaxGapMm) continue;
                    double along = vx * dx + vy * dy, off = Math.Abs(-vy * dx + vx * dy);
                    if (along / d < cosTol || off > o.AlignMm) continue;
                    if (q.dx * dx + q.dy * dy > -cosTol) continue; // the far end must face back
                    if (Crosses(p, q.p, segs, index, si, q.seg)) continue;
                    if (d < bestD[k]) { bestD[k] = d; best[k] = m; }
                }
            }
            var used = new bool[loose.Count];
            for (int k = 0; k < loose.Count; k++)
            {
                int m = best[k];
                if (m < 0 || used[k] || used[m] || best[m] != k) continue;
                used[k] = used[m] = true;
                var a = loose[k].p; var b = loose[m].p;
                bool opening = openings != null && openings.Any(s => NearSpan(s, a, b, o.OpeningNearMm));
                bridges.Add(new Bridge { A = a, B = b, Kind = opening ? "opening" : "gap" });
            }
            // corners: two loose ends whose run-on lines meet within corner_mm of both
            for (int k = 0; k < loose.Count; k++)
            {
                if (used[k] || !free.Contains(k)) continue;
                var (si, p, dx, dy) = loose[k];
                int pick = -1; Pt at = default; double bestRun = double.MaxValue;
                foreach (int m in LooseNear(p))
                {
                    if (m == k || used[m] || !free.Contains(m) || loose[m].seg == si) continue;
                    var q = loose[m];
                    double den = dx * q.dy - dy * q.dx;
                    if (Math.Abs(den) < 0.2) continue; // near parallel: not a corner
                    double wx = q.p.X - p.X, wy = q.p.Y - p.Y;
                    double t = (wx * q.dy - wy * q.dx) / den, u = (wx * dy - wy * dx) / den;
                    if (t < -o.SnapMm || u < -o.SnapMm || t > o.CornerMm || u > o.CornerMm) continue;
                    if (t + u < bestRun) { bestRun = t + u; pick = m; at = new Pt(p.X + dx * t, p.Y + dy * t); }
                }
                if (pick < 0) continue;
                used[k] = used[pick] = true;
                if (p.Dist2D(at) > o.SnapMm) bridges.Add(new Bridge { A = p, B = at, Kind = "corner" });
                if (loose[pick].p.Dist2D(at) > o.SnapMm) bridges.Add(new Bridge { A = loose[pick].p, B = at, Kind = "corner" });
            }
            return bridges;
        }

        private static bool Crosses(Pt a, Pt b, List<Seg> segs, SegIndex index, int skip1, int skip2)
        {
            var mid = new Pt((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            foreach (int j in index.Near(mid, a.Dist2D(b) / 2 + 1))
            {
                if (j == skip1 || j == skip2) continue;
                var s = segs[j];
                if (ProperCross(a, b, s.A, s.B) || DistToSeg(mid, s) <= 1) return true; // crossed, or a line already there
            }
            return false;
        }

        private static bool ProperCross(Pt a, Pt b, Pt c, Pt d)
        {
            double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
            return ((d1 > 1e-6 && d2 < -1e-6) || (d1 < -1e-6 && d2 > 1e-6)) && ((d3 > 1e-6 && d4 < -1e-6) || (d3 < -1e-6 && d4 > 1e-6));
        }

        private static double Cross(Pt o, Pt a, Pt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        /// <summary>Whether a line comes within `near` of the bridge a-b, over the bridge's span.</summary>
        private static bool NearSpan(Seg s, Pt a, Pt b, double near)
        {
            double l = a.Dist2D(b); if (l < 1) return false;
            double ux = (b.X - a.X) / l, uy = (b.Y - a.Y) / l;
            foreach (var p in new[] { s.A, s.B, new Pt((s.A.X + s.B.X) / 2, (s.A.Y + s.B.Y) / 2) })
            {
                double t = (p.X - a.X) * ux + (p.Y - a.Y) * uy, off = Math.Abs(-(p.X - a.X) * uy + (p.Y - a.Y) * ux);
                if (t >= -1 && t <= l + 1 && off <= near) return true;
            }
            return false;
        }

        internal static double DistToSeg(Pt p, Seg s)
        {
            double vx = s.B.X - s.A.X, vy = s.B.Y - s.A.Y, l2 = vx * vx + vy * vy;
            double t = l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((p.X - s.A.X) * vx + (p.Y - s.A.Y) * vy) / l2));
            double x = s.A.X + vx * t - p.X, y = s.A.Y + vy * t - p.Y;
            return Math.Sqrt(x * x + y * y);
        }

        // ---------- 2. faces ----------

        /// <summary>Close the gaps (survey side) or not (model side), then trace and sort the enclosures.</summary>
        public static EnclosureSet Build(IList<Seg> faces, IList<Seg> openings, EnclosureOptions o, bool closeGaps, IEnumerable<(Pt at, string name)> names = null)
        {
            o = o ?? new EnclosureOptions();
            var set = new EnclosureSet();
            var segs = faces.Where(s => s.Length2D > o.SnapMm && s.A.IsFinite() && s.B.IsFinite()).Select(s => new Seg(new Pt(s.A.X, s.A.Y), new Pt(s.B.X, s.B.Y), s.Source)).ToList();
            if (closeGaps) set.Bridges = CloseGaps(segs, openings, o);
            int firstBridge = segs.Count;
            segs.AddRange(set.Bridges.Select(b => new Seg(b.A, b.B, -2)));
            set.Segments = segs.Count;
            var g = Graph.Make(segs, o.SnapMm, firstBridge);
            set.Nodes = g.Pts.Count;
            set.Pruned = g.Prune();
            set.LooseEnds = g.LooseEnds;
            var faceList = g.TraceFaces();
            // the footprint: the outside of the biggest connected line work
            var outer = faceList.Where(f => f.area < 0).OrderBy(f => f.area).FirstOrDefault();
            if (outer.ring != null) { set.Footprint = outer.ring; set.FootprintM2 = -outer.area / 1e6; }
            var holes = faceList.Where(f => f.area < 0 && !ReferenceEquals(f.ring, outer.ring)).ToList();
            int id = 0;
            var inner = faceList.Where(f => f.area > 0).ToList();
            var byHalf = new Dictionary<long, int>();
            foreach (var f in inner)
            {
                double area = f.area, per = Perimeter(f.ring);
                var e = new Enclosure { Id = ++id, Ring = f.ring, PerimeterM = per / 1000, Centroid = Centroid(f.ring, f.area) };
                // islands inside (a column drawn in a room): their area is not the room's
                foreach (var h in holes)
                    if (GeoMath.PointInPolygon(h.ring[0], f.ring) && !inner.Any(o2 => o2.area < f.area && o2.area > 0 && GeoMath.PointInPolygon(h.ring[0], o2.ring))) area += h.area;
                e.AreaM2 = area / 1e6;
                e.WidthMm = per > 0 ? 2 * f.area / per : 0;
                bool bridged = f.bridgeEdges > 0;
                e.Kind = e.WidthMm < o.WallMaxMm ? (bridged ? "opening" : "wall") : e.AreaM2 < o.MinRoomM2 ? "small" : "room";
                set.Faces.Add(e);
                foreach (var h in f.halves) byHalf[h] = e.Id;
            }
            // which rooms each opening joins: the faces across its bridge edges
            var byId = set.Faces.ToDictionary(e => e.Id);
            foreach (var f in inner.Where(f => f.bridgeEdges > 0))
            {
                var e = set.Faces.First(x => ReferenceEquals(x.Ring, f.ring));
                if (e.Kind != "opening") continue;
                foreach (var h in f.bridgeHalves)
                    if (byHalf.TryGetValue(Graph.Twin(h), out int other) && byId[other].Kind == "room" && !e.Joins.Contains(other)) e.Joins.Add(other);
            }
            if (names != null)
                foreach (var (at, name) in names)
                {
                    var room = set.Rooms.Where(r => GeoMath.PointInPolygon(at, r.Ring)).OrderBy(r => r.AreaM2).FirstOrDefault();
                    if (room != null) room.Name = room.Name == null ? name : room.Name + " / " + name;
                }
            return set;
        }

        private static double Perimeter(List<Pt> ring)
        {
            double p = 0;
            for (int i = 0; i < ring.Count; i++) p += ring[i].Dist2D(ring[(i + 1) % ring.Count]);
            return p;
        }

        private static Pt Centroid(List<Pt> ring, double area)
        {
            double cx = 0, cy = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count]; double c = a.X * b.Y - b.X * a.Y;
                cx += (a.X + b.X) * c; cy += (a.Y + b.Y) * c;
            }
            return Math.Abs(area) < 1e-9 ? ring[0] : new Pt(cx / (6 * area), cy / (6 * area));
        }

        // ---------- 3. compare ----------

        public static EnclosureCompare Compare(EnclosureSet survey, EnclosureSet model, double cellMm = 50)
        {
            var c = new EnclosureCompare
            {
                SurveyFootprintM2 = Math.Round(survey.FootprintM2, 2), ModelFootprintM2 = Math.Round(model.FootprintM2, 2),
            };
            c.FootprintDiffPct = survey.FootprintM2 > 0 ? Math.Round(100 * (model.FootprintM2 - survey.FootprintM2) / survey.FootprintM2, 2) : 0;
            c.FootprintIou = Math.Round(Iou(survey.Footprint, model.Footprint, Math.Max(cellMm, 100)), 3);
            var sr = survey.Rooms.ToList(); var mr = model.Rooms.ToList();
            c.SurveyRooms = sr.Count; c.ModelRooms = mr.Count;
            var taken = new HashSet<int>();
            foreach (var s in sr.OrderByDescending(r => r.AreaM2))
            {
                // the model room over the survey room's centre (or, for an L-shaped room, the one with the most overlap)
                var cands = mr.Where(m => !taken.Contains(m.Id) && Overlaps(m.Ring, s.Ring)).ToList();
                Enclosure pick = null; double iou = 0;
                foreach (var m in cands) { double v = Iou(s.Ring, m.Ring, cellMm); if (v > iou) { iou = v; pick = m; } }
                var pair = new RoomPair { Survey = s.Id, Name = s.Name, SurveyM2 = Math.Round(s.AreaM2, 2), At = new[] { Math.Round(s.Centroid.X), Math.Round(s.Centroid.Y) } };
                if (pick != null && iou >= 0.5)
                {
                    taken.Add(pick.Id);
                    pair.Model = pick.Id; pair.ModelM2 = Math.Round(pick.AreaM2, 2); pair.Iou = Math.Round(iou, 3);
                    pair.AreaDiffPct = Math.Round(100 * (pick.AreaM2 - s.AreaM2) / s.AreaM2, 2);
                }
                c.Rooms.Add(pair);
            }
            c.Paired = c.Rooms.Count(r => r.Model != null);
            c.PairedWithin = c.Rooms.Count(r => r.Iou >= 0.9);
            c.MissingInModel = c.Rooms.Count(r => r.Model == null);
            c.ModelOnly = mr.Where(m => !taken.Contains(m.Id)).Select(m => m.Id).ToList();
            c.ExtraInModel = c.ModelOnly.Count;
            c.MeanIou = c.Paired > 0 ? Math.Round(c.Rooms.Where(r => r.Iou != null).Average(r => r.Iou.Value), 3) : 0;
            if (survey.Rooms.Count() == 0) c.Notes.Add("The plan's line work closed no rooms: check the wall layers (survey_layer_roles) or raise max_gap_mm.");
            if (survey.LooseEnds > 0) c.Notes.Add($"{survey.LooseEnds} loose ends were left after gap closing (pruned before tracing).");
            return c;
        }

        private static bool Overlaps(List<Pt> a, List<Pt> b)
        {
            GeoMath.BoundingBox(a, out var a0, out var a1); GeoMath.BoundingBox(b, out var b0, out var b1);
            return a0.X < b1.X && b0.X < a1.X && a0.Y < b1.Y && b0.Y < a1.Y;
        }

        /// <summary>Overlap / union of two rings, counted on a grid of cell mm.</summary>
        public static double Iou(List<Pt> a, List<Pt> b, double cell)
        {
            if (a == null || b == null || a.Count < 3 || b.Count < 3) return 0;
            GeoMath.BoundingBox(a.Concat(b), out var lo, out var hi);
            double w = hi.X - lo.X, h = hi.Y - lo.Y;
            // keep the grid to about 250 000 cells
            double step = Math.Max(cell, Math.Sqrt(w * h / 250000));
            int nx = Math.Max(1, (int)Math.Ceiling(w / step)), ny = Math.Max(1, (int)Math.Ceiling(h / step));
            long both = 0, any = 0;
            for (int iy = 0; iy < ny; iy++)
            {
                double y = lo.Y + (iy + 0.5) * step;
                var ia = Crossings(a, y); var ib = Crossings(b, y);
                for (int ix = 0; ix < nx; ix++)
                {
                    double x = lo.X + (ix + 0.5) * step;
                    bool inA = Inside(ia, x), inB = Inside(ib, x);
                    if (inA && inB) both++;
                    if (inA || inB) any++;
                }
            }
            return any == 0 ? 0 : (double)both / any;
        }

        private static List<double> Crossings(List<Pt> ring, double y)
        {
            var xs = new List<double>();
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var p = ring[i]; var q = ring[j];
                if ((p.Y > y) != (q.Y > y)) xs.Add(p.X + (y - p.Y) * (q.X - p.X) / (q.Y - p.Y));
            }
            xs.Sort();
            return xs;
        }

        private static bool Inside(List<double> xs, double x)
        {
            int n = 0; foreach (double v in xs) { if (v < x) n++; else break; }
            return (n & 1) == 1;
        }

        // ---------- plumbing ----------

        /// <summary>Segments in a grid, for "which lines are near here".</summary>
        private sealed class SegIndex
        {
            private readonly Dictionary<GridKey, List<int>> _g = new Dictionary<GridKey, List<int>>();
            private readonly double _cell;
            public SegIndex(IList<Seg> segs, double cell)
            {
                _cell = cell;
                for (int i = 0; i < segs.Count; i++)
                {
                    var s = segs[i];
                    long x0 = C(Math.Min(s.A.X, s.B.X)), x1 = C(Math.Max(s.A.X, s.B.X)), y0 = C(Math.Min(s.A.Y, s.B.Y)), y1 = C(Math.Max(s.A.Y, s.B.Y));
                    for (long x = x0; x <= x1; x++) for (long y = y0; y <= y1; y++)
                        {
                            var k = new GridKey(x, y);
                            if (!_g.TryGetValue(k, out var l)) _g[k] = l = new List<int>();
                            l.Add(i);
                        }
                }
            }
            private long C(double v) => (long)Math.Floor(v / _cell);
            public IEnumerable<int> Near(Pt p, double r)
            {
                var seen = new HashSet<int>();
                for (long x = C(p.X - r); x <= C(p.X + r); x++) for (long y = C(p.Y - r); y <= C(p.Y + r); y++)
                        if (_g.TryGetValue(new GridKey(x, y), out var l)) foreach (int i in l) if (seen.Add(i)) yield return i;
            }
        }

        /// <summary>A planar graph of the line work: lines cut where they cross, ends snapped, half-edges traced into faces.</summary>
        private sealed class Graph
        {
            public readonly List<Pt> Pts = new List<Pt>();
            private readonly Dictionary<GridKey, List<int>> _grid = new Dictionary<GridKey, List<int>>();
            private readonly double _tol;
            /// <summary>Neighbours of each node; an edge is a bridge when its key is in _bridges.</summary>
            private readonly List<HashSet<int>> _adj = new List<HashSet<int>>();
            private readonly HashSet<long> _bridges = new HashSet<long>();
            public int LooseEnds;

            private Graph(double tol) { _tol = Math.Max(0.5, tol); }

            private int Node(Pt p)
            {
                long cx = (long)Math.Floor(p.X / _tol), cy = (long)Math.Floor(p.Y / _tol);
                for (long x = cx - 1; x <= cx + 1; x++) for (long y = cy - 1; y <= cy + 1; y++)
                        if (_grid.TryGetValue(new GridKey(x, y), out var l)) foreach (int i in l) if (Pts[i].Dist2D(p) <= _tol) return i;
                Pts.Add(p); _adj.Add(new HashSet<int>());
                var k = new GridKey(cx, cy);
                if (!_grid.TryGetValue(k, out var lst)) _grid[k] = lst = new List<int>();
                lst.Add(Pts.Count - 1);
                return Pts.Count - 1;
            }

            public static long Half(int u, int v) => ((long)u << 32) | (uint)v;
            public static long Twin(long h) => Half((int)(h & 0xffffffff), (int)(h >> 32));
            private static long Undirected(int u, int v) => u < v ? Half(u, v) : Half(v, u);

            public static Graph Make(List<Seg> segs, double tol, int firstBridge)
            {
                var g = new Graph(tol);
                var cuts = segs.Select(_ => new List<double> { 0, 1 }).ToList();
                var index = new SegIndex(segs, 1000);
                for (int i = 0; i < segs.Count; i++)
                    foreach (int j in index.Near(Mid(segs[i]), segs[i].Length2D / 2 + tol))
                    {
                        if (j <= i) continue;
                        Intersect(segs[i], segs[j], tol, cuts[i], cuts[j]);
                    }
                for (int i = 0; i < segs.Count; i++)
                {
                    var s = segs[i]; var ts = cuts[i].Distinct().OrderBy(t => t).ToList();
                    int prev = g.Node(At(s, ts[0]));
                    for (int k = 1; k < ts.Count; k++)
                    {
                        int n = g.Node(At(s, ts[k]));
                        if (n == prev) continue;
                        g._adj[prev].Add(n); g._adj[n].Add(prev);
                        if (i >= firstBridge) g._bridges.Add(Undirected(prev, n));
                        prev = n;
                    }
                }
                return g;
            }

            private static Pt Mid(Seg s) => new Pt((s.A.X + s.B.X) / 2, (s.A.Y + s.B.Y) / 2);
            private static Pt At(Seg s, double t) => new Pt(s.A.X + (s.B.X - s.A.X) * t, s.A.Y + (s.B.Y - s.A.Y) * t);

            /// <summary>Where two lines cross or touch (an end on the other's body within tol): the cut parameters on each.</summary>
            private static void Intersect(Seg a, Seg b, double tol, List<double> ca, List<double> cb)
            {
                double rx = a.B.X - a.A.X, ry = a.B.Y - a.A.Y, sx = b.B.X - b.A.X, sy = b.B.Y - b.A.Y;
                double la = Math.Sqrt(rx * rx + ry * ry), lb = Math.Sqrt(sx * sx + sy * sy);
                double den = rx * sy - ry * sx;
                if (Math.Abs(den) > 1e-9 * la * lb)
                {
                    double qx = b.A.X - a.A.X, qy = b.A.Y - a.A.Y;
                    double t = (qx * sy - qy * sx) / den, u = (qx * ry - qy * rx) / den;
                    double ta = tol / la, tb = tol / lb;
                    if (t >= -ta && t <= 1 + ta && u >= -tb && u <= 1 + tb)
                    {
                        ca.Add(Math.Max(0, Math.Min(1, t))); cb.Add(Math.Max(0, Math.Min(1, u)));
                        return;
                    }
                }
                // parallel or not crossing: ends lying on the other line (overlaps, tees within tol)
                foreach (var (p, onA, own) in new[] { (b.A, true, 0.0), (b.B, true, 1.0), (a.A, false, 0.0), (a.B, false, 1.0) })
                {
                    var s = onA ? a : b; double l = onA ? la : lb;
                    double vx = s.B.X - s.A.X, vy = s.B.Y - s.A.Y;
                    double t = ((p.X - s.A.X) * vx + (p.Y - s.A.Y) * vy) / (l * l);
                    if (t <= 0 || t >= 1) continue;
                    if (DistToSeg(p, s) > tol) continue;
                    (onA ? ca : cb).Add(t);
                    (onA ? cb : ca).Add(own);
                }
            }

            /// <summary>Takes away loose ends (nodes with one neighbour) until none are left. Returns the edges removed.</summary>
            public int Prune()
            {
                int removed = 0;
                var stack = new Stack<int>(Enumerable.Range(0, Pts.Count).Where(i => _adj[i].Count == 1));
                LooseEnds = stack.Count;
                while (stack.Count > 0)
                {
                    int n = stack.Pop();
                    if (_adj[n].Count != 1) continue;
                    int m = _adj[n].First();
                    _adj[n].Clear(); _adj[m].Remove(n); removed++;
                    if (_adj[m].Count == 1) stack.Push(m);
                }
                return removed;
            }

            public List<(List<Pt> ring, double area, int bridgeEdges, List<long> halves, List<long> bridgeHalves)> TraceFaces()
            {
                var sorted = new Dictionary<int, int[]>();
                for (int i = 0; i < Pts.Count; i++)
                    if (_adj[i].Count > 0)
                    {
                        var p = Pts[i];
                        sorted[i] = _adj[i].OrderBy(j => Math.Atan2(Pts[j].Y - p.Y, Pts[j].X - p.X)).ToArray();
                    }
                var seen = new HashSet<long>();
                var faces = new List<(List<Pt>, double, int, List<long>, List<long>)>();
                foreach (var kv in sorted)
                    foreach (int v0 in kv.Value)
                    {
                        long h0 = Half(kv.Key, v0);
                        if (seen.Contains(h0)) continue;
                        var ring = new List<Pt>(); var halves = new List<long>(); var bh = new List<long>();
                        int u = kv.Key, v = v0, guard = 0, bridges = 0;
                        while (guard++ < 1000000)
                        {
                            long h = Half(u, v);
                            if (!seen.Add(h)) break;
                            ring.Add(Pts[u]); halves.Add(h);
                            if (_bridges.Contains(Undirected(u, v))) { bridges++; bh.Add(h); }
                            // next: at v, the neighbour just clockwise of u (the face stays on the left)
                            var around = sorted[v]; int idx = Array.IndexOf(around, u);
                            int w = around[(idx - 1 + around.Length) % around.Length];
                            u = v; v = w;
                        }
                        if (ring.Count < 3) continue;
                        double area = GeoMath.SignedArea(ring);
                        if (Math.Abs(area) < 1) continue;
                        faces.Add((ring, area, bridges, halves, bh));
                    }
                return faces;
            }
        }
    }
}
