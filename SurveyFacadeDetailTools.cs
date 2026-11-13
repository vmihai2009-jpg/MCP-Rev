// 1.8.0: facade_details. A facade's bands (cornices, string courses, fascias, copings) modelled 1:1 from the survey:
// the profile from the section that cuts the facade, the runs from its elevation, swept along the facade's face.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> Phase3()
        {
            yield return new RevitTool
            {
                Name = "facade_details",
                Write = true,
                Description = "Model a facade's horizontal bands 1:1 from the survey: cornices, string courses, fascias, sill bands, parapet copings. The profile of each comes from a section DWG that cuts the facade (its outline past the wall face, height by height; the section and its side are chosen by how well their bands line up with the facade's elevation), the runs along the facade from the elevation (where it draws the band's lines), and each run is swept along the facade's outer face (DirectShape, Generic Models, Comments say the source). dry_run (default true) only reports the bands, their heights, projections and runs. Needs facade_list and an elevation for the facade in elevation-index.json. One undo step.",
                InputSchema = Schema(
                    ("facade", S("string", "Facade id from facade_list (F29...)"), true),
                    ("section_paths", Arr("string", "Section DWGs (default: DWGs next to the facade's elevation whose name or text says section)"), false),
                    ("min_projection_mm", S("number", "Smallest projection that counts as a band (default 15)"), false),
                    ("min_cover", S("number", "Share of the facade the elevation must draw a band along (default 0.15)"), false),
                    ("only", Arr("integer", "Make only these bands (numbers from a dry run)"), false),
                    ("dry_run", S("boolean", "Report only (default true)"), false)),
                Run = (app, a) =>
                {
                    bool dry = Args.Bool(a, "dry_run") != false;
                    var doc = dry ? Doc(app) : Writable(app);
                    string fid = Args.Str(a, "facade") ?? throw new ArgumentException("facade is needed (an id from facade_list).");
                    var spec = ElevationSpecs(doc, null).FirstOrDefault(s => string.Equals(Args.Str(s, "facade"), fid, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"No elevation for {fid} in elevation-index.json: run elevation_index (or add one by hand).");
                    var geo = ElevationGeometry(doc, spec);
                    double minProj = Args.Dbl(a, "min_projection_mm") ?? 15, minCover = Args.Dbl(a, "min_cover") ?? 0.15;

                    // the elevation's horizontal lines as (height above datum, along-the-facade span)
                    var horizontals = geo.lines.Where(l => Math.Abs(l.A.Y - l.B.Y) < 2 && Math.Abs(l.A.X - l.B.X) >= 150
                                                           && !DwgFile.LayerMatches(l.Layer, new[] { "*Window*", "*Door*", "*Text*", "Zz_*", "*Light*", "*Sign*", "*StreetFurn*", "*Service*", "*Profile*" }))
                        .Select(l => (z: geo.heights.Height(l.A.Y), u0: geo.along.Along(l.A.X * geo.heights.A), u1: geo.along.Along(l.B.X * geo.heights.A))).ToList();

                    // sections: every side of every drawing, the one whose bands the elevation draws best
                    var paths = Args.Strings(a, "section_paths");
                    if (paths.Count == 0)
                    {
                        string dir = Path.GetDirectoryName(Args.Str(spec, "path"));
                        paths = Directory.GetFiles(dir, "*.dwg").Where(f => !f.Equals(Args.Str(spec, "path"), StringComparison.OrdinalIgnoreCase))
                            .Where(f => { try { return DwgFile.Read(f).Texts.Any(t => t.Value != null && t.Value.TrimStart().StartsWith("Section", StringComparison.OrdinalIgnoreCase)); } catch { return false; } }).ToList();
                    }
                    var tried = new List<object>();
                    (SectionSide side, List<FacadeBand> bands, double agree, string file) best = (null, null, 0, null);
                    foreach (var p in paths)
                    {
                        var file = DwgFile.Read(p);
                        var prof = file.Lines.Where(l => DwgFile.LayerMatches(l.Layer, new[] { "*Profile*" })).ToList();
                        foreach (var (x0, x1, y0, y1) in ElevationFit.SheetParts(prof, 5000))
                        {
                            var part = prof.Where(l => Math.Min(l.A.X, l.B.X) >= x0 - 1 && Math.Max(l.A.X, l.B.X) <= x1 + 1).ToList();
                            if (part.Count < 20) continue;
                            // heights: the section's level marks (drawn 1:1 above datum, checked)
                            var hf = ElevationFit.Heights(file.Texts.Where(t => ElevationFit.IsLevelText(t, DwgFile.LayerMatches(t.Layer, new[] { "*Level*" }))),
                                                          file.Lines.Where(l => ElevationFit.IsLevelLine(l, DwgFile.LayerMatches(l.Layer, new[] { "*Level*" }), file.Texts)), x0, x1);
                            var mapped = part.Select(l => new DwgLine { A = new Pt(l.A.X, hf.Height(l.A.Y)), B = new Pt(l.B.X, hf.Height(l.B.Y)), Layer = l.Layer }).ToList();
                            string title = file.Texts.Where(t => t.Value != null && t.Value.TrimStart().StartsWith("Section", StringComparison.OrdinalIgnoreCase) && t.At.X >= x0 - 5000 && t.At.X <= x1 + 5000)
                                .OrderBy(t => Math.Abs(t.At.X - (x0 + x1) / 2)).Select(t => t.Value.Trim()).FirstOrDefault() ?? $"{Path.GetFileName(p)} x {x0:0}";
                            foreach (var s in FacadeDetail.Sides(mapped, title))
                            {
                                var bands = FacadeDetail.Bands(s, minProj);
                                double agree = FacadeDetail.Agreement(bands, horizontals, geo.length);
                                tried.Add(new { section = s.Name, bands = bands.Count, agreement = Math.Round(agree, 2) });
                                if (agree > best.agree) best = (s, bands, agree, p);
                            }
                        }
                    }
                    if (best.side == null || best.agree < 0.4)
                        return new { facade = fid, made = 0, note = "No section cuts this facade well enough to give its band profiles (best agreement below 0.4). Bands need a section through the facade.", tried };

                    foreach (var b in best.bands) FacadeDetail.Extent(b, horizontals, geo.length);
                    var bandsOut = best.bands.Select((b, i) => (b, i: i + 1)).Where(x => x.b.Cover >= minCover).ToList();
                    var only = Args.Numbers(a, "only").Select(d => (int)d).ToHashSet();
                    if (only.Count > 0) bandsOut = bandsOut.Where(x => only.Contains(x.i)).ToList();

                    var made = new List<object>();
                    var problems = new List<string>();
                    if (!dry && bandsOut.Count > 0)
                    {
                        double ux = (geo.fb.X - geo.fa.X) / geo.length, uy = (geo.fb.Y - geo.fa.Y) / geo.length;
                        BuildTools.Tx.Run(doc, $"MCP: facade bands on {fid}", () =>
                        {
                            foreach (var (b, i) in bandsOut)
                            {
                                double zModel = b.Z0 - geo.datumMm;
                                // profile across the path: u to the left of the path = into the building, so out is negative
                                var prof = b.Profile.Select(q => (-q.p, q.z - b.Z0)).ToList();
                                foreach (var (u0, u1) in b.Runs)
                                {
                                    var path = new List<XYZ>
                                    {
                                        new XYZ((geo.fa.X + ux * u0) / Mm, (geo.fa.Y + uy * u0) / Mm, zModel / Mm),
                                        new XYZ((geo.fa.X + ux * u1) / Mm, (geo.fa.Y + uy * u1) / Mm, zModel / Mm),
                                    };
                                    try
                                    {
                                        long id = BuildTools.SweepShape(doc, path, prof, $"Band {i} {fid}",
                                            $"MCP|facade_details|{fid} band {i}: profile {best.side.Name}, run from elevation {Args.Str(spec, "name")}");
                                        made.Add(new { band = i, id, u = new[] { Math.Round(u0), Math.Round(u1) } });
                                    }
                                    catch (Exception ex) { problems.Add($"band {i} {u0:0}-{u1:0}: {McpServer.Unwrap(ex).Message}"); }
                                }
                            }
                        });
                    }
                    return new
                    {
                        facade = fid, elevation = Args.Str(spec, "name"), dryRun = dry,
                        section = new { best.side.Name, file = Path.GetFileName(best.file), agreement = Math.Round(best.agree, 2), wallFaceSheetX = Math.Round(best.side.Face) },
                        bands = bandsOut.Select(x => new
                        {
                            band = x.i, aodM = new[] { Math.Round(x.b.Z0 / 1000, 3), Math.Round(x.b.Z1 / 1000, 3) },
                            modelMm = new[] { Math.Round(x.b.Z0 - geo.datumMm), Math.Round(x.b.Z1 - geo.datumMm) },
                            heightMm = Math.Round(x.b.Z1 - x.b.Z0), projectionMm = Math.Round(x.b.MaxProjection),
                            cover = Math.Round(x.b.Cover, 2), runs = x.b.Runs.Count,
                            profile = x.b.Profile.Count <= 24 ? x.b.Profile.Select(q => new[] { q.p, Math.Round(q.z - x.b.Z0) }).ToList() : null,
                        }).ToList(),
                        bandsNotDrawnOnElevation = best.bands.Count - best.bands.Count(b => b.Cover >= minCover),
                        made = made.Count > 0 ? made : null, problems = problems.Count > 0 ? problems : null,
                        tried,
                        next = dry ? "Check the bands against the elevation, then dry_run false (only: [numbers] to make some)." : "survey_fidelity is unaffected (bands are DirectShapes); look at the facade in 3D.",
                    };
                }
            };
        }

        /// <summary>An indexed elevation on its facade: heights and along fits, its lines, the facade line.</summary>
        private static (HeightFit heights, AlongFit along, List<DwgLine> lines, Pt fa, Pt fb, double length, double datumMm) ElevationGeometry(Document doc, JsonObject spec)
        {
            string path = Args.Str(spec, "path") ?? throw new ArgumentException("an elevation needs path");
            var xr = Args.Numbers(spec, "sheet_x_range");
            double xMin = Math.Min(xr[0], xr[1]), xMax = Math.Max(xr[0], xr[1]);
            var yr = Args.Numbers(spec, "sheet_y_range");
            double yMin = yr.Count == 2 ? Math.Min(yr[0], yr[1]) : double.MinValue, yMax = yr.Count == 2 ? Math.Max(yr[0], yr[1]) : double.MaxValue;
            bool InSheet(Pt p) => p.X >= xMin && p.X <= xMax && p.Y >= yMin && p.Y <= yMax;
            var (fa, fb) = FacadeLine(doc, spec);
            double fl = fa.Dist2D(fb), ux = (fb.X - fa.X) / fl, uy = (fb.Y - fa.Y) / fl;
            var file = DwgFile.Read(path);
            var lines = file.Lines.Where(l => InSheet(l.A) && InSheet(l.B)).ToList();
            var winLines = lines.Where(l => DwgFile.LayerMatches(l.Layer, new[] { "*Window*" })).ToList();
            var heights = ElevationFit.Heights(file.Texts.Where(t => ElevationFit.IsLevelText(t, DwgFile.LayerMatches(t.Layer, new[] { "*Level*" })) && t.At.Y >= yMin && t.At.Y <= yMax),
                                               lines.Where(l => ElevationFit.IsLevelLine(l, DwgFile.LayerMatches(l.Layer, new[] { "*Level*" }), file.Texts)), xMin, xMax);
            var jambs = new List<double>();
            foreach (var cat in new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors })
                foreach (var fi in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
                {
                    if (!(fi.Location is LocationPoint lp)) continue;
                    var p = CadLink.ToMm(lp.Point);
                    double u = (p.X - fa.X) * ux + (p.Y - fa.Y) * uy, off = Math.Abs((p.X - fa.X) * -uy + (p.Y - fa.Y) * ux);
                    double w = WidthOf(fi);
                    if (off <= 800 && u > -500 && u < fl + 500 && w > 0) { jambs.Add(u - w / 2); jambs.Add(u + w / 2); }
                }
            int? dir = Args.Dbl(spec, "along_direction") is double dg ? (dg < 0 ? -1 : 1) : (Args.Str(spec, "facade") != null ? 1 : (int?)null);
            var along = ElevationFit.Along(ElevationFit.Jambs(winLines), jambs, heights.A, 60, dir);
            double datumMm = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;
            return (heights, along, lines, fa, fb, fl, datumMm);
        }
    }
}
