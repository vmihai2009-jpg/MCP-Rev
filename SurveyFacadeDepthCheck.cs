// 1.39.0: facade_depth_check, the facade's depth against the survey's sections (read-only). Every facade check before
// this one looks at the facade face-on, so a cornice at the right height but 150 mm too shallow, a string course left
// out, or a guessed 80 mm relief panel over flat wall all score as right. The sections are the only drawings with depth:
// at each one that crosses the facade, the model is cut on the same plane and its projection from its own wall face is
// compared, height by height, with the section's. Core in Survey/ProfileCompare.cs (tests: ProfileCompareTests).
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
        public static IEnumerable<RevitTool> FacadeDepthTools()
        {
            yield return new RevitTool
            {
                Name = "facade_depth_check",
                Description = "How far the facade's detail stands out, against the survey sections (read-only): the face-on checks " +
                              "(facade_correspondence, facade_course_check) can't see depth. At every section in section-index.json that crosses the " +
                              "facade square enough, the model (walls, wall sweeps, reveals, columns, generic models, roofs, windows) is cut on the " +
                              "section's plane and its outline's projection from its own wall face is compared, every 10 mm of height, with the " +
                              "section's projection from the section's own wall face (so a wall in the wrong place, which survey_fidelity scores, " +
                              "doesn't count twice). Per section: share of the detail's height within tolerance_mm, mean and largest error, the " +
                              "wall-face offset between the two, and the worst bands (missing: the section draws detail the model lacks; extra: " +
                              "the model stands out where the section is flush, usually a guessed depth; too_deep / too_shallow) with the element " +
                              "that is outermost there and the tool to fix it. Writes facade-depth-<F>.json.",
                InputSchema = Schema(
                    ("facade", S("string", "Facade id (facade_list / elevation-index.json)"), true),
                    ("sections", Arr("string", "Only these sections (names in section-index.json; default every one crossing the facade)"), false),
                    ("tolerance_mm", S("number", "Largest projection difference that passes (default 10)"), false),
                    ("min_proud_mm", S("number", "Heights where neither stands out this much are plain wall, not judged (default 15)"), false),
                    ("layers", Arr("string", "Section layers to read the outline from (default *Profile*, *Wall*)"), false),
                    ("z_from_mm", S("number", "Lowest height compared, model mm (default 0: below that are vaults and pavement)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    string fid = Args.Str(a, "facade") ?? throw new ArgumentException("facade is needed.");
                    double tol = Args.Dbl(a, "tolerance_mm") ?? 10, minProud = Args.Dbl(a, "min_proud_mm") ?? 15, zFrom = Args.Dbl(a, "z_from_mm") ?? 0;
                    var layerGlobs = Args.Strings(a, "layers"); if (layerGlobs.Count == 0) layerGlobs = new List<string> { "*Profile*", "*Wall*" };
                    var only = Args.Strings(a, "sections");
                    var (fa, fb) = SavedFacade(doc, fid);
                    double fl = fa.Dist2D(fb), ux = (fb.X - fa.X) / fl, uy = (fb.Y - fa.Y) / fl;

                    // outward normal and the facade's wall top, as facade_profile finds them
                    var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.Location is LocationCurve).ToList();
                    if (walls.Count == 0) throw new InvalidOperationException("The model has no walls.");
                    double cx = walls.Average(w => ((LocationCurve)w.Location).Curve.Evaluate(0.5, true).X) * Mm, cy = walls.Average(w => ((LocationCurve)w.Location).Curve.Evaluate(0.5, true).Y) * Mm;
                    double nx = -uy, ny = ux;
                    if ((cx - (fa.X + ux * fl / 2)) * nx + (cy - (fa.Y + uy * fl / 2)) * ny > 0) { nx = -nx; ny = -ny; }
                    double wallTop = walls.Where(w => { var c = ((LocationCurve)w.Location).Curve.Evaluate(0.5, true); double v = (c.X * Mm - fa.X) * nx + (c.Y * Mm - fa.Y) * ny, uu = (c.X * Mm - fa.X) * ux + (c.Y * Mm - fa.Y) * uy; return Math.Abs(v) < 1500 && uu > -500 && uu < fl + 500; })
                        .Select(w => w.get_BoundingBox(null)?.Max.Z * Mm ?? double.MinValue).DefaultIfEmpty(0).Max();
                    double datumMm = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;

                    string file = Path.Combine(ReportsFolder(doc), "section-index.json");
                    if (!File.Exists(file)) throw new ArgumentException("No section-index.json: section_index first.");
                    var secs = (JsonNode.Parse(File.ReadAllText(file))["sections"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                    var extra = new[] { BuiltInCategory.OST_Cornices, BuiltInCategory.OST_Reveals, BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns,
                                        BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors, BuiltInCategory.OST_StairsRailing };
                    var results = new List<object>(); var skipped = new List<string>();
                    int rowsAll = 0, withinAll = 0;
                    var allBands = new List<(string sec, DepthBand b)>();
                    var names = new Dictionary<long, string>();

                    foreach (var sec in secs)
                    {
                        string sname = Args.Str(sec, "name") ?? "?";
                        if (only.Count > 0 && !only.Any(o => string.Equals(o, sname, StringComparison.OrdinalIgnoreCase))) continue;
                        var c = Args.Numbers(sec, "cut"); if (c.Count != 4 || sec["offset"] == null) { skipped.Add($"{sname}: no cut line or fit in the index"); continue; }
                        double dx = c[2] - c[0], dy = c[3] - c[1], len = Math.Sqrt(dx * dx + dy * dy);
                        double den = dx * uy - dy * ux; if (Math.Abs(den) < 1e-9 || len < 1) continue;
                        double t = ((fa.X - c[0]) * uy - (fa.Y - c[1]) * ux) / den * len, uAt = ((fa.X - c[0]) * dy - (fa.Y - c[1]) * dx) / den;
                        if (t < -500 || t > len + 500 || uAt < 0 || uAt > fl) continue; // doesn't cross this facade
                        double cosS = (dx * nx + dy * ny) / len;
                        if (Math.Abs(cosS) < 0.7) { skipped.Add($"{sname}: cuts {fid} at a slant"); continue; }

                        // the section's outline in (out from the facade line, model z)
                        int sdir = (int)(Args.Dbl(sec, "dir") ?? 1); double soff = Args.Dbl(sec, "offset") ?? 0;
                        double sheetX0 = sdir * t + soff, outSign = sdir * Math.Sign(cosS);
                        var xr = Args.Numbers(sec, "sheet_x_range"); var yr = Args.Numbers(sec, "sheet_y_range");
                        DwgContent dwg;
                        try { dwg = DwgFile.Read(Args.Str(sec, "path")); } catch (Exception ex) { skipped.Add($"{sname}: {ex.Message}"); continue; }
                        bool In(Pt p) => (xr.Count != 2 || p.X >= Math.Min(xr[0], xr[1]) && p.X <= Math.Max(xr[0], xr[1])) && (yr.Count != 2 || p.Y >= Math.Min(yr[0], yr[1]) && p.Y <= Math.Max(yr[0], yr[1]));
                        var lines = dwg.Lines.Where(l => In(l.A) && In(l.B)).ToList();
                        var heights = ElevationFit.Heights(dwg.Texts.Where(tx => In(tx.At) && ElevationFit.IsLevelText(tx, DwgFile.LayerMatches(tx.Layer, new[] { "*Level*" }))),
                                                           lines.Where(l => ElevationFit.IsLevelLine(l, DwgFile.LayerMatches(l.Layer, new[] { "*Level*" }), dwg.Texts)),
                                                           xr.Count == 2 ? Math.Min(xr[0], xr[1]) : double.MinValue, xr.Count == 2 ? Math.Max(xr[0], xr[1]) : double.MaxValue);
                        var prof = lines.Where(l => DwgFile.LayerMatches(l.Layer, layerGlobs))
                            .Select(l => (new Pt(outSign * (l.A.X - sheetX0), heights.Height(l.A.Y) - datumMm), new Pt(outSign * (l.B.X - sheetX0), heights.Height(l.B.Y) - datumMm)))
                            .Where(l => Math.Max(l.Item1.X, l.Item2.X) > -3000 && Math.Min(l.Item1.X, l.Item2.X) < 2500).ToList();
                        if (prof.Count == 0) { skipped.Add($"{sname}: no outline lines near {fid} on {string.Join("/", layerGlobs)}"); continue; }
                        double faceS = SectionProfile.WallFace(prof, zFrom, wallTop);
                        double z0 = Math.Max(zFrom, prof.Min(l => Math.Min(l.Item1.Y, l.Item2.Y))), z1 = prof.Max(l => Math.Max(l.Item1.Y, l.Item2.Y));
                        if (z1 - z0 < 100) { skipped.Add($"{sname}: its outline near {fid} is under 100 mm tall"); continue; }
                        var (envS, _) = ProfileCompare.Envelope(prof.Select(l => (new Pt(l.Item1.X - faceS, l.Item1.Y), new Pt(l.Item2.X - faceS, l.Item2.Y), 0L)).ToList(), z0, z1);

                        // the model cut on the same plane, in the same frame
                        var A = new Pt(c[0], c[1]); var B = new Pt(c[2], c[3]);
                        var cut = CutModel(doc, A, B, z0, z1, out int failedCuts, extra);
                        double cdx = dx / len, cdy = dy / len;
                        double Out(double u) => (c[0] + cdx * u - fa.X) * nx + (c[1] + cdy * u - fa.Y) * ny;
                        var modelLines = new List<(Pt a, Pt b, long owner)>();
                        foreach (var e in cut)
                        {
                            names[e.Id] = e.Name;
                            foreach (var sg in e.Cut) modelLines.Add((new Pt(Out(sg.U0), sg.Z0), new Pt(Out(sg.U1), sg.Z1), e.Id));
                        }
                        if (modelLines.Count == 0) { skipped.Add($"{sname}: the model has nothing on this section's plane near {fid}"); continue; }
                        double faceM = SectionProfile.WallFace(modelLines.Select(l => (l.a, l.b)).ToList(), zFrom, wallTop);
                        var (envM, own) = ProfileCompare.Envelope(modelLines.Select(l => (new Pt(l.a.X - faceM, l.a.Y), new Pt(l.b.X - faceM, l.b.Y), l.owner)).ToList(), z0, z1);

                        var below = ProfileCompare.Compare(envS, envM, z0, 10, tol, minProud, 30, own, zFrom, wallTop);
                        var above = ProfileCompare.Compare(envS, envM, z0, 10, tol, minProud, 30, own, wallTop + 10, double.MaxValue);
                        rowsAll += below.Rows + above.Rows; withinAll += below.Within + above.Within;
                        allBands.AddRange(below.Bands.Concat(above.Bands).Select(b => (sname, b)));
                        object Bands(DepthCompareResult r) => r.Bands.Take(8).Select(b => new
                        {
                            z = new[] { b.Z0, b.Z1 }, b.Kind, surveyOutMm = b.SurveyOut, modelOutMm = b.ModelOut,
                            element = b.Element == 0 ? null : (object)new { id = b.Element, name = names.TryGetValue(b.Element, out var n) ? n : null },
                        }).ToList();
                        results.Add(new
                        {
                            section = sname, crossesAtMm = Math.Round(uAt), heights = heights.Note,
                            wallFaceOffsetMm = Math.Round(faceM - faceS), // + : the model's face stands in front of the section's
                            facade = new { pct = below.SharePct, rows = below.Rows, meanMm = below.MeanAbsMm, maxMm = below.MaxAbsMm, worst = Bands(below) },
                            roof = above.Rows == 0 ? null : new { pct = above.SharePct, rows = above.Rows, meanMm = above.MeanAbsMm, maxMm = above.MaxAbsMm, worst = Bands(above) },
                            cutFailures = failedCuts > 0 ? (int?)failedCuts : null,
                        });
                    }

                    double pct = rowsAll == 0 ? 0 : Math.Round(100.0 * withinAll / rowsAll, 1);
                    var worst = allBands.OrderByDescending(x => x.b.Weight).Take(10).Select(x => new
                    {
                        x.sec, z = new[] { x.b.Z0, x.b.Z1 }, x.b.Kind, surveyOutMm = x.b.SurveyOut, modelOutMm = x.b.ModelOut,
                        element = x.b.Element == 0 ? (long?)null : x.b.Element, fix = ProfileCompare.Fix(x.b.Kind),
                    }).ToList();
                    var report = new
                    {
                        facade = fid, tool = "facade_depth_check", toleranceMm = tol, minProudMm = minProud, wallTopMm = Math.Round(wallTop),
                        sectionsChecked = results.Count, depthPct = results.Count == 0 ? (double?)null : pct, sections = results, worst, skipped,
                        note = results.Count == 0
                            ? $"No survey section crosses {fid} square enough to measure its depth: depth here is unchecked (say so in the results, don't guess)."
                            : "depthPct = share of the heights where the section or the model stands out, within tolerance. Goal 95%.",
                    };
                    try
                    {
                        File.WriteAllText(Path.Combine(ReportsFolder(doc), $"facade-depth-{fid}.json"),
                            System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
                    }
                    catch { }
                    return report;
                }
            };
        }
    }
}
