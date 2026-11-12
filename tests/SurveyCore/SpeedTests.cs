// 1.15.0: dwg_image's renderer and PNG encoder, survey_run's issue causes and score history (Revit-free parts).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class SpeedTests
    {
        public static void Run()
        {
            Console.WriteLine("DwgRaster / Png");
            var lines = new List<DwgLine>
            {
                new DwgLine { A = new Pt(0, 500), B = new Pt(1000, 500), Layer = "Wall" },          // across the middle
                new DwgLine { A = new Pt(-50000, 0), B = new Pt(50000, 1000), Layer = "Window" },  // mostly outside: clipped
                new DwgLine { A = new Pt(5000, 5000), B = new Pt(6000, 6000), Layer = "Wall" },     // wholly outside
            };
            var texts = new List<DwgText> { new DwgText { At = new Pt(100, 800), Value = "S 0.94", HeightMm = 50 }, new DwgText { At = new Pt(100, 100), Value = "tiny", HeightMm = 1 } };
            var spec = new RasterSpec { X0 = 0, Y0 = 0, X1 = 1000, Y1 = 1000, WidthPx = 600, Highlight = l => l == "Window" };
            var r = DwgRaster.Render(lines, texts, spec, new[] { new RasterMark { X0 = 400, Y0 = 400, X1 = 600, Y1 = 600, Label = "model" } });
            T.Check(r.Png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG signature");
            var (w, h, rgb) = Png.Decode(r.Png);
            T.Check(w == r.Width && h == r.Height && rgb.SequenceEqual(r.Rgb), "PNG decodes back to the same pixels");
            T.Check(r.LinesDrawn == 2, "lines outside the box skipped, a long one clipped: " + r.LinesDrawn);
            T.Check(r.TextsDrawn == 1 && r.TextsTooSmall == 1, "readable text drawn, tiny text counted, not drawn");
            var (px, py) = r.ToPx(250, 500);
            int i = (py * w + px) * 3;
            T.Check(rgb[i] < 60 && rgb[i + 1] < 60 && rgb[i + 2] < 60, "the wall line is dark where it should be");
            var (mx, my) = r.ToPx(400, 450);
            int j = (my * w + mx) * 3;
            T.Check(rgb[j] > 180 && rgb[j + 1] < 80, "the red mark's left edge is red");
            var (cx, cy) = r.ToPx(800, 200);
            int k = (cy * w + cx) * 3;
            T.Check(rgb[k] > 200 && rgb[k + 1] > 200, "empty paper stays light");
            T.Check(Math.Abs(r.MmPerPx - 1000.0 / (600 - 74)) < 1e-9, "mm per px from the width");
            T.Check(Png.Crc(System.Text.Encoding.ASCII.GetBytes("IEND")) == 0xAE426082, "CRC32 of IEND");
            T.Check(DwgRaster.NiceStep(130) == 200 && DwgRaster.NiceStep(0.4) == 0.5 && DwgRaster.NiceStep(1000) == 1000, "nice ruler steps");
            bool threw = false; try { DwgRaster.Render(lines, texts, new RasterSpec { X0 = 1, X1 = 1, Y0 = 0, Y1 = 5 }); } catch (ArgumentException) { threw = true; }
            T.Check(threw, "a box with no size is refused");
            var tall = DwgRaster.Render(lines, null, new RasterSpec { X0 = 0, Y0 = 0, X1 = 100, Y1 = 100000, WidthPx = 1200, MaxHeightPx = 800 });
            T.Check(tall.Height <= 800, "a tall box is fitted to the height limit");

            Console.WriteLine("IssueCauses");
            var issues = new List<FidIssue>();
            for (int n = 0; n < 30; n++) issues.Add(new FidIssue { Kind = "wall_face", Level = "L1", Id = 100 + n, DevMm = 50 + (n % 3), Severity = 5 });
            issues.Add(new FidIssue { Kind = "wall_face", Level = "L1", Id = 200, DevMm = 18, Severity = 1.8 });
            issues.Add(new FidIssue { Kind = "wall_face", Level = "L1", Id = 201, DevMm = 400, Severity = 40, At = new double[] { 1, 2 } });
            for (int n = 0; n < 5; n++) issues.Add(new FidIssue { Kind = "opening", Level = "L1", Id = 300 + n, DevMm = 150, Against = "elevation head 2400", Severity = 5 });
            issues.Add(new FidIssue { Kind = "opening", Level = "L1", Id = 310, DevMm = -60, Against = "sill label S 0.94", Severity = 2 });
            issues.Add(new FidIssue { Kind = "wall_thickness", Level = "L1", Id = 400, DevMm = 120, Against = "DWG face pair 220 mm (model 100 mm)", Severity = 12 });
            issues.Add(new FidIssue { Kind = "wall_missing", Level = "L1", DevMm = 600, Severity = 2.6 });
            issues.Add(new FidIssue { Kind = "opening_missing", Level = "L1", DevMm = 900, Against = "plan (Survey to Revit's reading): a door 900 wide", Severity = 2.9 });
            issues.Add(new FidIssue { Kind = "level", Level = "L1", DevMm = 40, Severity = 7 });
            var causes = IssueCauses.Group(issues);
            var single = causes.First();
            T.Check(single.Cause.Contains("50 mm") && single.Count == 30 && single.Systematic, "30 walls ~50 mm off grouped as one systematic cause, ranked first");
            T.Check(single.Ids.Count == 8, "sample ids capped at 8");
            T.Check(causes.Any(c => c.Cause.Contains("over 150 mm") && c.Count == 1 && c.At != null), "a far-off wall is its own cause, with where");
            var head = causes.First(c => c.Cause.Contains("head vs elevation"));
            T.Check(head.Count == 5 && head.Systematic && head.MedianMm == 150, "5 heads 150 mm high: one systematic offset");
            T.Check(causes.Any(c => c.Cause.Contains("sill vs plan label") && c.Cause.Contains("too low")), "label sills split from elevation heads, with direction");
            T.Check(causes.Any(c => c.Cause.Contains("modelled 100 mm")), "thickness grouped by the model's thickness");
            T.Check(causes.Any(c => c.Cause.StartsWith("short walls")) && causes.Any(c => c.Cause.StartsWith("doors on the plan")), "short missing walls and missing doors named");
            T.Check(!causes.First(c => c.Kind == "wall_missing").Systematic, "missing items are never 'systematic'");
            T.Check(IssueCauses.Group(null).Count == 0, "no issues, no causes");

            string rows = IssueCauses.HistoryRows("2026-11-12 10:00", "7 Swallow, Place.rvt", "1.15.0", "L1", new[] { ("score", 0.8125), ("pass", 0.0) });
            T.Check(rows == "2026-11-12 10:00,\"7 Swallow, Place.rvt\",1.15.0,L1,score,0.813\n2026-11-12 10:00,\"7 Swallow, Place.rvt\",1.15.0,L1,pass,0\n", "score history rows, commas quoted");
        }
    }
}
