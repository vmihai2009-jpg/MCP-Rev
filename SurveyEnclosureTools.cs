// 1.37.0: survey_enclosures: the plan's rooms found from its line work alone (gaps at doors and windows closed, each
// bridge reported), which rooms each opening joins, and the same tracing on the model's walls, so the model's footprint
// and rooms are measured against the plan's without a hand-made reference. Read-only.
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
        public static IEnumerable<RevitTool> EnclosureTools()
        {
            yield return new RevitTool
            {
                Name = "survey_enclosures",
                Description = "Rooms and footprint from each floor's plan line work, compared with the model's walls traced the same way. The plan's " +
                              "wall faces stop at doors and windows, so gaps are closed first: a face that stops is bridged to the facing end of a " +
                              "face on the same line within max_gap_mm (marked \"opening\" when the plan's window/door lines are in the gap, \"gap\" " +
                              "when nothing is drawn there), and corners drawn short are run on to meet. Then closed areas are traced: thin ones are " +
                              "wall bodies, bridged thin ones are openings (with the rooms each one joins), the rest rooms (named from the plan's " +
                              "room labels). The model's walls (centre line +/- half the width) are traced alike; per floor: footprint area and " +
                              "overlap (IoU), each plan room paired with the model room over it (area difference, IoU), plan rooms with no model " +
                              "room (a wall missing or rooms merged) and model rooms with none on the plan (a wall not on the plan). Changes " +
                              "nothing; the bridges are only assumed for the count. Writes enclosures-<level>.json to the reports folder.",
                InputSchema = Schema(
                    ("levels", Arr("string", "Floors (default: every floor with a plan, as survey_fidelity)"), false),
                    ("max_gap_mm", S("number", "Widest door or window gap closed (default 2500)"), false),
                    ("corner_mm", S("number", "How far loose ends are run on to meet at a corner (default 200)"), false),
                    ("wall_max_mm", S("number", "Areas thinner than this are wall bodies, not rooms (default 650)"), false),
                    ("min_room_m2", S("number", "Smaller rooms are left out (default 0.5)"), false),
                    ("source", S("string", "Who reads the plans: auto (default), survey_to_revit, or cad_link"), false),
                    ("survey_folder", S("string", "Folder of the survey DWGs for Survey to Revit (as survey_fidelity)"), false),
                    ("rings", S("boolean", "Return each room's outline points too (default false: the report has them)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var source = ReadingFor(app, doc, a);
                    var o = new EnclosureOptions
                    {
                        MaxGapMm = Args.Dbl(a, "max_gap_mm") ?? 2500, CornerMm = Args.Dbl(a, "corner_mm") ?? 200,
                        WallMaxMm = Args.Dbl(a, "wall_max_mm") ?? 650, MinRoomM2 = Args.Dbl(a, "min_room_m2") ?? 0.5,
                    };
                    bool rings = Args.Bool(a, "rings") == true;
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                    var wanted = Args.Strings(a, "levels");
                    var plans = CadLink.All(doc).Where(i => i.ViewSpecific && doc.GetElement(i.OwnerViewId) is ViewPlan vp && vp.GenLevel != null)
                        .GroupBy(i => ((ViewPlan)doc.GetElement(i.OwnerViewId)).GenLevel.Id.Value).ToDictionary(g => g.Key, g => g.First());
                    var floors = levels.Where(l => source.Has(l, plans) && (wanted.Count == 0 || wanted.Any(w => w.Equals(l.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
                    if (floors.Count == 0) throw new ArgumentException("No floor has a plan to trace (" + source.Note + ")" + (wanted.Count > 0 ? " among: " + string.Join(", ", wanted) : "") + ". list_cad shows the links; survey_reading what Survey to Revit read.");
                    double datumMm = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;
                    string folder = ReportsFolder(doc);
                    var results = new List<object>(); var problems = new List<string>();
                    foreach (var level in floors)
                    {
                        try
                        {
                            var input = FidelityInputFor(doc, level, plans.TryGetValue(level.Id.Value, out var imp) ? imp : null, datumMm, source);
                            var faces = new List<Seg>(input.WallFaces);
                            foreach (var arc in input.WallArcs) faces.AddRange(Enclosures.ArcChords(arc, o.ArcStepMm));
                            var names = source?.Reading?.Level(level.Name)?.Rooms ?? new List<(Pt at, string name)>();
                            var survey = Enclosures.Build(faces, input.OpeningLines, o, closeGaps: true, names: names);
                            var model = Enclosures.Build(Enclosures.WallFaceLines(input.Walls, input.ArcWalls, o.ArcStepMm), null, o, closeGaps: false);
                            var cmp = Enclosures.Compare(survey, model);
                            object Room(Enclosure e) => new
                            {
                                id = e.Id, name = e.Name, areaM2 = Math.Round(e.AreaM2, 2), widthMm = Math.Round(e.WidthMm), at = new[] { Math.Round(e.Centroid.X), Math.Round(e.Centroid.Y) },
                                ring = rings ? e.Ring.Select(p => new[] { Math.Round(p.X), Math.Round(p.Y) }).ToList() : null,
                            };
                            var openings = survey.Faces.Where(f => f.Kind == "opening").Select(f => new
                            {
                                id = f.Id, at = new[] { Math.Round(f.Centroid.X), Math.Round(f.Centroid.Y) }, joins = f.Joins,
                                outside = f.Joins.Count < 2,
                            }).ToList();
                            var full = new
                            {
                                level = level.Name, sources = input.Sources,
                                survey = new
                                {
                                    rooms = survey.Rooms.Select(e => new { id = e.Id, name = e.Name, areaM2 = Math.Round(e.AreaM2, 2), at = new[] { Math.Round(e.Centroid.X), Math.Round(e.Centroid.Y) }, ring = e.Ring.Select(p => new[] { Math.Round(p.X), Math.Round(p.Y) }).ToList() }).ToList(),
                                    footprintM2 = Math.Round(survey.FootprintM2, 2), footprint = survey.Footprint.Select(p => new[] { Math.Round(p.X), Math.Round(p.Y) }).ToList(),
                                    bridges = survey.Bridges.Select(b => new { kind = b.Kind, a = new[] { Math.Round(b.A.X), Math.Round(b.A.Y) }, b = new[] { Math.Round(b.B.X), Math.Round(b.B.Y) }, lengthMm = Math.Round(b.LengthMm) }).ToList(),
                                    openings,
                                },
                                model = new { rooms = model.Rooms.Select(e => new { id = e.Id, areaM2 = Math.Round(e.AreaM2, 2), at = new[] { Math.Round(e.Centroid.X), Math.Round(e.Centroid.Y) }, ring = e.Ring.Select(p => new[] { Math.Round(p.X), Math.Round(p.Y) }).ToList() }).ToList(), footprintM2 = Math.Round(model.FootprintM2, 2) },
                                compare = cmp,
                            };
                            string path = Path.Combine(folder, $"enclosures-{Safe(level.Name)}.json");
                            File.WriteAllText(path, JsonSerializer.Serialize(full, JsonOpts));
                            results.Add(new
                            {
                                level = level.Name,
                                footprint = new { surveyM2 = cmp.SurveyFootprintM2, modelM2 = cmp.ModelFootprintM2, diffPct = cmp.FootprintDiffPct, iou = cmp.FootprintIou },
                                rooms = new { survey = cmp.SurveyRooms, model = cmp.ModelRooms, paired = cmp.Paired, pairedIou90 = cmp.PairedWithin, meanIou = cmp.MeanIou, missingInModel = cmp.MissingInModel, extraInModel = cmp.ExtraInModel },
                                bridges = survey.Bridges.GroupBy(b => b.Kind).ToDictionary(g => g.Key, g => g.Count()),
                                openings = new { count = openings.Count, betweenRooms = openings.Count(x => !x.outside), toOutsideOrUnclosed = openings.Count(x => x.outside) },
                                missingRooms = cmp.Rooms.Where(r => r.Model == null).Take(15).Select(r => new { r.Name, r.SurveyM2, at = r.At }).ToList(),
                                worstPaired = cmp.Rooms.Where(r => r.Model != null).OrderBy(r => r.Iou).Take(10).Select(r => new { r.Name, r.SurveyM2, r.ModelM2, r.AreaDiffPct, r.Iou, at = r.At }).ToList(),
                                surveyRooms = rings ? survey.Rooms.Select(Room).ToList() : null,
                                notes = cmp.Notes.Count > 0 ? cmp.Notes : null,
                                report = path,
                            });
                        }
                        catch (Exception ex) { problems.Add($"{level.Name}: {McpServer.Unwrap(ex).Message}"); }
                    }
                    return new
                    {
                        plans = source.Note,
                        options = new { o.MaxGapMm, o.CornerMm, o.WallMaxMm, o.MinRoomM2, o.SnapMm, o.AlignMm },
                        floors = results,
                        problems = problems.Count > 0 ? problems : null,
                        next = "A plan room with no model room is usually a missing wall (look at its 'at'); a low IoU, a wall in the wrong place " +
                               "(survey_fidelity names it). 'gap' bridges are assumed closures with nothing drawn: check them on the plan. " +
                               "Too few rooms: raise max_gap_mm, or check the wall layers (survey_layer_roles).",
                    };
                }
            };
        }
    }
}
