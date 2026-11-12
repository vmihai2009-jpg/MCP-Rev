// 1.8.0: ground points from a survey site plan (Survey/Terrain.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class TerrainTests
    {
        public static void Run()
        {
            Console.WriteLine("Terrain");
            var pre = TerrainOptions.DefaultPrefixes;
            T.Check(Terrain.ParseLevel("12.345", pre) == 12345 && Terrain.ParseLevel("+12.35", pre) == 12350, "bare and + labels are metres above datum");
            T.Check(Terrain.ParseLevel("SL 12.34", pre) == 12340 && Terrain.ParseLevel("GL12.30", pre) == 12300 && Terrain.ParseLevel("cl 11,95", pre) == 11950, "ground prefixes, any case, decimal comma");
            T.Check(Terrain.ParseLevel("S 0.94", pre) == null && Terrain.ParseLevel("H 3.27", pre) == null, "window sill and head labels are not ground");
            T.Check(Terrain.ParseLevel("FFL 27.05", pre) == null && Terrain.ParseLevel("IL 10.20", pre) == null, "floor levels and drain inverts are not ground");
            T.Check(Terrain.ParseLevel("12", pre) == null && Terrain.ParseLevel("Bedroom", pre) == null && Terrain.ParseLevel("1.2 x 3.4", pre) == null, "room numbers, names and sizes are not levels");
            T.Check(Terrain.ParseLevel("FFL 27.05", new[] { "FFL" }) == 27050, "a prefix can be allowed");
            T.Check(Terrain.ParseLevel("12345", pre, metres: false) == 12345, "labels in mm need no decimal point");

            // labels at their crosses, a 3D point, a contour, a typo and a window label
            var d = new DwgContent();
            for (int i = 0; i < 5; i++)
                for (int j = 0; j < 5; j++)
                {
                    double x = i * 5000, y = j * 5000, z = 10000 + i * 100 + j * 50;
                    d.Markers.Add(new DwgMarker { At = new Pt(x, y, 0), Layer = "Levels", Block = "X" });
                    d.Texts.Add(new DwgText { At = new Pt(x + 300, y + 200, 0), Value = (z / 1000).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), Layer = "Levels", HeightMm = 150 });
                }
            d.Texts.Add(new DwgText { At = new Pt(2500, 2500, 0), Value = "1.05", Layer = "Levels", HeightMm = 150 }); // typo for 10.05, at no cross
            d.Texts.Add(new DwgText { At = new Pt(7000, 7000, 0), Value = "S 0.94", Layer = "Levels", HeightMm = 150 });
            d.Markers.Add(new DwgMarker { At = new Pt(30000, 30000, 10900), Layer = "Levels" });
            d.Lines.Add(new DwgLine { A = new Pt(0, 22000, 10500), B = new Pt(10000, 22000, 10500), Layer = "Contours" });
            d.Lines.Add(new DwgLine { A = new Pt(0, 24000, 0), B = new Pt(10000, 24000, 0), Layer = "Contours" });
            var r = Terrain.Collect(d, new TerrainOptions());
            T.Check(r.SpotLabels == 26 && r.SpotsAtCross == 25 && r.SpotsAtText == 1, "25 labels paired with their crosses; one has none");
            var p = r.Points.FirstOrDefault(q => T.Near(q.X, 5000, 1) && T.Near(q.Y, 10000, 1));
            T.Check(p.Z == 10200, "a height sits at its cross, not at its text");
            T.Check(r.Points3D == 1 && r.Points.Any(q => T.Near(q.Z, 10900, 1)), "a 3D point on the spot layer is used");
            T.Check(r.ContourSegments == 1 && r.FlatContourSegments == 1 && r.ContourPoints >= 6, "a contour at its height is sampled every 2 m; a flat one is not used");
            T.Check(r.Outliers.Count == 1 && r.Outliers[0].Label == "1.05" && !r.Points.Any(q => q.Z < 5000), "a typo far off the rest is dropped and reported");
            T.Check(!r.Points.Any(q => q.Z == 940), "the window label is not ground");

            var b = Terrain.Collect(d, new TerrainOptions { Boundary = new List<Pt> { new Pt(-1, -1), new Pt(10001, -1), new Pt(10001, 10001), new Pt(-1, 10001) }, UseContours = false });
            T.Check(b.OutsideBoundary > 0 && b.Points.All(q => q.X <= 10001 && q.Y <= 10001), "only points inside the boundary");
            var thin = Terrain.Collect(d, new TerrainOptions { MaxPoints = 10 });
            T.Check(thin.Points.Count <= 10 && thin.ThinnedToCellMm > 0, "thinned to max points");
            var flat = Terrain.Collect(new DwgContent(), new TerrainOptions());
            T.Check(flat.Points.Count == 0 && flat.Warnings.Any(w => w.Contains("Fewer than 3")), "no levels: a clear warning");

            // read from a real file: POINT, block insert, LWPOLYLINE contour at its elevation
            string dir = Path.Combine(Path.GetTempPath(), "bgmcp-terrain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var doc = new ACadSharp.CadDocument();
                doc.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Meters;
                var lv = new ACadSharp.Tables.Layer("Spot Levels");
                var ct = new ACadSharp.Tables.Layer("Contours Major");
                doc.Layers.Add(lv); doc.Layers.Add(ct);
                var cross = new ACadSharp.Tables.BlockRecord("SPOT");
                cross.Entities.Add(new ACadSharp.Entities.Line { StartPoint = new CSMath.XYZ(-0.1, 0, 0), EndPoint = new CSMath.XYZ(0.1, 0, 0) });
                doc.BlockRecords.Add(cross);
                doc.Entities.Add(new ACadSharp.Entities.Insert(cross) { InsertPoint = new CSMath.XYZ(10, 10, 0), Layer = lv });
                doc.Entities.Add(new ACadSharp.Entities.TextEntity { Value = "+21.37", InsertPoint = new CSMath.XYZ(10.3, 10.2, 0), Height = 0.15, Layer = lv });
                doc.Entities.Add(new ACadSharp.Entities.Point { Location = new CSMath.XYZ(20, 10, 21.9), Layer = lv });
                var c = new ACadSharp.Entities.LwPolyline { Elevation = 21.5, Layer = ct };
                foreach (var (x, y) in new[] { (0.0, 20.0), (30.0, 20.0) }) c.Vertices.Add(new ACadSharp.Entities.LwPolyline.Vertex(new CSMath.XY(x, y)));
                doc.Entities.Add(c);
                string dxf = Path.Combine(dir, "site.dxf");
                using (var w = new ACadSharp.IO.DxfWriter(dxf, doc, false)) w.Write();
                var f = DwgFile.Read(dxf);
                T.Check(f.Markers.Any(m => m.Block == "SPOT" && T.Near(m.At.X, 10000, 1e-6)) && f.Markers.Any(m => m.Block == null && T.Near(m.At.Z, 21900, 1e-6)), "block inserts and POINTs read as markers, in mm");
                var fr = Terrain.Collect(f, new TerrainOptions());
                T.Check(fr.Points.Any(q => T.Near(q.X, 10000, 1) && T.Near(q.Z, 21370, 1e-6)), "file: label at its block's insertion point");
                T.Check(fr.Points.Any(q => T.Near(q.Z, 21900, 1e-6)) && fr.Points.Count(q => T.Near(q.Z, 21500, 1e-6)) >= 15, "file: 3D point and contour at its elevation");
            }
            catch (Exception ex) { T.Check(false, "terrain file: " + ex.Message); }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
