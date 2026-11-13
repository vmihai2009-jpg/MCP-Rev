// Revit-free. 1.17.0: the facade detail map. Reads one elevation's linework (already in facade coordinates:
// u = mm along the facade from its left end as seen from outside, z = mm above datum) and names what it draws:
// windows, bands (cornices, string courses, plinths), rustication, window surrounds (architraves, sills, hoods,
// pediments, keystones, consoles), pilasters, quoins, balustrades, and the shapes left over. Repeats are grouped
// into types so one family or profile can be made once and placed many times. Every item says how to build it
// and where its depth (projection out of the wall, which an elevation cannot show) has to come from.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public class FacadeItem
    {
        public string Id, Kind, Type;
        /// <summary>Box on the facade: u along (mm from the facade's left end, seen from outside), z above datum (mm).</summary>
        public double U0, U1, Z0, Z1;
        public int Lines, Count = 1;
        /// <summary>The window it belongs to (index in FacadeMapResult.Windows), if any.</summary>
        public int? Window;
        /// <summary>Measurements (mm) by name: width, height, projectionBeyondJamb, pitch, rise...</summary>
        public Dictionary<string, double> Size = new Dictionary<string, double>();
        /// <summary>For bands and joints: the heights (mm above datum) of the lines drawn.</summary>
        public List<double> LineHeights;
        /// <summary>For bands: where along the facade it runs (mm).</summary>
        public List<(double u0, double u1)> Runs;
        public string Build, Depth, Note;
    }

    public class FacadeWindowBox
    {
        public int Index;
        public double U0, U1, Z0, Z1;
        public double W => U1 - U0;
        public double H => Z1 - Z0;
        public double Cu => (U0 + U1) / 2;
    }

    public class FacadeType
    {
        public string Id, Kind, Signature;
        public int Count;
        public List<string> Items = new List<string>();
    }

    public class FacadeMapResult
    {
        public double Length;
        public List<FacadeWindowBox> Windows = new List<FacadeWindowBox>();
        public List<FacadeItem> Items = new List<FacadeItem>();
        public List<FacadeType> Types = new List<FacadeType>();
        /// <summary>Lines per layer inside the facade, and what the map did with each layer.</summary>
        public Dictionary<string, int> Layers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> LayerUse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int DetailLines, Explained;
    }

    public class FacadeMapOptions
    {
        public string[] WindowLayers = { "*Window*", "*Door*", "*Glaz*", "Ss_25_30*" };
        public string[] IgnoreLayers = { "*Text*", "Zz_*", "*Level*", "*Dim*", "*Hatch*", "*Light*", "*Sign*", "*StreetFurn*", "*Service*", "*Grid*", "*Title*", "*Frame*", "*Border*", "*Profile*", "*Pipe*", "*Drain*", "*Rwp*", "*Tree*", "*Veg*", "Defpoints" };
        /// <summary>A height drawn along at least this share of the facade is a band line.</summary>
        public double BandCover = 0.25;
        /// <summary>Furthest a window surround's outer line may be from the opening (mm).</summary>
        public double MaxSurround = 450;
    }

    public static class FacadeMap
    {
        private class Seg
        {
            public double U0, Z0, U1, Z1; public string Layer; public bool Used;
            public double Len => Math.Sqrt((U1 - U0) * (U1 - U0) + (Z1 - Z0) * (Z1 - Z0));
            public double MinU => Math.Min(U0, U1); public double MaxU => Math.Max(U0, U1);
            public double MinZ => Math.Min(Z0, Z1); public double MaxZ => Math.Max(Z0, Z1);
            public bool H => Math.Abs(Z1 - Z0) <= Math.Max(2, 0.02 * Len);
            public bool V => Math.Abs(U1 - U0) <= Math.Max(2, 0.02 * Len);
            public double U => (U0 + U1) / 2; public double Z => (Z0 + Z1) / 2;
        }

        /// <param name="lines">The elevation's lines in facade coordinates: A.X/B.X = u (mm along), A.Y/B.Y = z (mm above datum).</param>
        public static FacadeMapResult Read(IEnumerable<DwgLine> lines, double facadeLength, FacadeMapOptions o = null)
        {
            o = o ?? new FacadeMapOptions();
            var r = new FacadeMapResult { Length = facadeLength };
            var win = new List<Seg>(); var det = new List<Seg>();
            foreach (var l in lines)
            {
                r.Layers.TryGetValue(l.Layer ?? "", out int n); r.Layers[l.Layer ?? ""] = n + 1;
                var s = new Seg { U0 = l.A.X, Z0 = l.A.Y, U1 = l.B.X, Z1 = l.B.Y, Layer = l.Layer ?? "" };
                if (s.Len < 1) continue;
                if (DwgFile.LayerMatches(s.Layer, o.WindowLayers)) { win.Add(s); r.LayerUse[s.Layer] = "windows"; }
                else if (DwgFile.LayerMatches(s.Layer, o.IgnoreLayers)) r.LayerUse[s.Layer] = "ignored (annotation, services, outline)";
                else { det.Add(s); r.LayerUse[s.Layer] = "detail"; }
            }
            r.DetailLines = det.Count;
            r.Windows = WindowBoxes(win);
            int next = 1;
            string NewId(string p) => p + (next++);

            // surrounds first: a row of alike sills or hoods would otherwise read as a band
            foreach (var w in r.Windows) Surround(r, det, w, o, NewId);
            Bands(r, det, o, NewId);
            Quoins(r, det, NewId);
            Pilasters(r, det, NewId);
            Components(r, det, NewId);

            r.Explained = det.Count(s => s.Used);
            Group(r);
            return r;
        }

        // ---------- windows ----------

        /// <summary>The openings an elevation draws: its window-layer lines (already in facade u/z) grouped where they touch.</summary>
        public static List<FacadeWindowBox> Openings(IEnumerable<DwgLine> windowLines) =>
            WindowBoxes(windowLines.Select(l => new Seg { U0 = l.A.X, Z0 = l.A.Y, U1 = l.B.X, Z1 = l.B.Y, Layer = l.Layer ?? "" }).Where(x => x.Len >= 1).ToList());

        /// <summary>Window-layer lines grouped where their boxes touch (30 mm); groups 300-8000 wide and 400-8000 tall are openings.</summary>
        private static List<FacadeWindowBox> WindowBoxes(List<Seg> win)
        {
            var groups = Cluster(win, 30, boxTouch: true);
            var boxes = new List<FacadeWindowBox>();
            foreach (var g in groups)
            {
                double u0 = g.Min(s => s.MinU), u1 = g.Max(s => s.MaxU), z0 = g.Min(s => s.MinZ), z1 = g.Max(s => s.MaxZ);
                if (u1 - u0 >= 300 && u1 - u0 <= 8000 && z1 - z0 >= 400 && z1 - z0 <= 8000)
                    boxes.Add(new FacadeWindowBox { U0 = u0, U1 = u1, Z0 = z0, Z1 = z1 });
            }
            boxes = boxes.OrderBy(b => Math.Round(b.Z0 / 500)).ThenBy(b => b.U0).ToList();
            for (int i = 0; i < boxes.Count; i++) boxes[i].Index = i;
            return boxes;
        }

        // ---------- bands and rustication ----------

        private static void Bands(FacadeMapResult r, List<Seg> det, FacadeMapOptions o, Func<string, string> id)
        {
            double L = Math.Max(1, r.Length);
            var hs = det.Where(s => !s.Used && s.H && s.Len >= 200).OrderBy(s => s.Z).ToList();
            // heights within 6 mm are one line
            var levels = new List<(double z, List<Seg> segs, List<(double, double)> spans, double cover)>();
            int i = 0;
            while (i < hs.Count)
            {
                int j = i; var segs = new List<Seg>();
                while (j < hs.Count && hs[j].Z - hs[i].Z <= 6) segs.Add(hs[j++]);
                var spans = Union(segs.Select(s => (Math.Max(0, s.MinU), Math.Min(L, s.MaxU))), 20);
                double cover = spans.Sum(x => x.Item2 - x.Item1) / L;
                levels.Add((segs.Average(s => s.Z), segs, spans, cover));
                i = j;
            }
            double coverMin = o.BandCover;
            // rustication: at least 4 joints in a row at a steady pitch of 150-800, each drawn along a fair share of the facade
            var joints = levels.Where(x => x.cover >= 0.15).ToList();
            var rusticated = new HashSet<double>();
            for (int a = 0; a < joints.Count; a++)
            {
                int b = a + 1;
                double pitch = b < joints.Count ? joints[b].z - joints[a].z : 0;
                if (pitch < 150 || pitch > 800) continue;
                while (b + 1 < joints.Count && Math.Abs(joints[b + 1].z - joints[b].z - pitch) <= Math.Max(25, pitch * 0.15)) b++;
                int count = b - a + 1;
                if (count >= 4)
                {
                    var run = joints.Skip(a).Take(count).ToList();
                    foreach (var x in run) { rusticated.Add(x.z); foreach (var s in x.segs) s.Used = true; }
                    var allSpans = Union(run.SelectMany(x => x.spans), 400);
                    r.Items.Add(new FacadeItem
                    {
                        Id = id("R"), Kind = "rustication", U0 = allSpans.Min(x => x.Item1), U1 = allSpans.Max(x => x.Item2), Z0 = run[0].z, Z1 = run[count - 1].z,
                        Lines = run.Sum(x => x.segs.Count), Count = count, LineHeights = run.Select(x => Math.Round(x.z)).ToList(), Runs = allSpans,
                        Size = { ["pitch"] = Math.Round((run[count - 1].z - run[0].z) / (count - 1)), ["joints"] = count },
                        Build = "Channelled joints: a wall sweep of type Reveal (a small V or square groove Profile family, about 20 x 20) at each joint height over each run. Editable, follows the wall. Not yet automated: build the reveal type once, then one sweep per joint height.",
                        Depth = "Joint depth is not on the elevation: a section through the base (facade_profile), the reference model, or 20 mm.",
                    });
                    a = b;
                }
            }
            var bandLevels = levels.Where(x => x.cover >= coverMin && !rusticated.Contains(x.z) && x.spans.Sum(s => s.Item2 - s.Item1) >= 1500).ToList();
            double zMin = det.Count > 0 ? det.Min(s => s.MinZ) : 0, zMax = det.Count > 0 ? det.Max(s => s.MaxZ) : 0;
            int k = 0;
            while (k < bandLevels.Count)
            {
                int m = k;
                while (m + 1 < bandLevels.Count && bandLevels[m + 1].z - bandLevels[m].z <= 150) m++;
                var grp = bandLevels.Skip(k).Take(m - k + 1).ToList();
                foreach (var x in grp) foreach (var s in x.segs) s.Used = true;
                var runs = Union(grp.SelectMany(x => x.spans), 400).Where(x => x.Item2 - x.Item1 >= 300).ToList();
                double z0 = grp[0].z, z1 = grp[grp.Count - 1].z;
                string kind = z1 >= zMax - 600 && grp.Count >= 2 ? "cornice"
                    : z0 <= zMin + 1500 ? "plinth"
                    : grp.Count >= 3 ? "entablature band" : "string course";
                r.Items.Add(new FacadeItem
                {
                    Id = id("B"), Kind = kind, U0 = runs.Count > 0 ? runs.Min(x => x.Item1) : 0, U1 = runs.Count > 0 ? runs.Max(x => x.Item2) : L,
                    Z0 = z0, Z1 = z1, Lines = grp.Sum(x => x.segs.Count), LineHeights = grp.Select(x => Math.Round(x.z)).ToList(), Runs = runs,
                    Size = { ["height"] = Math.Round(z1 - z0), ["lines"] = grp.Count, ["cover"] = Math.Round(grp.Max(x => x.cover), 2) },
                    Build = "facade_profile (the section's true profile as a Profile family + native wall sweeps; it sweeps only where the elevation draws these lines). If no section crosses this facade, the band is still in facade_relief as faces; the line heights here are the fasciae to draw a profile from.",
                    Depth = "Projection from the section that cuts this facade (facade_profile reads it); plan or roof plan edge for the top cornice; otherwise the reference model.",
                });
                k = m + 1;
            }
        }

        // ---------- window surrounds ----------

        private static void Surround(FacadeMapResult r, List<Seg> det, FacadeWindowBox w, FacadeMapOptions o, Func<string, string> id)
        {
            double max = o.MaxSurround, h = w.H;
            bool Beside(Seg s) => Math.Min(s.MaxZ, w.Z1) - Math.Max(s.MinZ, w.Z0) >= 0.6 * h;
            var left = det.Where(s => !s.Used && s.V && s.U >= w.U0 - max && s.U <= w.U0 + 15 && Beside(s)).ToList();
            var right = det.Where(s => !s.Used && s.V && s.U <= w.U1 + max && s.U >= w.U1 - 15 && Beside(s)).ToList();
            var offs = new List<double>(); var used = new List<Seg>();
            foreach (var a in left.OrderByDescending(s => s.U))
            {
                double oa = w.U0 - a.U;
                var b = right.Where(s => Math.Abs((s.U - w.U1) - oa) <= 15).OrderBy(s => Math.Abs((s.U - w.U1) - oa)).FirstOrDefault();
                if (b == null) continue;
                if (offs.Count == 0 || Math.Abs(offs[offs.Count - 1] - oa) > 8) offs.Add(Math.Round(oa));
                used.Add(a); used.Add(b);
            }
            var faces = offs.Where(x => x > 10).ToList();
            double arch = faces.Count > 0 ? faces.Max() : 0;
            // the head of the surround: lines across the top within the architrave's width above the opening
            var head = det.Where(s => !s.Used && s.H && s.Z >= w.Z1 - 15 && s.Z <= w.Z1 + Math.Max(arch, 60) + 20 && s.MinU <= w.U0 + 20 && s.MaxU >= w.U1 - 20 && s.Len <= w.W + 2 * arch + 600).ToList();
            if (faces.Count > 0)
            {
                foreach (var s in used.Concat(head)) s.Used = true;
                bool moulded = faces.Count >= 2;
                r.Items.Add(new FacadeItem
                {
                    Id = id("S"), Kind = moulded ? "architrave (moulded)" : "architrave (flat)", Window = w.Index,
                    U0 = w.U0 - arch, U1 = w.U1 + arch, Z0 = w.Z0, Z1 = w.Z1 + arch, Lines = used.Count + head.Count,
                    Size = { ["width"] = arch, ["steps"] = faces.Count, ["openingWidth"] = Math.Round(w.W), ["openingHeight"] = Math.Round(w.H) },
                    Note = "Line offsets from the opening: " + string.Join(", ", faces.Select(x => x.ToString("0"))) + " mm",
                    Build = moulded
                        ? "One surround family per type: a sweep of this stepped moulding along the opening's outline (up a jamb, across the head, down the other). facade_relief already makes one family per window design from the faces; use the type and steps here to check or rationalise it into a swept profile."
                        : $"Window family architrave (architrave_mm={arch:0}) if the window family offers it, else part of the facade_relief surround family for this window design.",
                    Depth = "Projection from the plan (the surround shows as a step in the wall face beside the window at the plan cut) or the reference model; flat architraves are usually 15-30 mm proud.",
                });
            }
            double outer = arch;
            // sill: a line under the opening running past both jambs
            var sill = det.Where(s => !s.Used && s.H && s.Z >= w.Z0 - 250 && s.Z <= w.Z0 + 30 && s.MinU <= w.U0 - outer - 15 && s.MaxU >= w.U1 + outer + 15 && s.Len <= w.W + 2 * outer + 1200).ToList();
            if (sill.Count > 0)
            {
                foreach (var s in sill) s.Used = true;
                double su0 = sill.Min(s => s.MinU), su1 = sill.Max(s => s.MaxU), sz0 = sill.Min(s => s.Z), sz1 = sill.Max(s => s.Z);
                r.Items.Add(new FacadeItem
                {
                    Id = id("L"), Kind = "sill", Window = w.Index, U0 = su0, U1 = su1, Z0 = sz0, Z1 = Math.Max(sz1, w.Z0), Lines = sill.Count,
                    Size = { ["length"] = Math.Round(su1 - su0), ["horns"] = Math.Round(((w.U0 - su0) + (su1 - w.U1)) / 2), ["height"] = Math.Round(Math.Max(sz1, w.Z0) - sz0) },
                    Build = "Part of the window's surround family (an extrusion under the opening, this length and height, weathered top). One per type.",
                    Depth = "Sill projection from the section or the plan (often 40-80 mm past the wall face).",
                });
            }
            // above the head: hood, pediment, keystone, consoles
            double top = w.Z1 + outer, zoneU0 = w.U0 - outer - 500, zoneU1 = w.U1 + outer + 500;
            double ceiling = r.Windows.Where(x => x.Index != w.Index && x.U1 > w.U0 && x.U0 < w.U1 && x.Z0 > w.Z1).Select(x => x.Z0 - 50).DefaultIfEmpty(top + 1500).Min();
            ceiling = Math.Min(ceiling, top + 1500);
            var zone = det.Where(s => !s.Used && s.MinU >= zoneU0 && s.MaxU <= zoneU1 && s.MinZ >= w.Z1 - 120 && s.MaxZ <= ceiling).ToList();
            var hood = zone.Where(s => s.H && s.Len >= w.W + 2 * outer + 40 && s.Len <= w.W + 2 * outer + 1500 && s.Z >= top - 20 && s.Z <= top + 800).ToList();
            double hoodZ0 = 0, hoodZ1 = 0;
            if (hood.Count >= 2)
            {
                foreach (var s in hood) s.Used = true;
                hoodZ0 = hood.Min(s => s.Z); hoodZ1 = hood.Max(s => s.Z);
                double hu0 = hood.Min(s => s.MinU), hu1 = hood.Max(s => s.MaxU);
                r.Items.Add(new FacadeItem
                {
                    Id = id("H"), Kind = "hood (window cornice)", Window = w.Index, U0 = hu0, U1 = hu1, Z0 = hoodZ0, Z1 = hoodZ1, Lines = hood.Count,
                    LineHeights = hood.Select(s => Math.Round(s.Z)).Distinct().OrderBy(z => z).ToList(),
                    Size = { ["length"] = Math.Round(hu1 - hu0), ["height"] = Math.Round(hoodZ1 - hoodZ0), ["overhang"] = Math.Round(((w.U0 - hu0) + (hu1 - w.U1)) / 2) },
                    Build = "Part of the surround family: a sweep straight across with a profile from these line heights (a small cornice), returned at both ends. One per type.",
                    Depth = "Projection from a section through the window or the reference model (hoods typically 100-250 mm).",
                });
                // consoles: shapes under the hood's ends, outside the opening
                var under = zone.Where(s => !s.Used && s.MaxZ <= hoodZ0 + 30 && s.MinZ >= w.Z1 - 900 && (s.MaxU <= w.U0 + 10 || s.MinU >= w.U1 - 10)).ToList();
                foreach (var side in new[] { under.Where(s => s.U < w.Cu).ToList(), under.Where(s => s.U > w.Cu).ToList() })
                {
                    if (side.Count < 2) continue;
                    double cu0 = side.Min(s => s.MinU), cu1 = side.Max(s => s.MaxU), cz0 = side.Min(s => s.MinZ), cz1 = side.Max(s => s.MaxZ);
                    if (cu1 - cu0 < 50 || cu1 - cu0 > 500 || cz1 - cz0 < 120) continue;
                    foreach (var s in side) s.Used = true;
                    r.Items.Add(new FacadeItem
                    {
                        Id = id("C"), Kind = "console", Window = w.Index, U0 = cu0, U1 = cu1, Z0 = cz0, Z1 = cz1, Lines = side.Count,
                        Size = { ["width"] = Math.Round(cu1 - cu0), ["height"] = Math.Round(cz1 - cz0) },
                        Build = "A console family (reference_copy types_only from the reference if its console matches this size; else an extrusion of this outline in a Generic Model family) placed with place_family at each console.",
                        Depth = "Console depth usually equals the hood's projection.",
                    });
                }
            }
            // pediment: sloped lines rising to a peak over the middle; a curved one is many short pieces
            var slopes = zone.Where(s => !s.Used && !s.H && !s.V && s.MinZ >= w.Z1 - 20).ToList();
            var rising = slopes.Where(s => s.Len >= 200 && Slope(s) > 0.14 && Slope(s) < 1.2 && s.U < w.Cu + 50).ToList();
            var falling = slopes.Where(s => s.Len >= 200 && Slope(s) < -0.14 && Slope(s) > -1.2 && s.U > w.Cu - 50).ToList();
            var pieces = slopes.Where(s => s.Len < 250).ToList();
            if (rising.Count > 0 && falling.Count > 0)
            {
                var all = rising.Concat(falling).ToList();
                foreach (var s in all) s.Used = true;
                double pu0 = all.Min(s => s.MinU), pu1 = all.Max(s => s.MaxU), pz0 = all.Min(s => s.MinZ), pz1 = all.Max(s => s.MaxZ);
                r.Items.Add(new FacadeItem
                {
                    Id = id("P"), Kind = "pediment (triangular)", Window = w.Index, U0 = pu0, U1 = pu1, Z0 = pz0, Z1 = pz1, Lines = all.Count,
                    Size = { ["span"] = Math.Round(pu1 - pu0), ["rise"] = Math.Round(pz1 - pz0), ["pitchDeg"] = Math.Round(Math.Atan(Math.Abs(rising.Average(Slope))) * 180 / Math.PI, 1), ["mouldingLines"] = rising.Count },
                    Build = "Part of the surround family: the raking cornice as a sweep of the hood's profile up each slope, the tympanum as an extrusion of the triangle set back from it. One per type.",
                    Depth = "Same projection as the hood or band below it.",
                });
            }
            else if (pieces.Count >= 6 && pieces.Max(s => s.MaxU) - pieces.Min(s => s.MinU) >= 0.5 * w.W && Math.Abs(pieces.Average(s => s.U) - w.Cu) <= 0.15 * w.W)
            {
                foreach (var s in pieces) s.Used = true;
                double pu0 = pieces.Min(s => s.MinU), pu1 = pieces.Max(s => s.MaxU), pz0 = pieces.Min(s => s.MinZ), pz1 = pieces.Max(s => s.MaxZ);
                r.Items.Add(new FacadeItem
                {
                    Id = id("P"), Kind = "pediment or arch (curved)", Window = w.Index, U0 = pu0, U1 = pu1, Z0 = pz0, Z1 = pz1, Lines = pieces.Count,
                    Size = { ["span"] = Math.Round(pu1 - pu0), ["rise"] = Math.Round(pz1 - pz0) },
                    Build = "Segmental pediment: part of the surround family, a sweep of the hood's profile along the arc (span, rise). If the arc is the window head itself it belongs to the window family (arched head, window_look).",
                    Depth = "Same projection as the hood; arched heads: the window family.",
                });
            }
            // keystone: a small shape centred on the head, standing above it
            var key = zone.Where(s => !s.Used && Math.Abs(s.U - w.Cu) <= 250 && s.MinZ >= w.Z1 - 120 && s.MaxZ <= w.Z1 + 650 && s.Len >= 40 && s.Len <= 700).ToList();
            if (key.Count >= 2)
            {
                double ku0 = key.Min(s => s.MinU), ku1 = key.Max(s => s.MaxU), kz0 = key.Min(s => s.MinZ), kz1 = key.Max(s => s.MaxZ);
                if (ku1 - ku0 >= 60 && ku1 - ku0 <= 500 && kz1 - kz0 >= 100 && Math.Abs((ku0 + ku1) / 2 - w.Cu) <= 40)
                {
                    foreach (var s in key) s.Used = true;
                    r.Items.Add(new FacadeItem
                    {
                        Id = id("K"), Kind = "keystone", Window = w.Index, U0 = ku0, U1 = ku1, Z0 = kz0, Z1 = kz1, Lines = key.Count,
                        Size = { ["width"] = Math.Round(ku1 - ku0), ["height"] = Math.Round(kz1 - kz0) },
                        Build = $"Window family keystone (keystone_width_mm={ku1 - ku0:0}) if the window family offers it, else an extrusion of this outline in the surround family, 30-60 mm proud.",
                        Depth = "Usually 30-60 mm proud of the architrave.",
                    });
                }
            }
        }

        private static double Slope(Seg s) { double du = s.U1 - s.U0; return Math.Abs(du) < 1e-6 ? 1e9 : (s.Z1 - s.Z0) / du; }

        // ---------- quoins and pilasters ----------

        private static void Quoins(FacadeMapResult r, List<Seg> det, Func<string, string> id)
        {
            foreach (int end in new[] { 0, 1 })
            {
                double corner = end == 0 ? 0 : r.Length;
                // short horizontals that start at the corner
                var hs = det.Where(s => !s.Used && s.H && s.Len >= 150 && s.Len <= 1000
                                        && Math.Abs((end == 0 ? s.MinU : s.MaxU) - corner) <= 40).OrderBy(s => s.Z).ToList();
                if (hs.Count < 6) continue;
                var lens = hs.Select(s => s.Len).ToList();
                double longL = lens.Max(), shortL = lens.Min();
                if (longL - shortL < 80) continue;
                int alternations = 0;
                for (int i = 1; i < lens.Count; i++) if ((lens[i] > (longL + shortL) / 2) != (lens[i - 1] > (longL + shortL) / 2)) alternations++;
                if (alternations < hs.Count / 3) continue;
                var vs = det.Where(s => !s.Used && s.V && Math.Abs(s.U - corner) <= longL + 20 && s.MinZ >= hs[0].Z - 50 && s.MaxZ <= hs[hs.Count - 1].Z + 50).ToList();
                foreach (var s in hs.Concat(vs)) s.Used = true;
                r.Items.Add(new FacadeItem
                {
                    Id = id("Q"), Kind = "quoins", U0 = end == 0 ? 0 : r.Length - longL, U1 = end == 0 ? longL : r.Length, Z0 = hs[0].Z, Z1 = hs[hs.Count - 1].Z,
                    Lines = hs.Count + vs.Count, Count = hs.Count,
                    Size = { ["long"] = Math.Round(longL), ["short"] = Math.Round(shortL), ["course"] = Math.Round((hs[hs.Count - 1].Z - hs[0].Z) / Math.Max(1, hs.Count - 1)) },
                    LineHeights = hs.Select(s => Math.Round(s.Z)).ToList(),
                    Build = "A quoin family (long and short block types, an extrusion each) placed up the corner on both facades with place_family, a little proud of the wall; or reveals if flush. facade_relief makes them as faces meanwhile.",
                    Depth = "Quoins are often flush with channelled joints, or 10-25 mm proud: check the plan corner.",
                });
            }
        }

        private static void Pilasters(FacadeMapResult r, List<Seg> det, Func<string, string> id)
        {
            var vs = det.Where(s => !s.Used && s.V && s.Len >= 1200).OrderBy(s => s.U).ToList();
            var taken = new HashSet<Seg>();
            var found = new List<(double u0, double u1, double z0, double z1, List<Seg> segs)>();
            foreach (var a in vs)
            {
                if (taken.Contains(a)) continue;
                var b = vs.Where(s => s != a && !taken.Contains(s) && s.U - a.U >= 150 && s.U - a.U <= 1200
                                      && Math.Min(s.MaxZ, a.MaxZ) - Math.Max(s.MinZ, a.MinZ) >= 0.7 * Math.Min(s.Len, a.Len))
                    .OrderBy(s => s.U - a.U).FirstOrDefault();
                if (b == null) continue;
                double z0 = Math.Max(a.MinZ, b.MinZ), z1 = Math.Min(a.MaxZ, b.MaxZ);
                // a window between them means they are two sides of different things
                if (r.Windows.Any(w => w.U0 < b.U - 20 && w.U1 > a.U + 20 && w.Z0 < z1 && w.Z1 > z0)) continue;
                taken.Add(a); taken.Add(b);
                found.Add((a.U, b.U, Math.Min(a.MinZ, b.MinZ), Math.Max(a.MaxZ, b.MaxZ), new List<Seg> { a, b }));
            }
            // stacked pieces of one pilaster (broken by a band) are one
            foreach (var g in found.GroupBy(f => (Math.Round(f.u0 / 25), Math.Round(f.u1 / 25))))
            {
                var p = g.OrderBy(x => x.z0).ToList();
                double u0 = p.Average(x => x.u0), u1 = p.Average(x => x.u1), z0 = p.Min(x => x.z0), z1 = p.Max(x => x.z1);
                var segs = p.SelectMany(x => x.segs).ToList();
                // capital and base: short horizontals across it near its top and bottom
                var caps = det.Where(s => !s.Used && s.H && s.MinU >= u0 - 250 && s.MaxU <= u1 + 250 && s.Len >= (u1 - u0) * 0.8
                                          && (Math.Abs(s.Z - z1) <= 600 || Math.Abs(s.Z - z0) <= 400)).ToList();
                foreach (var s in segs.Concat(caps)) s.Used = true;
                r.Items.Add(new FacadeItem
                {
                    Id = id("V"), Kind = "pilaster", U0 = u0, U1 = u1, Z0 = z0, Z1 = z1, Lines = segs.Count + caps.Count,
                    Size = { ["width"] = Math.Round(u1 - u0), ["height"] = Math.Round(z1 - z0), ["capitalAndBaseLines"] = caps.Count },
                    LineHeights = caps.Select(s => Math.Round(s.Z)).Distinct().OrderBy(z => z).ToList(),
                    Build = "facade_columns (the plan draws its section: extruded between the heights the elevation draws, as a family). Capital and base: sweeps round three sides from these line heights in the same family. Reference families (EF_25_10-Wall_Ext_Detail) can be copied with reference_copy types_only where they match.",
                    Depth = "Projection from the plan at that floor (the pilaster is a step out of the wall face on the plan) or the reference model.",
                });
            }
        }

        // ---------- what is left: balustrades, consoles under bands, other shapes ----------

        private static void Components(FacadeMapResult r, List<Seg> det, Func<string, string> id)
        {
            var rest = det.Where(s => !s.Used && s.Len <= 1500).ToList();
            var comps = Cluster(rest, 5, boxTouch: false)
                .Select(g => (segs: g, u0: g.Min(s => s.MinU), u1: g.Max(s => s.MaxU), z0: g.Min(s => s.MinZ), z1: g.Max(s => s.MaxZ))).ToList();

            // balusters: small upright shapes of one size standing on one line at a steady pitch
            var balusterLike = comps.Where(c => c.u1 - c.u0 >= 50 && c.u1 - c.u0 <= 350 && c.z1 - c.z0 >= 250 && c.z1 - c.z0 <= 1100 && c.z1 - c.z0 > 1.5 * (c.u1 - c.u0)).ToList();
            var usedComps = new HashSet<List<Seg>>();
            foreach (var row in balusterLike.GroupBy(c => (Math.Round(c.z0 / 40), Math.Round((c.z1 - c.z0) / 40))))
            {
                var bs = row.OrderBy(c => c.u0).ToList();
                if (bs.Count < 5) continue;
                var gaps = bs.Zip(bs.Skip(1), (a, b) => (b.u0 + b.u1) / 2 - (a.u0 + a.u1) / 2).ToList();
                double pitch = gaps.OrderBy(g => g).ElementAt(gaps.Count / 2);
                // split where a gap is far over the pitch (a pier between two runs)
                var runs = new List<List<(List<Seg> segs, double u0, double u1, double z0, double z1)>> { new List<(List<Seg>, double, double, double, double)> { bs[0] } };
                for (int i = 1; i < bs.Count; i++)
                {
                    if (gaps[i - 1] > pitch * 1.8) runs.Add(new List<(List<Seg>, double, double, double, double)>());
                    runs[runs.Count - 1].Add(bs[i]);
                }
                foreach (var run in runs.Where(x => x.Count >= 4))
                {
                    foreach (var c in run) { usedComps.Add(c.segs); foreach (var s in c.segs) s.Used = true; }
                    double u0 = run.Min(c => c.u0), u1 = run.Max(c => c.u1), z0 = run.Min(c => c.z0), z1 = run.Max(c => c.z1);
                    var rails = det.Where(s => !s.Used && s.H && s.MinU <= u0 + 50 && s.MaxU >= u1 - 50 && ((s.Z >= z1 - 20 && s.Z <= z1 + 300) || (s.Z <= z0 + 20 && s.Z >= z0 - 300))).ToList();
                    foreach (var s in rails) s.Used = true;
                    r.Items.Add(new FacadeItem
                    {
                        Id = id("U"), Kind = "balustrade", U0 = u0, U1 = u1, Z0 = rails.Count > 0 ? Math.Min(z0, rails.Min(s => s.Z)) : z0, Z1 = rails.Count > 0 ? Math.Max(z1, rails.Max(s => s.Z)) : z1,
                        Lines = run.Sum(c => c.segs.Count) + rails.Count, Count = run.Count,
                        Size = { ["balusters"] = run.Count, ["pitch"] = Math.Round(pitch), ["balusterWidth"] = Math.Round(run.Average(c => c.u1 - c.u0)), ["balusterHeight"] = Math.Round(z1 - z0) },
                        LineHeights = rails.Select(s => Math.Round(s.Z)).Distinct().OrderBy(z => z).ToList(),
                        Build = "Railing with a baluster family (the reference uses Pr_25_30_36 railings with Baluster_Square / round balusters: reference_copy types_only), placed along this run at this pitch; or a baluster family placed with place_family. Top rail and plinth from these line heights.",
                        Depth = "Balusters are round: depth = width. Rail and plinth projections from the section or reference.",
                    });
                }
            }

            // consoles under a band: two or more alike small shapes whose tops touch a band's underside
            foreach (var band in r.Items.Where(i => i.Kind == "cornice" || i.Kind == "entablature band" || i.Kind == "string course").ToList())
            {
                var under = comps.Where(c => !usedComps.Contains(c.segs) && Math.Abs(c.z1 - band.Z0) <= 80 && c.u1 - c.u0 >= 50 && c.u1 - c.u0 <= 500 && c.z1 - c.z0 >= 120 && c.z1 - c.z0 <= 1200).ToList();
                foreach (var g in under.GroupBy(c => (Math.Round((c.u1 - c.u0) / 30), Math.Round((c.z1 - c.z0) / 30))).Where(g => g.Count() >= 2))
                {
                    var cs = g.OrderBy(c => c.u0).ToList();
                    foreach (var c in cs) { usedComps.Add(c.segs); foreach (var s in c.segs) s.Used = true; }
                    var centres = cs.Select(c => (c.u0 + c.u1) / 2).ToList();
                    r.Items.Add(new FacadeItem
                    {
                        Id = id("C"), Kind = band.Kind == "cornice" ? "modillions / brackets under the cornice" : "consoles under a band",
                        U0 = cs.Min(c => c.u0), U1 = cs.Max(c => c.u1), Z0 = cs.Min(c => c.z0), Z1 = cs.Max(c => c.z1), Count = cs.Count, Lines = cs.Sum(c => c.segs.Count),
                        Size = { ["width"] = Math.Round(cs.Average(c => c.u1 - c.u0)), ["height"] = Math.Round(cs.Average(c => c.z1 - c.z0)), ["pitch"] = cs.Count > 1 ? Math.Round((centres[centres.Count - 1] - centres[0]) / (cs.Count - 1)) : 0 },
                        Note = $"Under {band.Id}; centres at " + string.Join(", ", centres.Take(30).Select(c => c.ToString("0"))),
                        Build = "One console/modillion family (reference_copy types_only if the reference's matches, else an extrusion of this outline in a Generic Model family) placed with place_family at each centre, just under the band.",
                        Depth = "Usually the band's projection.",
                    });
                }
            }

            // other: anything sizeable left, for a look
            foreach (var c in comps.Where(c => !usedComps.Contains(c.segs) && c.segs.Count >= 3 && (c.u1 - c.u0) * (c.z1 - c.z0) >= 40000)
                                   .OrderByDescending(c => (c.u1 - c.u0) * (c.z1 - c.z0)).Take(60))
            {
                var near = r.Windows.FirstOrDefault(w => c.u1 >= w.U0 - 600 && c.u0 <= w.U1 + 600 && c.z1 >= w.Z0 - 600 && c.z0 <= w.Z1 + 1200);
                r.Items.Add(new FacadeItem
                {
                    Id = id("X"), Kind = "unclassified", Window = near?.Index, U0 = c.u0, U1 = c.u1, Z0 = c.z0, Z1 = c.z1, Lines = c.segs.Count,
                    Size = { ["width"] = Math.Round(c.u1 - c.u0), ["height"] = Math.Round(c.z1 - c.z0) },
                    Note = "Layers: " + string.Join(", ", c.segs.Select(s => s.Layer).Distinct().Take(4)),
                    Build = "Look first (image true, or dwg_elevation include_lines on this box) and say what it is. facade_relief already builds it as faces; make a family only if it repeats.",
                    Depth = "Unknown: plan, section or reference.",
                });
            }
        }

        // ---------- types: repeats made once, placed many times ----------

        private static void Group(FacadeMapResult r)
        {
            string Sig(FacadeItem i)
            {
                if (i.Kind == "unclassified" || i.Kind == "rustication" || i.Kind == "quoins") return null;
                if (i.Runs != null) return null; // bands are one item per height
                var parts = i.Size.Where(kv => kv.Key != "length" || i.Window == null).OrderBy(kv => kv.Key)
                    .Select(kv => kv.Key + "=" + (Math.Round(kv.Value / 25) * 25).ToString("0"));
                return i.Kind + " " + string.Join(" ", parts);
            }
            int n = 1;
            foreach (var g in r.Items.Select(i => (i, s: Sig(i))).Where(x => x.s != null).GroupBy(x => x.s).OrderByDescending(g => g.Count()))
            {
                var t = new FacadeType { Id = "T" + n++, Kind = g.First().i.Kind, Signature = g.Key, Count = g.Count(), Items = g.Select(x => x.i.Id).ToList() };
                foreach (var x in g) x.i.Type = t.Id;
                r.Types.Add(t);
            }
        }

        // ---------- helpers ----------

        private static List<(double, double)> Union(IEnumerable<(double lo, double hi)> spans, double gap)
        {
            var res = new List<(double, double)>();
            foreach (var (lo, hi) in spans.Where(s => s.hi > s.lo).OrderBy(s => s.lo))
            {
                if (res.Count > 0 && lo <= res[res.Count - 1].Item2 + gap) res[res.Count - 1] = (res[res.Count - 1].Item1, Math.Max(res[res.Count - 1].Item2, hi));
                else res.Add((lo, hi));
            }
            return res;
        }

        /// <summary>Groups of segments that touch: by their boxes (boxTouch) or by the segments themselves, within tol mm.</summary>
        private static List<List<Seg>> Cluster(List<Seg> segs, double tol, bool boxTouch)
        {
            var order = segs.OrderBy(s => s.MinU).ToList();
            var parent = Enumerable.Range(0, order.Count).ToArray();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int i = 0; i < order.Count; i++)
            {
                var a = order[i];
                for (int j = i + 1; j < order.Count && order[j].MinU <= a.MaxU + tol; j++)
                {
                    var b = order[j];
                    if (b.MinZ > a.MaxZ + tol || b.MaxZ < a.MinZ - tol) continue;
                    if (!boxTouch && SegDist(a, b) > tol) continue;
                    int ra = Find(i), rb = Find(j);
                    if (ra != rb) parent[ra] = rb;
                }
            }
            return Enumerable.Range(0, order.Count).GroupBy(Find).Select(g => g.Select(k => order[k]).ToList()).ToList();
        }

        private static double SegDist(Seg a, Seg b)
        {
            if (Cross(a, b)) return 0;
            return Math.Min(Math.Min(PtSeg(a.U0, a.Z0, b), PtSeg(a.U1, a.Z1, b)), Math.Min(PtSeg(b.U0, b.Z0, a), PtSeg(b.U1, b.Z1, a)));
        }

        private static bool Cross(Seg a, Seg b)
        {
            double d1 = Orient(b.U0, b.Z0, b.U1, b.Z1, a.U0, a.Z0), d2 = Orient(b.U0, b.Z0, b.U1, b.Z1, a.U1, a.Z1);
            double d3 = Orient(a.U0, a.Z0, a.U1, a.Z1, b.U0, b.Z0), d4 = Orient(a.U0, a.Z0, a.U1, a.Z1, b.U1, b.Z1);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static double Orient(double ax, double ay, double bx, double by, double cx, double cy) => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

        private static double PtSeg(double x, double y, Seg s)
        {
            double dx = s.U1 - s.U0, dy = s.Z1 - s.Z0, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((x - s.U0) * dx + (y - s.Z0) * dy) / l2));
            double px = s.U0 + t * dx - x, py = s.Z0 + t * dy - y;
            return Math.Sqrt(px * px + py * py);
        }
    }
}
