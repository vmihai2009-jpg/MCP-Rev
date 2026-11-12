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
            T.Check(ops.Count == 3 && T.Near(ops[1].bottom, 31340, 1) && T.Near(ops[1].top, 33600, 1) && T.Near(ops[1].right - ops[1].left, 1000, 1), "stacked windows: one opening per floor, with its own sill and head");
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
