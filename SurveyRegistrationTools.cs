// 1.28.0: registration_check: do the floor plans share one frame? Each floor's survey lines against a reference floor and
// against the floor below: the shift that lines them up (dx, dy, turn) and the residual after it. Elevations' and sections'
// fits are in elevation_fit_check and survey_section.
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
        public static IEnumerable<RevitTool> RegistrationTools()
        {
            yield return new RevitTool
            {
                Name = "registration_check",
                Description = "Do the survey's floor plans share one frame: each floor's survey lines (as survey_fidelity reads them) against a " +
                              "reference floor and against the floor below. Long straight lines of one floor, offset across themselves, mostly " +
                              "land on the other's; walls that really step between floors scatter, a sheet placed off moves them all the same " +
                              "way. Gives per floor the shift (dx, dy mm), the turn (degrees) and the residual after it. A shift of more than " +
                              "tol_mm means that floor's plan (or its CAD link) is off: every wall built from it inherits the error. Changes nothing.",
                InputSchema = Schema(
                    ("reference", S("string", "Floor everything is compared with (default: the floor with the most survey line length)"), false),
                    ("levels", Arr("string", "Only these floors (default all with a plan)"), false),
                    ("tol_mm", S("number", "A shift above this is reported as off (default 3)"), false),
                    ("source", S("string", "As survey_fidelity"), false),
                    ("survey_folder", S("string", "As survey_fidelity"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var source = ReadingFor(app, doc, a);
                    double tol = Args.Dbl(a, "tol_mm") ?? 3;
                    double datumMm = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;
                    var plans = CadLink.All(doc).Where(i => i.ViewSpecific && doc.GetElement(i.OwnerViewId) is ViewPlan vp && vp.GenLevel != null)
                        .GroupBy(i => ((ViewPlan)doc.GetElement(i.OwnerViewId)).GenLevel.Id.Value).ToDictionary(g => g.Key, g => g.First());
                    var wanted = Args.Strings(a, "levels");
                    var floors = new List<(Level level, List<Seg> faces)>();
                    var problems = new List<string>();
                    foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation))
                    {
                        if (!source.Has(level, plans) || (wanted.Count > 0 && !wanted.Any(w => w.Equals(level.Name, StringComparison.OrdinalIgnoreCase)))) continue;
                        try { floors.Add((level, FidelityInputFor(doc, level, plans.TryGetValue(level.Id.Value, out var imp) ? imp : null, datumMm, source).WallFaces)); }
                        catch (Exception ex) { problems.Add($"{level.Name}: {McpServer.Unwrap(ex).Message}"); }
                    }
                    floors = floors.Where(f => f.faces.Count > 0).ToList();
                    if (floors.Count < 2) throw new ArgumentException("Fewer than two floors with survey lines to compare.");
                    string refName = Args.Str(a, "reference");
                    var reference = floors.FirstOrDefault(f => f.level.Name.Equals(refName ?? "", StringComparison.OrdinalIgnoreCase));
                    if (reference.level == null) reference = floors.OrderByDescending(f => f.faces.Sum(s => s.Length2D)).First();
                    object Row(RegistrationResult r) => new { dxMm = r.Dx, dyMm = r.Dy, shiftMm = Math.Round(r.ShiftMm, 1), turnDeg = r.RotDeg, residualMm = r.ResidualMm, sharedM = new[] { r.MatchedAM, r.MatchedBM }, note = r.Note };
                    var rows = new List<object>(); int off = 0;
                    for (int i = 0; i < floors.Count; i++)
                    {
                        var f = floors[i];
                        var toRef = f.level.Id == reference.level.Id ? null : Registration.Align(f.faces, reference.faces);
                        var toBelow = i == 0 ? null : Registration.Align(f.faces, floors[i - 1].faces);
                        bool isOff = toRef != null && toRef.Note == null && (toRef.ShiftMm > tol || Math.Abs(toRef.RotDeg) > 0.02);
                        if (isOff) off++;
                        rows.Add(new { level = f.level.Name, againstReference = toRef == null ? null : Row(toRef), againstFloorBelow = toBelow == null ? null : Row(toBelow), off = isOff });
                    }
                    var answer = new
                    {
                        reference = reference.level.Name, tolMm = tol, floorsOff = off, floors = rows, problems = problems.Count > 0 ? problems : null,
                        next = off == 0 ? "The plans share one frame within tolerance." :
                               "A floor that is off: move its plan (the CAD link in its plan view, or the reading's sheet) by the shift shown, then re-run; walls already built from it move with survey_autofix. " +
                               "If only the floor below disagrees, the two floors may truly step: check with dwg_image before moving anything.",
                    };
                    File.WriteAllText(Path.Combine(ReportsFolder(doc), "registration-check.json"), JsonSerializer.Serialize(answer, JsonOpts));
                    return answer;
                }
            };
        }
    }
}
