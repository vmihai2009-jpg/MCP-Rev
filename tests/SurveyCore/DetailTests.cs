using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class DetailTests
    {
        static Seg S(double x1, double y1, double x2, double y2) => new Seg(new Pt(x1, y1), new Pt(x2, y2), 0);

        public static void Run()
        {
            Console.WriteLine("detail (1.9.0)");

            // planes
            var p = SurveyDetail.FitPlane(new List<Pt> { new Pt(0, 0, 1000), new Pt(4000, 0, 2000), new Pt(0, 4000, 1000), new Pt(4000, 4000, 2000) });
            T.Check(T.Near(p.A, 0.25, 1e-9) && T.Near(p.B, 0, 1e-9) && T.Near(p.At(2000, 0), 1500, 1e-6), "plane fit: slope along x, 1 in 4");
            T.Check(T.Near(p.SlopeDeg, Math.Atan(0.25) * 180 / Math.PI, 1e-6), "plane slope in degrees");
            var flat = SurveyDetail.FitPlane(new List<Pt> { new Pt(0, 0, 500), new Pt(3000, 0, 520) });
            T.Check(flat.Flat && T.Near(flat.C, 510, 1e-9), "two spots: flat at their mean");
            var line = SurveyDetail.FitPlane(new List<Pt> { new Pt(0, 0, 500), new Pt(1000, 0, 600), new Pt(2000, 0, 700) });
            T.Check(line.Flat, "spots in a line: no slope across them, so flat");

            // faces: a 10 x 6 m rectangle split by a ridge line into two, with a dangling line and a crossing line
            var segs = new List<Seg>
            {
                S(0, 0, 10000, 0), S(10000, 0, 10000, 6000), S(10000, 6000, 0, 6000), S(0, 6000, 0, 0),
                S(0, 3000, 10000, 3000),         // ridge: splits into two faces
                S(5000, 3000, 5000, 4000),       // dangling: dropped
            };
            var faces = SurveyDetail.Faces(segs);
            T.Check(faces.Count == 2, $"faces: a rectangle with a ridge is two faces ({faces.Count})");
            T.Check(faces.All(f => T.Near(GeoMath.SignedArea(f), 30e6, 1)), "faces: each 30 m2, counter-clockwise");
            T.Check(SurveyDetail.FaceAt(faces, new Pt(2000, 1000)) != SurveyDetail.FaceAt(faces, new Pt(2000, 5000)), "faces: a point finds its own face");
            var crossing = new List<Seg>(segs) { S(4000, -1000, 4000, 7000) }; // crosses the outline and the ridge
            T.Check(SurveyDetail.Faces(crossing).Count == 4, "faces: a line across splits where it crosses");

            // columns: two small squares among a long wall
            var col = new List<Seg> { S(0, 0, 500, 0), S(500, 0, 500, 400), S(500, 400, 0, 400), S(0, 400, 0, 0),
                                      S(3000, 0, 3700, 0), S(3700, 0, 3700, 700), S(3700, 700, 3000, 700), S(3000, 700, 3000, 0),
                                      S(-5000, 5000, 15000, 5000), S(-5000, 5300, 15000, 5300) };
            var cols = SurveyDetail.Columns(col);
            T.Check(cols.Count == 2, $"columns: two small outlines found, the open wall lines left ({cols.Count})");

            // stairs: 10 treads 1100 wide, 250 apart, plus a string line
            var st = new List<Seg>();
            for (int i = 0; i < 10; i++) st.Add(S(0, i * 250, 1100, i * 250));
            st.Add(S(0, 0, 0, 2250)); st.Add(S(1100, 0, 1100, 2250));
            var stairs = SurveyDetail.Stairs(st);
            T.Check(stairs.Count == 1 && stairs[0].Treads == 10 && T.Near(stairs[0].GoingMm, 250) && T.Near(stairs[0].WidthMm, 1100), "stairs: 10 treads, going 250, width 1100");

            // terraces: a 10 x 4 m outline, left half spots at 1000, right half at 1500
            var ter = SurveyDetail.Terraces(new List<Pt> { new Pt(0, 0), new Pt(10000, 0), new Pt(10000, 4000), new Pt(0, 4000) },
                new List<Pt> { new Pt(1000, 2000, 1000), new Pt(3000, 1000, 1010), new Pt(8000, 2000, 1500), new Pt(9000, 3000, 1490) });
            double Area(List<(double x0, double y0, double x1, double y1)> rs) => rs.Sum(r => (r.x1 - r.x0) * (r.y1 - r.y0));
            T.Check(ter.Count == 2 && T.Near(Area(ter[0].rects) + Area(ter[1].rects), 40e6, 1) && T.Near(ter[0].z, 1005, 1e-6), "terraces: two levels cover the outline exactly");
            T.Check(ter.All(t => t.rects.Count <= 50), $"terraces: cells merged into few rectangles ({ter[0].rects.Count}, {ter[1].rects.Count})");

            // merged openings: 4900 wide with two 230 piers drawn inside
            var parts = SurveyDetail.SplitSpan(-2450, 2450, new[] { -900.0, -670, 700, 930, 701 });
            T.Check(parts.Count == 3 && T.Near(parts[0].a, -2450) && T.Near(parts[0].b, -900) && T.Near(parts[1].a, -670) && T.Near(parts[1].b, 700) && T.Near(parts[2].a, 930), "split: a 4.9 m opening with two piers is three openings");
            T.Check(SurveyDetail.SplitSpan(0, 1200, new double[0]).Count == 1, "split: nothing inside, one opening");

            // sections: verticals at x = 1000 + t for walls at t = 0, 230, 5000, 5230, 9000
            var walls = new List<double> { 0, 230, 5000, 5230, 9000 };
            var vert = walls.Select(t => 71000 - t).ToList(); // drawn the other way round
            var place = SurveyDetail.PlaceSection(walls, vert);
            T.Check(place != null && place.Dir == -1 && T.Near(place.Offset, 71000, 1) && place.Matched == 5, "section placed on its cut line, mirrored");
            T.Check(T.Near(place.T(71000 - 5000), 5000, 1e-6), "section x back to the plan");
            var soff = SurveyDetail.Soffit(new[] { (0.0, 5000.0, 2700.0), (0.0, 5000.0, 3100.0), (0.0, 5000.0, 300.0) }, 2500, 0, 3500);
            T.Check(soff == 2700, "soffit: the lowest line above head height");

            // model compare
            var a = new List<Pt> { new Pt(0, 0), new Pt(10000, 0), new Pt(10000, 5000), new Pt(0, 5000), new Pt(4000, 2000) };
            var mv = new PlanMove { Angle = 0.02, Dx = 300, Dy = -120 };
            var b = a.Select(mv.Apply).ToList();
            var (fit, rms, n) = ModelCompare.Align(a, b, PlanMove.Identity, 3000);
            T.Check(T.Near(fit.Angle, 0.02, 1e-6) && T.Near(fit.Dx, 300, 0.5) && T.Near(fit.Dy, -120, 0.5) && rms < 1, "align: rotation and shift recovered");
            var far = new PlanMove { Angle = Math.PI / 2 + 0.01, Dx = 1e6, Dy = -2e5 };
            var grid = new List<Pt>(); for (int i = 0; i < 12; i++) for (int j = 0; j < 6; j++) if (i == 0 || j == 0 || i == 11 || j == 5 || i == 4) grid.Add(new Pt(i * 1000, j * 700));
            var (any, rmsAny, nAny) = ModelCompare.AlignAny(grid, grid.Select(far.Apply).ToList());
            T.Check(T.Near(any.Angle, far.Angle, 1e-4) && T.Near(any.Dx, far.Dx, 1) && rmsAny < 1, "align with no start: a quarter turn and a km away found");
            var r = new List<CmpItem> { new CmpItem { Id = 1, Key = "Windows", C = new Pt(0, 0, 0), Sx = 1000, Sy = 200, Sz = 1500 },
                                        new CmpItem { Id = 2, Key = "Windows", C = new Pt(3000, 0, 0), Sx = 1000, Sy = 200, Sz = 1500 } };
            var o = new List<CmpItem> { new CmpItem { Id = 11, Key = "Windows", C = new Pt(20, 0, 0), Sx = 1000, Sy = 200, Sz = 1500 } };
            var m = ModelCompare.Match(r, o, 1000);
            T.Check(m.Pairs.Count == 1 && m.Pairs[0].Ref.Id == 1 && m.Missing.Count == 1 && m.Missing[0].Id == 2, "compare: nearest pairs, the other is missing");

            WindowPatterns();
            Relief();
        }

        static void Relief()
        {
            Console.WriteLine("facade relief (1.14.2)");
            // plan: a facade along x, a 600 mm square pier standing 400 mm out at x 2000-2600, a wall face line at 0
            var plan = new List<Seg> { S(0, 0, 10000, 0), S(2000, 0, 2000, 400), S(2000, 400, 2600, 400), S(2600, 400, 2600, 0) };
            var prof = FacadeRelief.Profile(plan, new Pt(0, 0), 1, 0, 0, 1, 10000);
            T.Check(T.Near(prof.Over(2050, 2550), 400, 1) && prof.Over(4000, 5000) == 0, "relief profile: the pier stands 400 out, the wall face is not a projection");
            // elevation: the pier's outline (tall), a band across, a window box
            var el = new List<Seg>
            {
                S(2000, 0, 2000, 3000), S(2600, 0, 2600, 3000), S(0, 0, 10000, 0), S(0, 3000, 10000, 3000),   // pier sides, ground, top
                S(0, 3200, 10000, 3200), S(0, 3000, 0, 3200), S(10000, 3000, 10000, 3200),                    // a band 200 high on top
                S(0, 0, 0, 3000), S(10000, 0, 10000, 3000),
            };
            var faces = FacadeRelief.Build(el, new List<(double, double, double, double)>(), (z0, z1) => z0 <= 1000 && z1 >= 1000 ? prof : null, new ReliefOptions { MaxFaceM2 = 10 });
            var pier = faces.FirstOrDefault(f => f.U0 >= 1990 && f.U1 <= 2610);
            var bandF = faces.FirstOrDefault(f => f.Z0 >= 2990 && f.Z1 <= 3210);
            T.Check(pier != null && pier.Source == "plan" && T.Near(pier.Depth, 400, 1), "relief: the pier's face takes the plan's 400");
            T.Check(bandF != null && bandF.Source == "band" && bandF.Depth == 120, "relief: the band above takes the band depth (it doesn't cross the plan's cut height)");
            T.Check(faces.All(f => f.AreaM2 <= 10), "relief: no face bigger than the plain-wall limit");

            // section profile: wall face at 0 up to 3000, a cornice 400 out at 2800-3000, then a mansard from 3000 sloping back
            var sec = new List<(Pt, Pt)>
            {
                (new Pt(0, 0), new Pt(0, 2800)), (new Pt(0, 2800), new Pt(400, 2800)), (new Pt(400, 2800), new Pt(400, 3000)), (new Pt(400, 3000), new Pt(0, 3000)),
                (new Pt(0, 3000), new Pt(-800, 5000)),
            };
            var env = SectionProfile.Envelope(sec, 0, 5000);
            T.Check(T.Near(env[290], 400, 1) && T.Near(env[100], 0, 1) && T.Near(env[400], -400, 5), $"section envelope: cornice 400 out, wall at 0, mansard back ({env[290]}, {env[100]}, {env[400]})");
            // a fluted pilaster on a plan: 850 wide, 140 proud, flutes as small arcs; the elevation draws its edges 6000-16000
            var pil = new List<Seg> { S(0, -50, 0, 0), S(-2000, 0, 0, 0), S(0, 0, 0, 140) };
            double fx = 0;
            for (int k = 0; k < 6; k++, fx += 140)
            {
                pil.Add(S(fx, 140, fx + 50, 140));
                for (int q = 0; q < 6; q++) { double a0 = Math.PI * q / 6, a1 = Math.PI * (q + 1) / 6; pil.Add(S(fx + 70 - 20 * Math.Cos(a0), 140 - 20 * Math.Sin(a0), fx + 70 - 20 * Math.Cos(a1), 140 - 20 * Math.Sin(a1))); }
                pil.Add(S(fx + 90, 140, fx + 140, 140));
            }
            pil.Add(S(840, 140, 840, 0)); pil.Add(S(840, 0, 3000, 0));
            var pp = FacadeColumns.PlanPieces(pil, new Pt(-2000, 0), 1, 0, 0, 1, 6000);
            T.Check(pp.Count == 1 && T.Near(pp[0].Max(p => p.Y), 140, 1) && T.Near(pp[0].Max(p => p.X) - pp[0].Min(p => p.X), 840, 1) && pp[0].Count > 40, $"plan piece: the fluted pilaster's outline, flutes and all ({pp.Count}, {(pp.Count > 0 ? pp[0].Count : 0)} points)");
            var elv = new List<Seg> { S(2000, 6000, 2000, 16000), S(2840, 6000, 2840, 16000), S(1000, 0, 1000, 20000) };
            var run = FacadeColumns.EdgeRun(elv, 2000, 2840, 9000);
            T.Check(run != null && T.Near(run.Value.z0, 6000, 1) && T.Near(run.Value.z1, 16000, 1), "plan piece: its height from the elevation's edges");
            var regs = SectionProfile.Regions(env, 0, 10, 3000);
            T.Check(regs.Count == 2, $"section regions: the cornice and the mansard ({regs.Count})");
            T.Check(regs.Any(r => T.Near(r.Max(p => p.X), 400, 1) && r.Max(p => p.Y) <= 3000), "section regions: the cornice stands 400 out");
        }

        static DwgLine L(double x1, double y1, double x2, double y2) => new DwgLine { A = new Pt(x1, y1), B = new Pt(x2, y2), Layer = "Ss_25_30_95-Window" };
        static IEnumerable<DwgLine> Box(double x0, double y0, double x1, double y1) => new[] { L(x0, y0, x1, y0), L(x1, y0, x1, y1), L(x1, y1, x0, y1), L(x0, y1, x0, y0) };

        static void WindowPatterns()
        {
            Console.WriteLine("window patterns (1.9.17)");
            // a sliding sash: two panes with a 60 mm meeting rail, reveals 70 mm outside them
            var sash = Box(70, 60, 930, 1100).Concat(Box(70, 1160, 930, 2200)).ToList();
            var op = new ElevOpening { Left = 70, Right = 930, Bottom = 60, Top = 2200, HasReveal = true, RevealLeft = 0, RevealRight = 1000, RevealBottom = 60, RevealTop = 2200 };
            var w = WindowPattern.Read(sash, op);
            T.Check(w.FromDrawing && w.Panes.Count == 2 && !w.Arched, $"sash: two panes, flat ({w.Panes.Count})");
            T.Check(T.Near(w.Width, 1000, 1e-6) && T.Near(w.Height, 2140, 1e-6), "sash: the opening is the reveal width, sill to head");
            T.Check(w.Describe() == "1 wide x 2 high", "sash: described " + w.Describe());
            T.Check(w.Panes.All(p => p.All(q => q.X >= 25 - 1e-6 && q.X <= 975 + 1e-6 && q.Y >= 25 - 1e-6 && q.Y <= 2115 + 1e-6)), "sash: panes kept inside a frame");
            var tv = w.TypeValues(out bool simp);
            T.Check(tv["Panes Across"] == 1 && tv["Meeting Rail"] == 1 && T.Near(tv["Meeting Rail Height"], 1040, 1) && T.Near(tv["Meeting Rail Width"], 60, 1)
                    && tv["Panes High Bottom"] == 1 && tv["Panes High Top"] == 1 && !simp, "sash type: one across, meeting rail at 1040, 60 wide, 1/1");
            T.Check(T.Near(tv["Frame Width"], 50, 25) && tv["Width"] == 1000 && tv["Height"] == 2140, $"sash type: frame from the margins ({tv["Frame Width"]})");
            T.Check(WindowPattern.TypeName(tv).StartsWith("1000x2140 1 across, sash 1/1"), "sash type name: " + WindowPattern.TypeName(tv));
            var m2 = w.Mirrored();
            T.Check(m2.Key == w.Key, "sash: symmetric, so mirrored is the same look");

            // a frame drawn round the panes on the same layer: the panes are the innermost loops
            var framed = Box(0, 0, 1000, 2000).Concat(Box(60, 60, 470, 1940)).Concat(Box(530, 60, 940, 1940)).ToList();
            var w2 = WindowPattern.Read(framed, new ElevOpening { Left = 0, Right = 1000, Bottom = 0, Top = 2000 });
            T.Check(w2.Panes.Count == 2 && w2.Describe() == "2 wide x 1 high", "framed: the two inner loops are the panes (" + w2.Describe() + ")");
            var tv2 = w2.TypeValues(out _);
            T.Check(tv2["Panes Across"] == 2 && tv2["Meeting Rail"] == 0 && T.Near(tv2["Glazing Bar Width"], 60, 1), "framed type: two across, no rail, a 60 mm bar between");

            // a pair of sashes with glazing bars: 4 rows, the wide gap is the meeting rail
            var gb = new List<DwgLine>();
            foreach (var (y0, y1) in new[] { (60.0, 560.0), (585.0, 1085.0), (1145.0, 1645.0), (1670.0, 2170.0) })
                foreach (var (x0, x1) in new[] { (70.0, 487.0), (512.0, 930.0) }) gb.AddRange(Box(x0, y0, x1, y1));
            var w5 = WindowPattern.Read(gb, new ElevOpening { Left = 70, Right = 930, Bottom = 0, Top = 2230, HasReveal = true, RevealLeft = 0, RevealRight = 1000 });
            var tv5 = w5.TypeValues(out _);
            T.Check(tv5["Panes Across"] == 2 && tv5["Meeting Rail"] == 1 && tv5["Panes High Bottom"] == 2 && tv5["Panes High Top"] == 2
                    && T.Near(tv5["Meeting Rail Height"], 1085, 1) && T.Near(tv5["Glazing Bar Width"], 25, 1), "glazed sashes: 2 across, rail at 1085, 2/2 high, 25 mm bars");

            // a round-headed window: sides to 1500, a semicircle above; the opening is found up to the crown
            var arch = new List<DwgLine> { L(0, 0, 1000, 0), L(1000, 0, 1000, 1500), L(0, 1500, 0, 0) };
            for (int i = 0; i < 24; i++)
            {
                double t0 = Math.PI * i / 24, t1 = Math.PI * (i + 1) / 24;
                arch.Add(L(500 + 500 * Math.Cos(t0), 1500 + 500 * Math.Sin(t0), 500 + 500 * Math.Cos(t1), 1500 + 500 * Math.Sin(t1)));
            }
            var ops = ElevationFit.Openings(arch, 0, 1000);
            T.Check(ops.Count == 1 && T.Near(ops[0].Top, 2000, 1), $"arch: the opening reaches the crown ({(ops.Count > 0 ? ops[0].Top : 0):0})");
            var w3 = WindowPattern.Read(arch, ops[0]);
            T.Check(w3.Arched && w3.Panes.Count == 1 && w3.Outline.Count > 6 && T.Near(w3.Outline.Max(q => q.Y), 2025, 1), "arch: arched outline over the crown, a frame's width above the glass");
            T.Check(w3.Outline.Where(q => q.X > 300 && q.X < 700).All(q => q.Y > 1500 + 25), "arch: the outline stays above the arched pane");
            T.Check(w3.Describe().StartsWith("arched"), "arch: described " + w3.Describe());

            // two windows either side of a pier: the pier's two facing jambs are not an opening
            var pair = Box(0, 0, 900, 1000).Concat(Box(0, 1060, 900, 2000)).Concat(Box(1800, 0, 2700, 1000)).Concat(Box(1800, 1060, 2700, 2000)).ToList();
            T.Check(ElevationFit.Openings(pair, 900, 1800).Count == 0, "pier: the jambs either side of it are two windows', not one opening");
            T.Check(ElevationFit.Openings(pair, 0, 900).Count == 1, "pier: each window is still found");

            // a surround drawn round the reveal: the reveal is the first wall line outside the glass, not the outermost
            DwgLine W(double x1, double y1, double x2, double y2) => new DwgLine { A = new Pt(x1, y1), B = new Pt(x2, y2), Layer = "EF_25-Wall" };
            var walls = new List<DwgLine> { W(0, 0, 0, 2200), W(1000, 0, 1000, 2200), W(-110, -50, -110, 2300), W(1110, -50, 1110, 2300) };
            var sops = ElevationFit.Openings(sash, 70, 930, walls);
            T.Check(sops.Count == 1 && sops[0].HasReveal && T.Near(sops[0].RevealLeft, 0, 1e-6) && T.Near(sops[0].RevealRight, 1000, 1e-6), "surround: the reveal is the line 70 mm out, not the surround 180 mm out");
            var far = new List<DwgLine> { W(-110, -50, -110, 2300), W(1040, -50, 1040, 2300) };
            T.Check(!ElevationFit.Openings(sash, 70, 930, far)[0].HasReveal, "surround only (180 mm out): no reveal, the plan gives the width");

            // the plan's width: panes centred in it
            var fit = w.FitWidth(1100);
            T.Check(fit != null && T.Near(fit.Width, 1100, 1e-6) && T.Near(fit.Panes.SelectMany(q => q).Min(q => q.X), 120, 1e-6), "fit width: 860 mm of glass centred in 1100");
            T.Check(w.FitWidth(880) == null, "fit width: glass that doesn't fit is not squeezed");

            // nothing drawn: one pane in a default frame
            var w4 = WindowPattern.Read(new List<DwgLine>(), new ElevOpening { Left = 0, Right = 900, Bottom = 0, Top = 1500 });
            T.Check(!w4.FromDrawing && w4.Panes.Count == 1 && w4.Describe() == "plain", "nothing drawn: plain");
        }
    }
}
