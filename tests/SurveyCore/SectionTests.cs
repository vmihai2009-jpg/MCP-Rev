using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class SectionTests
    {
        static SecSeg L(double u0, double z0, double u1, double z1, string layer = "Profile") => new SecSeg(u0, z0, u1, z1, layer);

        // a closed outline as section segments
        static List<SecSeg> Box(double u0, double z0, double u1, double z1) => new List<SecSeg> { L(u0, z0, u1, z0), L(u1, z0, u1, z1), L(u1, z1, u0, z1), L(u0, z1, u0, z0) };

        static List<SecSeg> Poly(params double[] uz)
        {
            var r = new List<SecSeg>();
            for (int i = 0; i + 3 < uz.Length; i += 2) r.Add(L(uz[i], uz[i + 1], uz[i + 2], uz[i + 3]));
            r.Add(L(uz[uz.Length - 2], uz[uz.Length - 1], uz[0], uz[1]));
            return r;
        }

        public static void Run()
        {
            Console.WriteLine("sections and curved walls (3D score)");
            var g = new FidelityGate();

            // ---- a section that agrees: wall, slab, flat roof, parapet ----
            var wall = new SecElement { Id = 1, Kind = "wall", Cut = Box(0, 0, 300, 6900) };
            var slab = new SecElement { Id = 2, Kind = "floor", Cut = Box(300, 2800, 8000, 3000) };
            var roof = new SecElement { Id = 3, Kind = "roof", Cut = Box(300, 5800, 8000, 6000) };
            var good = new SectionInput { Name = "A", Elements = { wall, slab, roof } };
            good.Lines.AddRange(Box(0, 0, 300, 6900)); good.Lines.AddRange(Box(300, 2800, 8000, 3000)); good.Lines.AddRange(Box(300, 5800, 8000, 6000));
            var (s0, i0) = SectionFit.Measure(good, g);
            T.Check(s0.Pass && s0.WithinTolPct == 100 && s0.Verified == 3, $"section: a model drawn as the section passes ({s0.WithinTolPct}%, {string.Join(",", s0.FailedOn)})");
            T.Check(s0.Items.First(x => x.Id == 1).Parapet, "section: a wall standing 900 mm above the roof beside it is a parapet");
            T.Check(i0.Count == 0, $"section: no issues when it agrees ({i0.Count})");

            // ---- slab 40 mm low, parapet missing (model wall stops at the roof), slab 30 mm too thin ----
            var low = new SectionInput
            {
                Name = "B",
                Elements =
                {
                    new SecElement { Id = 1, Kind = "wall", Cut = Box(0, 0, 300, 6000) },
                    new SecElement { Id = 2, Kind = "floor", Cut = Box(300, 2790, 8000, 2960) },
                    new SecElement { Id = 3, Kind = "roof", Cut = Box(300, 5800, 8000, 6000) },
                },
            };
            low.Lines.AddRange(good.Lines);
            var (s1, i1) = SectionFit.Measure(low, g);
            var top = i1.FirstOrDefault(x => x.Kind == "slab_top");
            T.Check(top != null && T.Near(top.DevMm, 40, 1), $"section: slab top 40 mm low found ({top?.DevMm})");
            var th = i1.FirstOrDefault(x => x.Kind == "slab_thickness");
            T.Check(th != null && T.Near(th.DevMm, 30, 1), $"section: slab 30 mm too thin found ({th?.DevMm})");
            var par = i1.FirstOrDefault(x => x.Kind == "parapet_top");
            T.Check(par != null && T.Near(par.DevMm, 900, 1) && par.Id == 1, $"section: missing parapet found on the wall below it ({par?.Kind} {par?.DevMm})");
            T.Check(!s1.Pass && s1.FailedOn.Contains("slabs") && s1.FailedOn.Contains("wall tops"), "section: fails on slabs and wall tops");

            // ---- a flat model roof where the section draws a 30 degree pitch ----
            double rise = 3700 * Math.Tan(30 * Math.PI / 180);
            var flatRoof = new SectionInput { Name = "C", Elements = { new SecElement { Id = 3, Kind = "roof", Cut = Box(300, 5800, 8000, 6000) } } };
            flatRoof.Lines.AddRange(Box(300, 5800, 8000, 6000));
            flatRoof.Lines.Add(L(300, 6000, 4000, 6000 + rise)); flatRoof.Lines.Add(L(4000, 6000 + rise, 7700, 6000));
            var (s2, i2) = SectionFit.Measure(flatRoof, g);
            var form = i2.FirstOrDefault(x => x.Kind == "roof_form");
            T.Check(form != null && form.Fix.Contains("slopes_deg 30.0"), $"section: a pitched roof modelled flat is found, with its pitch ({form?.Against})");
            T.Check(s2.FailedOn.Contains("roofs"), "section: fails on roofs");
            T.Check(i2.Any(x => x.Kind == "section_unmatched"), "section: the slopes no model edge follows are listed");

            // ---- pitched roof, 30 in the model, 35 in the section, ridge 300 mm low ----
            double r30 = 3850 * Math.Tan(30 * Math.PI / 180), r35 = 3850 * Math.Tan(35 * Math.PI / 180);
            var pitched = new SectionInput
            {
                Name = "D",
                Elements = { new SecElement { Id = 4, Kind = "roof", Cut = Poly(150, 6000, 4000, 6000 + r30, 7850, 6000, 7850, 5800, 4000, 5800 + r30, 150, 5800) } },
            };
            pitched.Lines.Add(L(150, 6000, 4000, 6000 + r35)); pitched.Lines.Add(L(4000, 6000 + r35, 7850, 6000));
            pitched.Lines.Add(L(150, 5800, 4000, 5800 + r35)); pitched.Lines.Add(L(4000, 5800 + r35, 7850, 5800));
            var (s3, i3) = SectionFit.Measure(pitched, g);
            var pitch = i3.FirstOrDefault(x => x.Kind == "roof_pitch");
            var it3 = s3.Items.First();
            T.Check(pitch != null && T.Near(it3.SurveyPitchDeg ?? 0, 35, 0.2) && T.Near(it3.PitchDevDeg ?? 0, 5, 0.2), $"section: roof pitch 35 against 30 ({it3.SurveyPitchDeg}, {it3.PitchDevDeg})");
            T.Check(i3.Any(x => x.Kind == "roof_ridge" && T.Near(x.DevMm, r35 - r30, 2)), $"section: ridge {r35 - r30:0} mm low found");
            T.Check(it3.EavesDevMm is double ed && Math.Abs(ed) < 2, $"section: the eaves agree ({it3.EavesDevMm})");

            // ---- stairs: 10 risers of 175 drawn, model says 182 ----
            var stairLines = new List<SecSeg>();
            for (int k = 0; k < 10; k++) { stairLines.Add(L(1000 + k * 250, k * 175, 1000 + k * 250, (k + 1) * 175)); stairLines.Add(L(1000 + k * 250, (k + 1) * 175, 1250 + k * 250, (k + 1) * 175)); }
            var (cnt, riser) = SectionFit.Risers(stairLines, 900, 3600, 0, 1800);
            T.Check(cnt == 10 && T.Near(riser, 175, 0.5), $"section: 10 risers of 175 read ({cnt}, {riser})");
            var stair = new SectionInput { Name = "E", Elements = { new SecElement { Id = 5, Kind = "stair", Risers = 10, RiserMm = 182, Cut = Poly(1000, 0, 3500, 1750, 3500, 1550, 1000, -200) } } };
            stair.Lines.AddRange(stairLines);
            var (s4, i4) = SectionFit.Measure(stair, g);
            T.Check(i4.Any(x => x.Kind == "stair_riser" && T.Near(x.DevMm, -7, 0.6)), "section: riser 7 mm lower than the model's found");

            // ---- placing the sheet: titles and ranges, faces matched both ways ----
            var titles = SectionFit.Titles(new[] { new DwgText { Value = "Section A", At = new Pt(10000, 0) }, new DwgText { Value = "SECTION-B", At = new Pt(40000, 0) }, new DwgText { Value = "Sections C-C & D-D", At = new Pt(70000, 0) } });
            T.Check(titles.Select(t => t.name).SequenceEqual(new[] { "A", "B", "C", "D" }), $"section titles read as Survey to Revit reads them ({string.Join(",", titles.Select(t => t.name))})");
            var rng = SectionFit.SheetRange(titles.Take(3).Select(t => (t.name, t.x)).ToList(), "B");
            T.Check(T.Near(rng.x0, 25000) && T.Near(rng.x1, 55000), "section B's sheet range: halfway to its neighbours");
            var modelFaces = SectionFit.ModelFaces(new[] { new SecElement { Kind = "wall", Cut = Box(0, 0, 300, 3000) }, new SecElement { Kind = "wall", Cut = Box(5000, 0, 5215, 3000) }, new SecElement { Kind = "wall", Cut = Box(9000, 0, 9340, 3000) } });
            T.Check(modelFaces.Count == 6, "model wall faces on the cut: two per wall");
            var sheet = modelFaces.Select(u => new DwgLine { A = new Pt(80000 - u, 100), B = new Pt(80000 - u, 3100), Layer = "Profile" }).ToList();
            var along = ElevationFit.Along(SectionFit.SheetFaces(sheet), modelFaces, 1, 60);
            T.Check(along.Dir == -1 && along.Matched == 6 && T.Near(along.Along(80000 - 5000), 5000, 1), $"section sheet placed on the cut, drawn the other way ({along.Note})");
            var plane = SectionFit.ToPlane(sheet, new HeightFit { A = 1, B = 20000 }, along, 20000);
            T.Check(T.Near(plane[0].U0, 0, 1) && T.Near(plane[0].Z0, 100, 1e-6), "sheet lines onto the plane: u from the faces, z from the level marks less the datum");

            // ---- the overlay picture ----
            var png = SectionFit.Overlay(low, i1, 800);
            T.Check(png.Length > 100 && png[0] == 137 && png[1] == 80 && png[2] == 78 && png[3] == 71, "overlay: a PNG");
            int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            T.Check(w == 800 && h > 400 && h <= 2000, $"overlay: 800 wide, proportioned to the section ({w} x {h})");
            T.Check(PngOk(png), "overlay: chunks and checksums read back");

            // ---- curved walls ----
            var c = CurvedFit.Circle(Enumerable.Range(0, 20).Select(k => new Pt(1000 + 4000 * Math.Cos(k * 0.05), -500 + 4000 * Math.Sin(k * 0.05))).ToList());
            T.Check(c != null && T.Near(c.Cx, 1000, 0.5) && T.Near(c.Cy, -500, 0.5) && T.Near(c.R, 4000, 0.5) && c.RmsMm < 0.01, "circle fitted to points on an arc");
            T.Check(CurvedFit.Circle(new List<Pt> { new Pt(0, 0), new Pt(1000, 0), new Pt(2000, 0) }) == null, "no circle through points in a line");
            // a quarter-circle wall, centre line R 5000, 300 thick; the DWG draws it 20 mm bigger (faces at 4870 and 5170), as 48 short pieces
            var arcWall = new ArcWall { Id = 9, Cx = 0, Cy = 0, Radius = 5000, Start = 0, Sweep = Math.PI / 2, ThicknessMm = 300 };
            var dwg = new List<Seg>();
            foreach (double r in new[] { 4870.0, 5170.0 })
                for (int k = 0; k < 48; k++)
                {
                    double a0 = Math.PI / 2 * k / 48, a1 = Math.PI / 2 * (k + 1) / 48;
                    dwg.Add(new Seg(new Pt(r * Math.Cos(a0), r * Math.Sin(a0)), new Pt(r * Math.Cos(a1), r * Math.Sin(a1)), 0));
                }
            var cr = CurvedFit.Measure(arcWall, dwg, 10);
            T.Check(cr.Verified && T.Near(cr.WorstFaceMm, 20, 2), $"curved wall: 20 mm out on both faces ({cr.WorstFaceMm:0.0})");
            T.Check(cr.NewRadius is double nr && T.Near(nr, 5020, 2) && T.Near(cr.NewThicknessMm ?? 0, 300, 2) && T.Near(cr.NewCx ?? 99, 0, 2), $"curved wall: refitted to R {cr.NewRadius:0}, {cr.NewThicknessMm:0} thick");
            var fi = new FidelityInput { Level = "L0", ArcWalls = { arcWall }, WallFaces = dwg };
            var (fs, fis) = Fidelity.Measure(fi, g);
            T.Check(fs.Walls.CurvedChecked == 1 && fs.Walls.Checked == 1 && fis.Any(x => x.Kind == "wall_face_curved" && x.Id == 9), "curved walls are in the floor's wall score");
            var okDwg = dwg.Select(sg => new Seg(Scale(sg.A, 5000.0 / 5020), Scale(sg.B, 5000.0 / 5020), 0)).ToList();
            var (fs2, _) = Fidelity.Measure(new FidelityInput { Level = "L0", ArcWalls = { arcWall }, WallFaces = okDwg }, g);
            T.Check(fs2.Walls.WithinTolPct >= 95, $"a curved wall on its DWG passes ({fs2.Walls.WithinTolPct}%)");
            // Survey to Revit's reading gives the faces as true arcs (degrees, anticlockwise)
            var fromReading = new FidelityInput { Level = "L0", ArcWalls = { arcWall }, WallArcs = { new[] { 0.0, 0, 4870, 0, 90 }, new[] { 0.0, 0, 5170, 0, 90 } } };
            var (fs3, fis3) = Fidelity.Measure(fromReading, g);
            T.Check(fs3.Walls.Checked == 1 && fis3.Any(x => x.Kind == "wall_face_curved" && x.Fix.Contains("radius 5020")), $"curved walls measured against the reading's arcs ({fis3.FirstOrDefault()?.Fix})");
            T.Check(CurvedFit.Pieces(new[] { 0.0, 0, 1000, 350, 10 }).All(sg => sg.A.X > 900), "an arc across 0 degrees runs anticlockwise the short way");
        }

        static Pt Scale(Pt p, double k) => new Pt(p.X * k, p.Y * k);

        static bool PngOk(byte[] png)
        {
            int o = 8;
            var names = new List<string>();
            while (o + 12 <= png.Length)
            {
                int len = (png[o] << 24) | (png[o + 1] << 16) | (png[o + 2] << 8) | png[o + 3];
                string type = System.Text.Encoding.ASCII.GetString(png, o + 4, 4);
                names.Add(type);
                o += 12 + len;
            }
            return o == png.Length && names.First() == "IHDR" && names.Contains("IDAT") && names.Last() == "IEND";
        }
    }
}
