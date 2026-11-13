// 1.24.0: elevation_fit_check: does one height fit hold along each elevation (docs/FACADE-CHECKS.md 3.3, Error B.2)? Each
// elevation's level marks, grouped along the drawing, against the fit the facade tools use; and each mark against the
// model's levels. A part of the drawing whose marks disagree is where detail lands at the wrong height.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> ElevationFitTools()
        {
            yield return new RevitTool
            {
                Name = "elevation_fit_check",
                Description = "Does one height fit hold along each elevation: its level marks grouped along the drawing (a gap of gap_mm " +
                              "starts a new part: another block, or another drawing beside it on the sheet), each part's own offset against " +
                              "the fit the facade tools use for the whole elevation, and each mark against the nearest model level. A part " +
                              "off by more than disagree_mm is listed with its stretch along the facade (u): detail built from that part " +
                              "lands at the wrong height there (cut the elevation's sheet range in elevation_index, or index that part as " +
                              "its own elevation). Changes nothing.",
                InputSchema = Schema(
                    ("facade", S("string", "Facade id, or all (default all)"), false),
                    ("gap_mm", S("number", "Gap between marks along the sheet that starts a new part (default 4000)"), false),
                    ("disagree_mm", S("number", "A part off the whole fit by more than this is reported (default 50)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    string want = Args.Str(a, "facade") ?? "all";
                    double gap = Args.Dbl(a, "gap_mm") ?? 4000, dis = Args.Dbl(a, "disagree_mm") ?? 50;
                    Func<IEnumerable<string>, Dictionary<string, string>> roles = null;
                    try { roles = ReadingFor(app, doc, a)?.Roles; } catch { }
                    double datumMm = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => (l.Name, z: l.Elevation * Mm + datumMm)).ToList();
                    var specs = ElevationSpecs(doc, null).Where(s => want.Equals("all", StringComparison.OrdinalIgnoreCase) || string.Equals(Args.Str(s, "facade"), want, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (specs.Count == 0) throw new ArgumentException("No elevation in elevation-index.json for " + want + ": elevation_index first.");
                    var outList = new List<object>(); int bad = 0;
                    foreach (var spec in specs)
                    {
                        string fid = Args.Str(spec, "facade"), name = Args.Str(spec, "name");
                        try
                        {
                            var fr = ElevationFrame(doc, spec, roles);
                            var xr = Args.Numbers(spec, "sheet_x_range");
                            double xMin = Math.Min(xr[0], xr[1]), xMax = Math.Max(xr[0], xr[1]);
                            var yr = Args.Numbers(spec, "sheet_y_range");
                            double yMin = yr.Count == 2 ? Math.Min(yr[0], yr[1]) : double.MinValue, yMax = yr.Count == 2 ? Math.Max(yr[0], yr[1]) : double.MaxValue;
                            var file = DwgFile.Read(Args.Str(spec, "path"));
                            var role = roles?.Invoke(file.Lines.Select(l => l.Layer).Concat(file.Texts.Select(t => t.Layer)));
                            bool IsLevel(string layer) => role != null && layer != null && role.TryGetValue(layer, out var r) && (r == "LevelText" || r == "HeightText") || DwgFile.LayerMatches(layer, new[] { "*Level*" });
                            var pairs = ElevationFit.MarkPairs(file.Texts.Where(t => IsLevel(t.Layer) && t.At.Y >= yMin && t.At.Y <= yMax),
                                fr.Lines.Where(l => IsLevel(l.Layer)), xMin, xMax);
                            var whole = ElevationFit.FitPairs(pairs.Select(p => (p.y, p.z)).ToList());
                            var parts = ElevationFit.Parts(pairs, whole, gap, dis);
                            bad += parts.Count(p => !p.Agrees);
                            var marks = pairs.Select(p =>
                            {
                                var near = levels.OrderBy(l => Math.Abs(l.z - p.z)).FirstOrDefault();
                                return new { u = Math.Round(fr.U(p.x)), valueMm = Math.Round(p.z), fitMm = Math.Round(whole.Height(p.y)), nearestLevel = near.Name, levelMinusMarkMm = near.Name == null ? (double?)null : Math.Round(near.z - p.z) };
                            }).OrderBy(m => m.u).ToList();
                            outList.Add(new
                            {
                                facade = fid, elevation = name, fit = whole.Note, marks = pairs.Count,
                                parts = parts.Select(p => new
                                {
                                    u = new[] { Math.Round(Math.Min(fr.U(p.X0), fr.U(p.X1))), Math.Round(Math.Max(fr.U(p.X0), fr.U(p.X1))) }, p.Marks,
                                    offFitMm = p.DiffMm, spreadMm = p.SpreadMm, p.Agrees,
                                }).ToList(),
                                disagree = parts.Count(p => !p.Agrees),
                                levelMarks = marks,
                                note = pairs.Count == 0 ? "No level marks: the elevation's heights are assumed drawn 1:1 to datum; nothing to check them against." : null,
                            });
                        }
                        catch (Exception ex) { outList.Add(new { facade = fid, elevation = name, error = McpServer.Unwrap(ex).Message }); }
                    }
                    var answer = new
                    {
                        elevations = outList, partsDisagreeing = bad, disagreeMm = dis,
                        next = "A part that disagrees: its detail is built at the wrong height. Narrow that elevation's sheet_x_range in elevation-index.json to the part that agrees, and index the other part as its own elevation (elevation_index), then rebuild that facade's detail. " +
                               "levelMinusMarkMm far from 0 on many marks: a model level is off the survey (survey_floor_levels).",
                    };
                    File.WriteAllText(Path.Combine(ReportsFolder(doc), "elevation-fit-check.json"), JsonSerializer.Serialize(answer, JsonOpts));
                    return answer;
                }
            };
        }
    }
}
