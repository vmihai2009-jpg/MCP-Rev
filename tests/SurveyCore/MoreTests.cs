using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    /// <summary>BG MCP's own additions: the config as System.Text.Json reads it, the DWG wall fit, and reading DWG text.</summary>
    internal static class MoreTests
    {
        public static void Run()
        {
            Console.WriteLine("Config (System.Text.Json)");
            ConfigTests();
            Console.WriteLine("Wall fit");
            FitTests();
            Console.WriteLine("DWG file");
            DwgTests();
            Console.WriteLine("Elevation fit");
            ElevationTests();
            Console.WriteLine("Fidelity (the 1:1 score)");
            FidelityTests();
            Console.WriteLine("Facades");
            FacadeTests();
        }

        // a 10 x 6 m room of 200 mm walls, drawn as each wall's two faces; and the model of it
        static readonly double[][] Room = { new double[] { 0, 0, 10000, 0 }, new double[] { 10000, 0, 10000, 6000 }, new double[] { 10000, 6000, 0, 6000 }, new double[] { 0, 6000, 0, 0 } };

        static List<Seg> RoomFaces()
        {
            var segs = new List<Seg>();
            foreach (var c in Room)
            {
                double L = Math.Sqrt((c[2] - c[0]) * (c[2] - c[0]) + (c[3] - c[1]) * (c[3] - c[1])), nx = -(c[3] - c[1]) / L, ny = (c[2] - c[0]) / L;
                foreach (int s in new[] { -1, 1 })
                    segs.Add(new Seg(new Pt(c[0] + nx * 100 * s, c[1] + ny * 100 * s), new Pt(c[2] + nx * 100 * s, c[3] + ny * 100 * s), 0));
            }
            return segs;
        }

        static List<FitWall> RoomWalls(double shiftFirstMm = 0, bool dropLast = false)
        {
            var walls = new List<FitWall>();
            for (int i = 0; i < Room.Length - (dropLast ? 1 : 0); i++)
            {
                var c = Room[i];
                double dy = i == 0 ? shiftFirstMm : 0;
                walls.Add(new FitWall { Id = 100 + i, A = new Pt(c[0], c[1] + dy), B = new Pt(c[2], c[3] + dy), ThicknessMm = 200, Type = "Concept Wall 200mm" });
            }
            return walls;
        }

        static FidelityInput RoomInput(List<FitWall> walls) => new FidelityInput
        {
            Level = "Ground Floor", LevelZ = 0, DatumMm = 26470, Walls = walls, WallFaces = RoomFaces(),
            SpotHeights = new List<(Pt, double)> { (new Pt(3000, 3000), 26470), (new Pt(6000, 3000), 26470) },
        };

        static void FacadeTests()
        {
            // a section: left wall face at x 1000 from 27 m to 48 m, a cornice stepping out 150 then 300 at 47.0-47.3 m;
            // right face at 18000 with a 200 string course at 44.5-44.7 m
            var sec = new List<DwgLine>
            {
                L(1000, 27000, 1000, 47000, "Ss_25-Profile"), L(1000, 47000, 850, 47000, "Ss_25-Profile"), L(850, 47000, 850, 47150, "Ss_25-Profile"),
                L(850, 47150, 700, 47150, "Ss_25-Profile"), L(700, 47150, 700, 47300, "Ss_25-Profile"), L(700, 47300, 1000, 47300, "Ss_25-Profile"), L(1000, 47300, 1000, 48000, "Ss_25-Profile"),
                L(18000, 27000, 18000, 44500, "Ss_25-Profile"), L(18000, 44500, 18200, 44500, "Ss_25-Profile"), L(18200, 44500, 18200, 44700, "Ss_25-Profile"),
                L(18200, 44700, 18000, 44700, "Ss_25-Profile"), L(18000, 44700, 18000, 48000, "Ss_25-Profile"),
            };
            var sides = FacadeDetail.Sides(sec, "Section-A");
            var left = sides.First(s => s.Side < 0); var right = sides.First(s => s.Side > 0);
            T.Check(T.Near(left.Face, 1000, 5) && T.Near(right.Face, 18000, 5), "each side's wall face is its commonest outer line");
            var lb = FacadeDetail.Bands(left);
            T.Check(lb.Count == 1 && T.Near(lb[0].Z0, 47000, 10) && T.Near(lb[0].Z1, 47300, 10) && T.Near(lb[0].MaxProjection, 300, 1), "the cornice: its heights and full projection");
            T.Check(lb[0].Profile.Any(q => T.Near(q.p, 150, 1)), "the cornice's step is kept in its profile");
            var rb = FacadeDetail.Bands(right);
            T.Check(rb.Count == 1 && T.Near(rb[0].MaxProjection, 200, 1) && T.Near(rb[0].Z0, 44500, 10), "the string course on the other side");
            var hz = new List<(double z, double u0, double u1)> { (47000, 0, 6000), (47300, 0, 6000), (47150, 6500, 9800) };
            FacadeDetail.Extent(lb[0], hz, 10000);
            T.Check(lb[0].Runs.Count == 2 && T.Near(lb[0].Runs[0].u1, 6000, 1) && T.Near(lb[0].Cover, 0.93, 0.01), "its runs along the facade where the elevation draws it");
            T.Check(FacadeDetail.Agreement(lb, hz, 10000) == 1 && FacadeDetail.Agreement(rb, hz, 10000) == 0, "the side whose bands the elevation draws wins");

            // a sheet with two elevations 6 m apart: two parts
            var sheet = new List<DwgLine> { L(1000, 30000, 1000, 32000), L(2000, 30000, 2000, 32000), L(4000, 30000, 4000, 32000), L(20000, 30000, 20000, 33000), L(21000, 30000, 21000, 33000) };
            var parts = ElevationFit.SheetParts(sheet);
            T.Check(parts.Count == 2 && parts[0].x1 == 4000 && parts[1].x0 == 20000 && parts[1].y1 == 33000, "a sheet of elevations side by side splits into its drawings");
            T.Check(Facades.LooksLikeStreet("SWALLOW PLACE") && Facades.LooksLikeStreet("Regent Street") && !Facades.LooksLikeStreet("Basement Yard") && !Facades.LooksLikeStreet("Security Mesh Over Basement Yard") && !Facades.LooksLikeStreet("FFL 26.47"), "street names, not drawing notes");
            var lab = Fidelity.LabelsFor(new Pt(0, 0), new[] { new DwgText { Value = "S 0.94", At = new Pt(0, 600) }, new DwgText { Value = "H 3.27", At = new Pt(0, 700) }, new DwgText { Value = "S 1.10", At = new Pt(2500, 600) } });
            T.Check(lab.sill == 940 && lab.head == 3270, "a window's own labels");
            var amb = Fidelity.LabelsFor(new Pt(0, 0), new[] { new DwgText { Value = "S 0.94", At = new Pt(-500, 600) }, new DwgText { Value = "S 1.10", At = new Pt(500, 600) } });
            T.Check(amb.sill == null, "two windows' labels equally near: no guess");

            // the 10 x 6 m room on two floors, plus an internal wall across it; angle 0 (model north is grid north)
            var walls = new List<FacadeWall>();
            foreach (var (lv, z) in new[] { ("Ground", 0.0), ("First", 3000.0) })
            {
                for (int i = 0; i < Room.Length; i++)
                    walls.Add(new FacadeWall { Id = (z > 0 ? 200 : 100) + i, Level = lv, A = new Pt(Room[i][0], Room[i][1]), B = new Pt(Room[i][2], Room[i][3]), ThicknessMm = 200, BaseZ = z, TopZ = z + 3000 });
                walls.Add(new FacadeWall { Id = (z > 0 ? 290 : 190), Level = lv, A = new Pt(5000, 0), B = new Pt(5000, 6000), ThicknessMm = 100, BaseZ = z, TopZ = z + 3000 });
            }
            var runs = Facades.Runs(walls);
            T.Check(runs.Count == 4 && runs.All(r => r.WallIds.Count == 2 && r.Levels.Count == 2), "four facades, each the same wall on both floors; the internal wall is not one");
            var north = runs[0];
            T.Check(north.Id == "F1" && T.Near(Facades.Bearing(north.OutX, north.OutY, 0), 0, 0.1) && Facades.Compass(0) == "north", "F1 faces north (ids go clockwise from north)");
            T.Check(T.Near(north.A.X, 10000, 1) && T.Near(north.B.X, 0, 1) && T.Near(north.A.Y, 6100, 1) && north.TopZ == 6000, "the line is the outer face, left to right as seen from outside");
            T.Check(runs[1].Id == "F2" && T.Near(Facades.Bearing(runs[1].OutX, runs[1].OutY, 0), 90, 0.1), "F2 faces east");
            // Swallow Place: project base point "angle to true north" 347.374 = model north turned 12.626 anticlockwise
            T.Check(T.Near(Facades.Bearing(0, 1, 12.626 * Math.PI / 180), 347.374, 0.01), "the project angle turns model north to grid (347.374 at Swallow Place)");
            var street = Facades.StreetNear(north, new[] { new DwgText { Value = "SWALLOW PLACE", At = new Pt(5000, 12000) }, new DwgText { Value = "REGENT STREET", At = new Pt(20000, 3000) } });
            T.Check(street == "Swallow Place", "the street text outside a facade names it");
        }

        static void FidelityTests()
        {
            var gate = new FidelityGate();
            T.Check(Fidelity.Label("S 0.94") == ('S', 940.0) && Fidelity.Label("H 3.27") == ('H', 3270.0) && Fidelity.Label("S X.XX") == null, "S/H labels read in mm; placeholders ignored");

            var (perfect, pi) = Fidelity.Measure(RoomInput(RoomWalls()), gate);
            T.Check(perfect.Pass && T.Near(perfect.Walls.WithinTolPct, 100) && perfect.Walls.MaxMm == 0 && perfect.Walls.Missing == 0 && pi.Count == 0, "a perfect model scores 100% and passes");
            T.Check(perfect.Walls.ThicknessChecked == 4 && perfect.Walls.ThicknessOver == 0 && perfect.LevelCheck.Checked && perfect.LevelCheck.DevMm == 0, "thickness and level measured and right");

            var (moved, mi) = Fidelity.Measure(RoomInput(RoomWalls(shiftFirstMm: 15)), gate);
            T.Check(!moved.Pass && moved.FailedOn.Contains("walls") && T.Near(moved.Walls.MaxMm, 15, 1), "a wall moved 15 mm fails, max deviation 15 mm");
            T.Check(mi.Count(i => i.Kind == "wall_face") == 1 && mi.First(i => i.Kind == "wall_face").Id == 100, "the moved wall is the issue, by id");

            var (gone, gi) = Fidelity.Measure(RoomInput(RoomWalls(dropLast: true)), gate);
            var miss = gi.FirstOrDefault(i => i.Kind == "wall_missing");
            T.Check(!gone.Pass && gone.Walls.Missing == 1 && miss != null && T.Near(miss.DevMm, 6000, 250), "a missing wall is listed with its length");

            // a window 1000 wide at 5000 on the first wall: plan jambs 20 mm off on the right; its sill label says 0.90 against the model's 940
            var input = RoomInput(RoomWalls());
            input.Inserts.Add(new FidInsert { Id = 7, Category = "Windows", HostWallId = 100, At = new Pt(5000, 0), W = 1000, Sill = 940, Head = 2400 });
            input.OpeningLines.Add(new Seg(new Pt(4500, -100), new Pt(4500, 100), 0));
            input.OpeningLines.Add(new Seg(new Pt(5520, -100), new Pt(5520, 100), 0));
            input.Labels.Add(new DwgText { Value = "S 0.90", At = new Pt(5000, 500) });
            var (win, wi) = Fidelity.Measure(input, gate);
            var op = wi.FirstOrDefault(i => i.Kind == "opening");
            T.Check(win.Openings.PlanChecked == 1 && win.Openings.PlanOver == 0 && win.Openings.LabelOver == 1 && op != null && op.Id == 7 && T.Near(op.DevMm, 40, 1), "a window's sill 40 mm off its label fails; its plan jambs (20 mm) pass");

            var (lvl, _) = Fidelity.Measure(new FidelityInput { Level = "L", LevelZ = 0, DatumMm = 26470, Walls = RoomWalls(), WallFaces = RoomFaces(),
                SpotHeights = new List<(Pt, double)> { (new Pt(1, 1), 26420), (new Pt(2, 2), 26420), (new Pt(3, 3), 26900) } }, gate);
            T.Check(lvl.LevelCheck.DevMm == -50 && lvl.FailedOn.Contains("level"), "a level 50 mm above its spot heights fails");

            var html = Fidelity.Html(new FidelityResult { Model = "t", When = "now", Gate = gate, Floors = new List<FloorScore> { moved }, Issues = mi });
            T.Check(html.Contains("<table") && html.Contains("fail: walls"), "the report page lists the floors and issues");
        }

        static DwgLine L(double x1, double y1, double x2, double y2, string layer = "Ss_25_30_95-Window") => new DwgLine { A = new Pt(x1, y1), B = new Pt(x2, y2), Layer = layer };
        static DwgText Tx(string v, double x, double y) => new DwgText { Value = v, At = new Pt(x, y), Layer = "Zz_60_45-Level" };

        static void ElevationTests()
        {
            T.Check(ElevationFit.LevelValueMm("48.97") == 48970 && ElevationFit.LevelValueMm("+26.47") == 26470 && ElevationFit.LevelValueMm("27.40m") == 27400, "level marks read as mm above datum");
            T.Check(ElevationFit.LevelValueMm("Elevation 1") == null && ElevationFit.LevelValueMm("S 0.94") == null, "other text is not a level mark");

            // the survey sheet: drawn 1:1, a mark's text sits ~230 mm above its line
            var lines = new List<DwgLine> { L(40000, 48970, 44000, 48970, "Zz_60_45-Level"), L(40000, 33100, 44000, 33100, "Zz_60_45-Level"), L(40000, 27400, 44000, 27400, "Zz_60_45-Level") };
            var marks = new[] { Tx("48.97", 40400, 49196), Tx("33.10", 42600, 33327), Tx("27.40", 42070, 27628), Tx("99.99", 90000, 5000) };
            var h = ElevationFit.Heights(marks, lines, 39000, 45000);
            T.Check(h.Marks == 3 && T.Near(h.A, 1, 1e-9) && T.Near(h.Height(48970), 48970, 0.01), "heights fit to the lines under the marks, not the text (1:1)");
            var none = ElevationFit.Heights(new DwgText[0], lines, 0, 1);
            T.Check(T.Near(none.A, 1) && none.Note.Contains("assumed"), "with no marks it says it assumed 1:1");

            // a facade of 3 windows, 1200 wide at 2000, 5000, 9000 along (not symmetrical); the elevation draws them the other way round
            var us = new[] { 2000.0, 5000, 9000 };
            var model = new List<double>(); foreach (var u in us) { model.Add(u - 600); model.Add(u + 600); }
            var elev = new List<DwgLine>();
            foreach (var u in us)
            {
                double xa = 51000 - (u - 600), xb = 51000 - (u + 600);
                elev.Add(L(xa, 30000, xa, 32000)); elev.Add(L(xb, 30000, xb, 32000));
                elev.Add(L(xb, 30000, xa, 30000)); elev.Add(L(xb, 32000, xa, 32000)); elev.Add(L(xb - 50, 29950, xa + 50, 29950)); // frame, and a stone sill below
            }
            var jambs = ElevationFit.Jambs(elev);
            T.Check(jambs.Count == 6, "six jambs found (frames drawn once each)");
            var along = ElevationFit.Along(jambs, model, 1);
            T.Check(along.Dir == -1 && along.Matched == 6 && T.Near(along.Along(51000 - 5000), 5000, 1), "a mirrored elevation lines up with the facade, all jambs matched");
            var op = ElevationFit.Opening(elev, along.SheetX(5000 - 600), along.SheetX(5000 + 600));
            T.Check(op != null && T.Near(Math.Abs(op.Value.right - op.Value.left), 1200, 1) && T.Near(op.Value.top, 32000, 1) && T.Near(op.Value.bottom, 29950, 1), "an opening's width, head and lowest sill line read from the elevation");
            T.Check(ElevationFit.Opening(elev, 60000, 61000) == null, "nothing drawn: no opening");
            T.Check(!along.Ambiguous, "an unsymmetrical facade's direction is known");
            var sym = new List<double> { 1400, 2600, 4400, 5600, 7400, 8600 };
            var symElev = sym.Select(u => 51000 - u).ToList();
            T.Check(ElevationFit.Along(symElev, sym, 1).Ambiguous, "a symmetrical facade is reported as ambiguous");
            T.Check(ElevationFit.Along(symElev, sym, 1, 60, -1).Dir == -1, "a given direction is used");

            // Plowman Craven spot levels: a small triangle pointing down at the level, the text ~225 above its apex
            var tri = new List<DwgLine>();
            foreach (var (x, y) in new[] { (64899.0, 27519.0), (66556.0, 31140.0), (70961.0, 35370.0) })
            { tri.Add(L(x, y, x + 56, y + 150, "Zz_60_45-Level")); tri.Add(L(x + 56, y + 150, x - 56, y + 150, "Zz_60_45-Level")); tri.Add(L(x - 56, y + 150, x, y, "Zz_60_45-Level")); }
            var triMarks = new[] { Tx("27.52", 64476, 27744), Tx("31.14", 66133, 31365), Tx("35.37", 70538, 35595) };
            var ht = ElevationFit.Heights(triMarks, tri, 62000, 104000);
            T.Check(ht.Marks == 3 && T.Near(ht.Height(27519), 27520, 2) && T.Near(ht.A, 1, 1e-3), "spot-level triangles: the apex is the level");

            // one column of windows on three floors: one opening per floor, not one tall one
            var col = new List<DwgLine>();
            foreach (var (s, hd) in new[] { (27400.0, 29400.0), (31400.0, 33600.0), (35400.0, 37000.0) })
            { col.Add(L(1000, s, 1000, hd)); col.Add(L(2000, s, 2000, hd)); col.Add(L(1000, s, 2000, s)); col.Add(L(1000, hd, 2000, hd)); col.Add(L(950, s - 60, 2050, s - 60)); }
            var ops = ElevationFit.Openings(col, 1000, 2000);
            T.Check(ops.Count == 3 && T.Near(ops[1].Bottom, 31340, 1) && T.Near(ops[1].Top, 33600, 1) && T.Near(ops[1].Right - ops[1].Left, 1000, 1), "stacked windows: one opening per floor, with its own sill and head");

            // Plowman Craven draws the frame (window layer) in two sashes with a 60 mm meeting-rail gap, and the
            // masonry opening (wall layer) 65 mm outside it: one opening, its width from the masonry
            var sash = new List<DwgLine> { L(66001, 37672, 66001, 39273), L(66861, 37672, 66861, 39273), L(66001, 39333, 66001, 40092), L(66861, 39333, 66861, 40092),
                                           L(66001, 37672, 66861, 37672), L(66001, 40092, 66861, 40092) };
            var masonry = new List<DwgLine> { L(65936, 37642, 65936, 40156, "EF_25-Wall"), L(65971, 37642, 65971, 40122, "EF_25-Wall"), L(66926, 37642, 66926, 40156, "EF_25-Wall"), L(66003, 36759, 66003, 37394, "EF_25-Wall") };
            var one = ElevationFit.Openings(sash, 65950, 66950, masonry);
            T.Check(one.Count == 1 && T.Near(one[0].Bottom, 37672, 1) && T.Near(one[0].Top, 40092, 1), "a frame in two sashes is one opening; sill and head from the frame");
            T.Check(one[0].HasReveal && T.Near(one[0].OpeningRight - one[0].OpeningLeft, 990, 1), "width from the masonry opening round the frame");

            // a 1:1 sheet with one mark paired to the wrong line does not tilt the scale
            var lv = new List<DwgLine> { L(0, 27400, 4000, 27400, "Zz_60_45-Level"), L(0, 31140, 4000, 31140, "Zz_60_45-Level"), L(0, 35370, 4000, 35370, "Zz_60_45-Level"), L(0, 41000, 4000, 41000, "Zz_60_45-Level"), L(6000, 48500, 9000, 48500, "Zz_60_45-Level") };
            var lm = new[] { Tx("27.40", 500, 27625), Tx("31.14", 500, 31365), Tx("35.37", 500, 35595), Tx("41.00", 500, 41225), Tx("48.97", 7000, 48725) };
            var robust = ElevationFit.Heights(lm, lv, 0, 10000);
            T.Check(robust.A == 1 && robust.Marks == 4 && T.Near(robust.Height(35370), 35370, 1), "1:1 sheet: a wrong pairing is left out, scale stays 1");
        }

        static void ConfigTests()
        {
            var cfg = ConfigJson.Parse(@"{
              // comments and trailing commas are fine
              ""Tolerances"": { ""snapMm"": 7, ""CLOSEGAPMM"": 80 },
              ""levels"": [ { ""name"": ""Ground Floor"", ""elevationMm"": 0 } ],
              ""defaultLevel"": ""Ground Floor"",
              ""rules"": [ { ""name"": ""walls"", ""match"": [""EF_25*""], ""action"": ""wall_double_line"", ""maxThicknessMm"": 1300, ""zOffsetMm"": null }, ],
            }");
            T.Check(T.Near(cfg.Tolerances.SnapMm, 7) && T.Near(cfg.Tolerances.CloseGapMm, 80), "tolerances are read although they are fields, any case");
            T.Check(T.Near(cfg.Tolerances.MinSegmentMm, 10), "tolerances left out keep their defaults");
            T.Check(cfg.Rules.Count == 1 && T.Near(cfg.Rules[0].MaxThicknessMm, 1300) && T.Near(cfg.Rules[0].MinThicknessMm, 60), "rule values read, others default");
            T.Check(cfg.Levels[0].Name == "Ground Floor", "levels read");
            var empty = ConfigJson.Parse("{}");
            T.Check(empty.Rules != null && empty.Tolerances != null && empty.Levels != null, "an empty config gets empty lists, not nulls");
            bool threw = false; try { ConfigJson.Parse("{ not json"); } catch (ArgumentException) { threw = true; }
            T.Check(threw, "bad JSON is a readable error");
            var json = System.Text.Json.JsonSerializer.Serialize(new CleanStats { DuplicatesRemoved = 3 }, ConfigJson.Write);
            T.Check(json.Contains("\"duplicatesRemoved\":3"), "results are written camelCase, fields included");
        }

        static FitWall W(long id, double x1, double y1, double x2, double y2, double t) =>
            new FitWall { Id = id, A = new Pt(x1, y1), B = new Pt(x2, y2), ThicknessMm = t, Type = "Concept Wall " + t + "mm" };
        static Seg S(double x1, double y1, double x2, double y2) => new Seg(new Pt(x1, y1), new Pt(x2, y2), 0);

        static void FitTests()
        {
            // the DWG: a 10 m wall with faces at y = 0 and y = 370 (370 mm thick, centred on y = 185)
            var dwg = new List<Seg> { S(0, 0, 10000, 0), S(0, 370, 10000, 370) };

            var right = WallFit.Fit(new[] { W(1, 0, 185, 10000, 185, 370) }, dwg, new FitOptions());
            T.Check(right.Count == 0, "a wall already on its DWG faces is left alone");

            var p = WallFit.Fit(new[] { W(2, 0, 300, 10000, 300, 300) }, dwg, new FitOptions()).Single();
            T.Check(T.Near(p.NewThicknessMm, 370) && Math.Abs(p.ShiftStartMm + 115) < 1 && Math.Abs(p.ShiftEndMm + 115) < 1, "an off, too-thin wall: moved 115 mm onto the faces, made 370 mm");
            T.Check(T.Near(p.NewLine[1], 185, 1) && T.Near(p.NewLine[3], 185, 1) && T.Near(p.NewLine[0], 0, 1) && T.Near(p.NewLine[2], 10000, 1), "the new centre line runs between the faces, same ends");
            T.Check(p.Confidence == "moderate", "well covered and modest: moderate");

            // drawn the other way round, and the faces in broken pieces: same answer
            var broken = new List<Seg> { S(10000, 0, 6000, 0), S(5900, 0, 0, 0), S(0, 370, 4000, 370), S(4100, 370, 10000, 370) };
            var p2 = WallFit.Fit(new[] { W(3, 10000, 300, 0, 300, 300) }, broken, new FitOptions()).Single();
            T.Check(T.Near(p2.NewThicknessMm, 370) && T.Near(p2.NewLine[1], 185, 1), "reversed wall and broken face lines fit the same");

            // only one face drawn: no proposal (it would be a guess)
            T.Check(WallFit.Fit(new[] { W(4, 0, 300, 10000, 300, 300) }, new List<Seg> { S(0, 0, 10000, 0) }, new FitOptions()).Count == 0, "one face only: nothing proposed");

            // a slightly skewed wall: the ends move by different amounts
            var p3 = WallFit.Fit(new[] { W(5, 0, 150, 10000, 230, 370) }, dwg, new FitOptions()).Single();
            T.Check(Math.Abs(p3.ShiftStartMm - 35) < 2 && Math.Abs(p3.ShiftEndMm + 45) < 3, "a skewed wall is straightened end by end");

            // a big jump is flagged for review
            var dwgFar = new List<Seg> { S(0, 500, 10000, 500), S(0, 1000, 10000, 1000) };
            var p4 = WallFit.Fit(new[] { W(6, 0, 300, 10000, 300, 300) }, dwgFar, new FitOptions()).Single();
            T.Check(p4.Confidence == "review" && T.Near(p4.ShiftStartMm, 450, 1), "a 450 mm move is for review");
            T.Check(WallFit.Fit(new[] { W(9, 0, 300, 10000, 300, 300) }, new List<Seg> { S(0, 600, 10000, 600), S(0, 1300, 10000, 1300) }, new FitOptions()).Count == 0,
                "faces beyond the search distance are not this wall's");

            // walls the DWG has and the model does not
            var plan = new List<Seg> { S(0, 0, 10000, 0), S(0, 370, 10000, 370), S(20000, 0, 20000, 6000), S(20215, 0, 20215, 6000) };
            var missing = WallFit.Missing(new[] { W(7, 0, 185, 10000, 185, 370) }, plan, 60, 1300, 600);
            T.Check(missing.Count == 1 && T.Near(missing[0].ThicknessMm, 215) && T.Near(missing[0].LengthMm, 6000), "the DWG wall with no model wall is missing; the built one is not");
            T.Check(T.Near(WallFit.Covered(new Pt(0, 185), new Pt(10000, 185), new[] { W(8, 0, 185, 5000, 185, 370) }, 400), 0.5, 1e-9), "coverage counts half a wall as half");
        }

        static void DwgTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "bgmcp-dwg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // a survey sheet in metres: two wall lines, a window label pair, a spot height, a closed outline
                var doc = new ACadSharp.CadDocument();
                doc.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Meters;
                var walls = new ACadSharp.Tables.Layer("EF_25-Wall");
                var text = new ACadSharp.Tables.Layer("Zz_20-TextHt");
                doc.Layers.Add(walls); doc.Layers.Add(text);
                doc.Entities.Add(new ACadSharp.Entities.Line { StartPoint = new CSMath.XYZ(0, 0, 0), EndPoint = new CSMath.XYZ(10, 0, 0), Layer = walls });
                doc.Entities.Add(new ACadSharp.Entities.Line { StartPoint = new CSMath.XYZ(0, 0.37, 0), EndPoint = new CSMath.XYZ(10, 0.37, 0), Layer = walls });
                doc.Entities.Add(new ACadSharp.Entities.TextEntity { Value = "S 0.94", InsertPoint = new CSMath.XYZ(2, -0.5, 0), Height = 0.1, Layer = text });
                doc.Entities.Add(new ACadSharp.Entities.TextEntity { Value = "H  3.27", InsertPoint = new CSMath.XYZ(2, -0.7, 0), Height = 0.1, Layer = text });
                doc.Entities.Add(new ACadSharp.Entities.MText { Value = "+26.47", InsertPoint = new CSMath.XYZ(50, 50, 0), Height = 0.2, Layer = text });
                var outline = new ACadSharp.Entities.LwPolyline { IsClosed = true, Layer = walls };
                foreach (var (x, y) in new[] { (20.0, 0.0), (24.0, 0.0), (24.0, 3.0), (20.0, 3.0) }) outline.Vertices.Add(new ACadSharp.Entities.LwPolyline.Vertex(new CSMath.XY(x, y)));
                doc.Entities.Add(outline);

                string dxf = Path.Combine(dir, "plan.dxf");
                using (var w = new ACadSharp.IO.DxfWriter(dxf, doc, false)) w.Write();
                var c = DwgFile.Read(dxf);
                T.Check(T.Near(c.MmPerUnit, 1000) && c.UnitsNote.Contains("metres"), "units from the header: metres");
                var sill = c.Texts.FirstOrDefault(t => t.Value == "S 0.94");
                T.Check(sill != null && T.Near(sill.At.X, 2000, 1e-6) && T.Near(sill.At.Y, -500, 1e-6) && sill.Layer == "Zz_20-TextHt" && T.Near(sill.HeightMm, 100, 1e-6), "a TEXT label read with its place in mm and its layer");
                T.Check(c.Texts.Any(t => t.Value == "H 3.27"), "spaces in labels are tidied");
                T.Check(c.Texts.Any(t => t.Value == "+26.47" && T.Near(t.At.X, 50000, 1e-6)), "MTEXT read as plain text");
                T.Check(c.Lines.Count(l => l.Layer == "EF_25-Wall") == 6, "lines and the closed outline's four sides read");
                T.Check(c.Lines.Any(l => T.Near(l.B.X, 10000, 1e-6) && T.Near(l.A.Y, 370, 1e-6)), "line ends in mm");
                T.Check(c.LayerCounts["EF_25-Wall"] == 3 && c.LayerCounts["Zz_20-TextHt"] == 3, "entities counted per layer");
                T.Check(ReferenceEquals(c, DwgFile.Read(dxf)), "a file read twice is read once");
                T.Check(DwgFile.LayerMatches("EF_25-Wall", new[] { "ef_25*" }) && DwgFile.LayerMatches("XREF|EF_25-Wall", new[] { "EF_25*" }) && !DwgFile.LayerMatches("Zz_20", new[] { "EF_25*" }),
                    "layer globs: any case, xref prefixes stripped");

                // the same drawing as a DWG, unitless: units inferred from its size
                doc.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Unitless;
                string dwg = Path.Combine(dir, "plan.dwg");
                try
                {
                    using (var w = new ACadSharp.IO.DwgWriter(dwg, doc)) w.Write();
                    var d = DwgFile.Read(dwg);
                    T.Check(T.Near(d.MmPerUnit, 1000) && d.UnitsNote.Contains("unitless") && d.Texts.Any(t => t.Value == "S 0.94"), "a unitless DWG spanning metres is read as metres");
                }
                catch (Exception ex) { T.Check(false, "DWG write and read: " + ex.Message); }

                bool missing = false; try { DwgFile.Read(Path.Combine(dir, "nope.dwg")); } catch (FileNotFoundException) { missing = true; }
                T.Check(missing, "a missing file is a clear error");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
