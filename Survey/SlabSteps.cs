// Revit-free. 1.14.0: stepped floors from a plan's spot heights. A survey plan gives one floor several heights
// (split levels, raised or sunken rooms, landings); the build makes one flat slab per floor. This groups the spots
// that fall on one slab into height bands, takes the band most spots agree on as the slab, and finds the area each
// other band covers: a room (when the model has rooms) or, without rooms, the area nearest each spot walking round
// the walls (so a step ends at a wall or in a doorway, not half-way across a room). The Revit tool turns each area
// into its own floor (split) or into shape points on the slab (shape).
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public class SlabSpot
    {
        public Pt At;
        /// <summary>Survey height relative to the floor's level (mm).</summary>
        public double Mm;
        public int Band = -1;
        /// <summary>Not used for the steps (on or beside a stair, outside every room...): why.</summary>
        public string Skipped;
    }

    public class SlabBand
    {
        public int Id;
        public double Mm;
        public double SpreadMm;
        public List<SlabSpot> Spots = new List<SlabSpot>();
        /// <summary>Within MinStepMm of the main band: counted as the slab, not a step.</summary>
        public bool Main;
    }

    public class SlabRegion
    {
        public int Id, Band, Spots;
        public double Mm;
        /// <summary>Outer loops counter-clockwise, holes clockwise; model mm.</summary>
        public List<List<Pt>> Loops = new List<List<Pt>>();
        public double AreaM2;
        public string Room, Note;
    }

    public class SlabStepOptions
    {
        public double BandTolMm = 60, MinStepMm = 100, CellMm = 100, MaxReachMm = 4000, MinAreaM2 = 0.5, DoorwayMm = 1200;
        public int MinSpots = 1;
    }

    public class SlabStepPlan
    {
        public double MainMm;
        public int MainSpots;
        public string Method;
        public double FrameDeg;
        public List<SlabBand> Bands = new List<SlabBand>();
        public List<SlabRegion> Steps = new List<SlabRegion>();
        public List<SlabSpot> Spots = new List<SlabSpot>();
        public List<string> Notes = new List<string>();
    }

    public static class SlabSteps
    {
        // ------------------------------------------------------------------ height bands

        /// <summary>
        /// Spots (not skipped) grouped by height: sorted, a new band wherever the next spot is more than tolMm above the
        /// last. The main band is the one with most spots (ties: nearest preferMm, the slab's present top); bands within
        /// minStepMm of it count as the slab too. Sets each spot's Band.
        /// </summary>
        public static List<SlabBand> Bands(IList<SlabSpot> spots, double tolMm, double minStepMm, double preferMm)
        {
            var bands = new List<SlabBand>();
            foreach (var s in spots.Where(s => s.Skipped == null).OrderBy(s => s.Mm))
            {
                if (bands.Count == 0 || s.Mm - bands[^1].Spots[^1].Mm > tolMm) bands.Add(new SlabBand { Id = bands.Count });
                bands[^1].Spots.Add(s);
            }
            foreach (var b in bands)
            {
                var v = b.Spots.Select(s => s.Mm).OrderBy(x => x).ToList();
                b.Mm = v[v.Count / 2]; b.SpreadMm = v[^1] - v[0];
                foreach (var s in b.Spots) s.Band = b.Id;
            }
            if (bands.Count == 0) return bands;
            var main = bands.OrderByDescending(b => b.Spots.Count).ThenBy(b => Math.Abs(b.Mm - preferMm)).First();
            foreach (var b in bands) b.Main = Math.Abs(b.Mm - main.Mm) < minStepMm;
            // the slab's height is the main band's own (the absorbed bands only join it)
            return bands;
        }

        private static SlabBand MainOf(List<SlabBand> bands) =>
            bands.Where(b => b.Main).OrderByDescending(b => b.Spots.Count).FirstOrDefault();

        // ------------------------------------------------------------------ by rooms

        /// <summary>
        /// Each room takes the band most of its spots are in; a room on a band other than the slab's is a step, its
        /// outline the room's. Rooms with no spots stay on the slab. Spots outside every room are left out.
        /// </summary>
        public static SlabStepPlan ByRooms(IList<(string name, List<List<Pt>> loops)> rooms, IList<SlabSpot> spots, SlabStepOptions o, double preferMm)
        {
            var plan = new SlabStepPlan { Method = "rooms", Spots = spots.ToList() };
            int RoomOf(Pt p)
            {
                for (int i = 0; i < rooms.Count; i++) if (rooms[i].loops.Count > 0 && Inside(p, rooms[i].loops)) return i;
                return -1;
            }
            var roomOf = new Dictionary<SlabSpot, int>();
            var outside = new List<SlabSpot>();
            foreach (var s in spots.Where(s => s.Skipped == null))
            {
                int r = RoomOf(s.At);
                if (r < 0) outside.Add(s); else roomOf[s] = r;
            }
            // every spot on the slab counts for its height, in a room or not (1.14.1: a model with few rooms had most of
            // its spots dropped, and the slab's height taken from a handful); only the steps are rooms
            plan.Bands = Bands(spots, o.BandTolMm, o.MinStepMm, preferMm);
            foreach (var s in outside) s.Skipped = "outside every room (counted for the slab's height, not a step)";
            var main = MainOf(plan.Bands);
            if (main == null) { plan.Notes.Add("No spot heights on this slab."); return plan; }
            plan.MainMm = main.Mm; plan.MainSpots = plan.Bands.Where(b => b.Main).Sum(b => b.Spots.Count);
            foreach (var g in roomOf.GroupBy(kv => kv.Value))
            {
                var inRoom = g.Select(kv => kv.Key).ToList();
                var top = inRoom.GroupBy(s => plan.Bands[s.Band].Main ? -1 : s.Band).OrderByDescending(x => x.Count()).ThenBy(x => x.Key == -1 ? 0 : 1).First();
                var room = rooms[g.Key];
                if (inRoom.Select(s => s.Band).Distinct().Count() > 1)
                    plan.Notes.Add($"{room.name}: spots at several heights ({string.Join(", ", inRoom.Select(s => Math.Round(s.Mm)).Distinct())} mm); the room takes the commonest.");
                if (top.Key == -1 || top.Count() < o.MinSpots) continue;
                var band = plan.Bands[top.Key];
                var outer = Biggest(room.loops);
                var loops = room.loops.Select(l => Orient(l, l == outer)).ToList();
                double area = loops.Sum(l => GeoMath.SignedArea(l)) / 1e6;
                if (area < o.MinAreaM2) { plan.Notes.Add($"{room.name}: {area:0.00} m2 is under the smallest step area; left on the slab."); continue; }
                plan.Steps.Add(new SlabRegion
                {
                    Id = plan.Steps.Count + 1, Band = band.Id, Mm = Math.Round(top.Average(s => s.Mm)), Spots = top.Count(), Loops = loops, AreaM2 = Math.Round(area, 2), Room = room.name,
                    Note = top.Count() == 1 ? "one spot only: check it is a floor level, not a pit or a typo" : null,
                });
            }
            return plan;
        }

        private static List<Pt> Biggest(List<List<Pt>> loops) => loops.OrderByDescending(l => Math.Abs(GeoMath.SignedArea(l))).First();

        private static List<Pt> Orient(List<Pt> ring, bool ccw)
        {
            bool isCcw = GeoMath.SignedArea(ring) > 0;
            if (isCcw == ccw) return ring.ToList();
            var r = ring.ToList(); r.Reverse(); return r;
        }

        /// <summary>Inside by the even-odd rule over all loops (an outline and its holes).</summary>
        public static bool Inside(Pt p, IList<List<Pt>> loops)
        {
            bool inside = false;
            foreach (var l in loops) if (GeoMath.PointInPolygon(p, l)) inside = !inside;
            return inside;
        }

        // ------------------------------------------------------------------ by nearest spot round the walls

        /// <summary>The walls' main direction (degrees, 0-90), so a grid laid along it gives straight step edges.</summary>
        public static double FrameAngle(IList<Seg> walls)
        {
            double sx = 0, sy = 0;
            foreach (var w in walls)
            {
                double a = Math.Atan2(w.B.Y - w.A.Y, w.B.X - w.A.X), l = w.Length2D;
                sx += l * Math.Cos(4 * a); sy += l * Math.Sin(4 * a);
            }
            if (sx * sx + sy * sy < 1e-6) return 0;
            double deg = Math.Atan2(sy, sx) / 4 * 180 / Math.PI;
            return ((deg % 90) + 90) % 90;
        }

        /// <summary>
        /// Without rooms: a grid along the walls over the slab; every cell takes the band of the spot it is nearest to,
        /// walking round the walls (cells under a wall are filled last, from both sides), up to MaxReachMm; cells no spot
        /// reaches stay on the slab. Each other band's connected cells with enough spots and area become a step, traced
        /// into outlines.
        /// </summary>
        public static SlabStepPlan ByGrid(IList<List<Pt>> slabLoops, IList<SlabSpot> spots, IList<Seg> walls, SlabStepOptions o, double preferMm)
        {
            var plan = new SlabStepPlan { Method = "nearest spot round the walls", Spots = spots.ToList() };
            foreach (var s in spots.Where(s => s.Skipped == null)) if (!Inside(s.At, slabLoops)) s.Skipped = "not on the slab";
            plan.Bands = Bands(spots, o.BandTolMm, o.MinStepMm, preferMm);
            var main = MainOf(plan.Bands);
            if (main == null) { plan.Notes.Add("No spot heights on this slab."); return plan; }
            plan.MainMm = main.Mm; plan.MainSpots = plan.Bands.Where(b => b.Main).Sum(b => b.Spots.Count);
            if (plan.Bands.All(b => b.Main)) return plan;

            double deg = FrameAngle(walls); plan.FrameDeg = Math.Round(deg, 2);
            double ang = deg * Math.PI / 180, c = Math.Cos(ang), sn = Math.Sin(ang);
            Pt Fwd(Pt p) => new Pt(p.X * c + p.Y * sn, -p.X * sn + p.Y * c);   // model -> grid frame
            Pt Back(Pt p) => new Pt(p.X * c - p.Y * sn, p.X * sn + p.Y * c);   // grid frame -> model
            var loops = slabLoops.Select(l => l.Select(Fwd).ToList()).ToList();
            GeoMath.BoundingBox(loops.SelectMany(l => l), out var mn, out var mx);
            double cell = o.CellMm;
            int nx = (int)Math.Ceiling((mx.X - mn.X) / cell), ny = (int)Math.Ceiling((mx.Y - mn.Y) / cell);
            if ((long)nx * ny > 3_000_000) { cell *= Math.Sqrt((double)nx * ny / 3_000_000); nx = (int)Math.Ceiling((mx.X - mn.X) / cell); ny = (int)Math.Ceiling((mx.Y - mn.Y) / cell); }
            nx = Math.Max(nx, 1); ny = Math.Max(ny, 1);

            // 1) cells on the slab: scan lines through the cell centres
            var on = new bool[nx, ny];
            var edges = loops.SelectMany(l => l.Select((p, i) => (a: p, b: l[(i + 1) % l.Count]))).ToList();
            for (int j = 0; j < ny; j++)
            {
                double y = mn.Y + (j + 0.5) * cell;
                var xs = new List<double>();
                foreach (var (a, b) in edges)
                    if ((a.Y > y) != (b.Y > y)) xs.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int i0 = (int)Math.Ceiling(Math.Round((xs[k] - mn.X) / cell - 0.5, 6)), i1 = (int)Math.Floor(Math.Round((xs[k + 1] - mn.X) / cell - 0.5, 6));
                    for (int i = Math.Max(0, i0); i <= Math.Min(nx - 1, i1); i++) on[i, j] = true;
                }
            }
            // 2) cells under a wall
            var wall = new bool[nx, ny];
            foreach (var w in walls)
            {
                Pt a = Fwd(w.A), b = Fwd(w.B);
                int n = Math.Max(1, (int)Math.Ceiling(a.Dist2D(b) / (cell / 3)));
                for (int k = 0; k <= n; k++)
                {
                    double x = a.X + (b.X - a.X) * k / n, y = a.Y + (b.Y - a.Y) * k / n;
                    int i = Idx(x - mn.X, cell), j = Idx(y - mn.Y, cell);
                    if (i >= 0 && j >= 0 && i < nx && j < ny && on[i, j]) wall[i, j] = true;
                }
            }
            // 3) rooms: the free cells with doorways closed (walls grown by half the widest doorway); each takes its own
            //    spots' bands first, so a step ends in the doorway and does not leak into the next room
            int grow = Math.Max(1, (int)Math.Ceiling(o.DoorwayMm / 2 / cell));
            var closed = Grow(wall, nx, ny, grow);
            int mainLabel = -1;
            var label = new int[nx, ny]; var dist = new int[nx, ny];
            for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) label[i, j] = -2;
            var q = new Queue<(int, int)>();
            int LabelOf(SlabSpot s) => plan.Bands[s.Band].Main ? mainLabel : s.Band;
            var cellOf = new Dictionary<SlabSpot, (int i, int j)>();
            foreach (var s in spots.Where(s => s.Skipped == null).OrderBy(s => plan.Bands[s.Band].Main ? 1 : 0))
            {
                var p = Fwd(s.At);
                int i = Idx(p.X - mn.X, cell), j = Idx(p.Y - mn.Y, cell);
                if (i < 0 || j < 0 || i >= nx || j >= ny) continue;
                cellOf[s] = (i, j);
                if (label[i, j] != -2) continue;
                label[i, j] = LabelOf(s); q.Enqueue((i, j));
            }
            var nb = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
            while (q.Count > 0)
            {
                var (i, j) = q.Dequeue();
                foreach (var (di, dj) in nb)
                {
                    int a = i + di, b = j + dj;
                    if (a < 0 || b < 0 || a >= nx || b >= ny || !on[a, b] || closed[a, b] || label[a, b] != -2) continue;
                    label[a, b] = label[i, j]; q.Enqueue((a, b));
                }
            }
            //    then the doorways and the edges by the walls, from every side, as far as MaxReachMm (cells no spot
            //    reaches stay on the slab)
            for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) if (label[i, j] != -2) q.Enqueue((i, j));
            int reach = (int)Math.Ceiling(o.MaxReachMm / cell);
            while (q.Count > 0)
            {
                var (i, j) = q.Dequeue();
                if (dist[i, j] >= reach) continue;
                foreach (var (di, dj) in nb)
                {
                    int a = i + di, b = j + dj;
                    if (a < 0 || b < 0 || a >= nx || b >= ny || !on[a, b] || wall[a, b] || label[a, b] != -2) continue;
                    label[a, b] = label[i, j]; dist[a, b] = dist[i, j] + 1; q.Enqueue((a, b));
                }
            }
            // 4) the walls, from both sides (a step under an outside wall runs to the slab edge)
            for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) if (label[i, j] != -2) q.Enqueue((i, j));
            while (q.Count > 0)
            {
                var (i, j) = q.Dequeue();
                foreach (var (di, dj) in nb)
                {
                    int a = i + di, b = j + dj;
                    if (a < 0 || b < 0 || a >= nx || b >= ny || !on[a, b] || !wall[a, b] || label[a, b] != -2) continue;
                    label[a, b] = label[i, j]; q.Enqueue((a, b));
                }
            }
            // 5) one smoothing pass: a cell whose four neighbours all agree on another band joins them
            for (int i = 1; i + 1 < nx; i++)
                for (int j = 1; j + 1 < ny; j++)
                {
                    if (!on[i, j]) continue;
                    int l = label[i + 1, j];
                    if (l != label[i, j] && l == label[i - 1, j] && l == label[i, j + 1] && l == label[i, j - 1]) label[i, j] = l;
                }

            // 6) each step band's connected areas
            var seen = new bool[nx, ny];
            foreach (var band in plan.Bands.Where(b => !b.Main))
                for (int i0 = 0; i0 < nx; i0++)
                    for (int j0 = 0; j0 < ny; j0++)
                    {
                        if (seen[i0, j0] || !on[i0, j0] || label[i0, j0] != band.Id) continue;
                        var comp = new HashSet<(int, int)>(); var st = new Stack<(int, int)>(); st.Push((i0, j0)); seen[i0, j0] = true;
                        while (st.Count > 0)
                        {
                            var (i, j) = st.Pop(); comp.Add((i, j));
                            foreach (var (di, dj) in nb)
                            {
                                int a = i + di, b = j + dj;
                                if (a < 0 || b < 0 || a >= nx || b >= ny || seen[a, b] || !on[a, b] || label[a, b] != band.Id) continue;
                                seen[a, b] = true; st.Push((a, b));
                            }
                        }
                        var inside = cellOf.Where(kv => kv.Key.Band == band.Id && comp.Contains(kv.Value)).Select(kv => kv.Key).ToList();
                        double area = comp.Count * cell * cell / 1e6;
                        if (inside.Count < o.MinSpots || area < o.MinAreaM2)
                        {
                            plan.Notes.Add($"{inside.Count} spot(s) at {Math.Round(band.Mm)} mm over {area:0.0} m2: too small for a step, left on the slab.");
                            continue;
                        }
                        var traced = Trace(comp, cell, mn).Select(l => l.Select(Back).ToList()).ToList();
                        traced = traced.Where(l => GeoMath.SignedArea(l) > 0 || Math.Abs(GeoMath.SignedArea(l)) / 1e6 >= o.MinAreaM2).ToList();
                        plan.Steps.Add(new SlabRegion
                        {
                            Id = plan.Steps.Count + 1, Band = band.Id, Mm = Math.Round(inside.Average(s => s.Mm)), Spots = inside.Count, Loops = traced, AreaM2 = Math.Round(area, 2),
                            Note = inside.Count == 1 ? "one spot only: check it is a floor level, not a pit or a typo" : null,
                        });
                    }
            foreach (var b in plan.Bands.Where(b => !b.Main))
                foreach (var s in b.Spots) if (!plan.Steps.Any(r => r.Band == b.Id) && s.Skipped == null) s.Skipped = "its area is too small for a step";
            return plan;
        }

        /// <summary>Cell index of a distance along the grid; rounded first so a line on a cell edge falls the same way all along.</summary>
        private static int Idx(double d, double cell) => (int)Math.Floor(Math.Round(d / cell, 6));

        /// <summary>A mask grown by k cells every way (square), in two passes.</summary>
        private static bool[,] Grow(bool[,] m, int nx, int ny, int k)
        {
            var h = new bool[nx, ny];
            for (int j = 0; j < ny; j++)
            {
                int last = -1_000_000;
                for (int i = 0; i < nx; i++) { if (m[i, j]) last = i; if (i - last <= k) h[i, j] = true; }
                last = 1_000_000;
                for (int i = nx - 1; i >= 0; i--) { if (m[i, j]) last = i; if (last - i <= k) h[i, j] = true; }
            }
            var v = new bool[nx, ny];
            for (int i = 0; i < nx; i++)
            {
                int last = -1_000_000;
                for (int j = 0; j < ny; j++) { if (h[i, j]) last = j; if (j - last <= k) v[i, j] = true; }
                last = 1_000_000;
                for (int j = ny - 1; j >= 0; j--) { if (h[i, j]) last = j; if (last - j <= k) v[i, j] = true; }
            }
            return v;
        }

        /// <summary>
        /// Outlines of a set of grid cells (cell (i, j) spans origin + [i, i+1] x [j, j+1] cells): outer loops
        /// counter-clockwise, holes clockwise, straight runs merged.
        /// </summary>
        public static List<List<Pt>> Trace(ICollection<(int i, int j)> cells, double cell, Pt origin)
        {
            var set = cells as HashSet<(int, int)> ?? new HashSet<(int, int)>(cells);
            // directed boundary edges with the region on their left
            var outs = new Dictionary<(int, int), List<(int, int)>>();
            void E((int, int) a, (int, int) b) { if (!outs.TryGetValue(a, out var l)) outs[a] = l = new List<(int, int)>(); l.Add(b); }
            foreach (var (i, j) in set)
            {
                if (!set.Contains((i, j - 1))) E((i, j), (i + 1, j));
                if (!set.Contains((i + 1, j))) E((i + 1, j), (i + 1, j + 1));
                if (!set.Contains((i, j + 1))) E((i + 1, j + 1), (i, j + 1));
                if (!set.Contains((i - 1, j))) E((i, j + 1), (i, j));
            }
            var loops = new List<List<Pt>>();
            while (outs.Count > 0)
            {
                var start = outs.Keys.First();
                var ring = new List<(int, int)>();
                (int, int) cur = start, prev = (int.MinValue, int.MinValue);
                int guard = 0;
                while (outs.TryGetValue(cur, out var l) && guard++ < 10_000_000)
                {
                    (int, int) next;
                    if (l.Count == 1 || prev.Item1 == int.MinValue) next = l[0];
                    else
                    {
                        // two ways on (cells touching corner to corner): turn left, so each loop stays simple
                        int dx = cur.Item1 - prev.Item1, dy = cur.Item2 - prev.Item2;
                        next = l.OrderByDescending(n => dx * (n.Item2 - cur.Item2) - dy * (n.Item1 - cur.Item1)).First();
                    }
                    l.Remove(next); if (l.Count == 0) outs.Remove(cur);
                    ring.Add(cur); prev = cur; cur = next;
                    if (cur == start) break;
                }
                // drop points in the middle of a straight run
                var pts = new List<Pt>();
                for (int k = 0; k < ring.Count; k++)
                {
                    var a = ring[(k - 1 + ring.Count) % ring.Count]; var b = ring[k]; var cc = ring[(k + 1) % ring.Count];
                    if ((b.Item1 - a.Item1) * (cc.Item2 - b.Item2) - (b.Item2 - a.Item2) * (cc.Item1 - b.Item1) == 0) continue;
                    pts.Add(new Pt(origin.X + b.Item1 * cell, origin.Y + b.Item2 * cell));
                }
                if (pts.Count >= 3) loops.Add(pts);
            }
            return loops;
        }

        // ------------------------------------------------------------------ preview

        /// <summary>A plan picture: slab outline grey, each step's outline (red above the slab, blue below), spots as rings in their band's colour.</summary>
        public static byte[] Preview(IList<List<Pt>> slabLoops, SlabStepPlan plan, IList<Seg> walls = null, int widthPx = 1400)
        {
            var img = new LineImage(widthPx);
            var light = ((byte)215, (byte)215, (byte)215);
            if (walls != null) foreach (var w in walls) img.Add(w.A.X, w.A.Y, w.B.X, w.B.Y, light);
            foreach (var l in slabLoops) for (int i = 0; i < l.Count; i++) { var a = l[i]; var b = l[(i + 1) % l.Count]; img.Add(a.X, a.Y, b.X, b.Y, LineImage.Ink, 2); }
            (byte, byte, byte) Col(double mm) => mm >= plan.MainMm ? LineImage.Red : LineImage.Blue;
            foreach (var r in plan.Steps)
                foreach (var l in r.Loops) for (int i = 0; i < l.Count; i++) { var a = l[i]; var b = l[(i + 1) % l.Count]; img.Add(a.X, a.Y, b.X, b.Y, Col(r.Mm), 3); }
            foreach (var s in plan.Spots)
            {
                var c = s.Skipped != null ? LineImage.Grey : s.Band >= 0 && plan.Bands[s.Band].Main ? LineImage.Ink : Col(s.Mm);
                img.Ring(s.At.X, s.At.Y, 250, c);
            }
            return img.Png();
        }
    }
}
