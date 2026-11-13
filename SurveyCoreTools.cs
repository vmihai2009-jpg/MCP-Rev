// 1.35.0: survey_cores: the stairs and lifts that run up through the building, from the plans, and the slab openings they
// need floor to floor. Each opening is an ordinary floor opening (a sketch a person can edit), made only where the slab is
// still solid over the well.
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
        public static IEnumerable<RevitTool> CoreTools()
        {
            yield return new RevitTool
            {
                Name = "survey_cores",
                Write = true,
                Description = "Vertical cores from the plan DWGs and the slab openings they need: stair flights (stair layers, read as treads) " +
                              "rise through the slab of the floor above, so that slab is open over them and over the flights there that carry " +
                              "on up; lifts (closed lift-sized boxes on lift layers) open every slab between the floors they are drawn on. " +
                              "Preview first (default): per floor, each well, whether the slab is still solid there. apply: true cuts a floor " +
                              "opening (an editable sketch) in each slab that is still solid over a well. Layers by name (stair_layers, " +
                              "lift_layers). One undo step.",
                InputSchema = Schema(
                    ("apply", S("boolean", "Cut the openings (default false: preview)"), false),
                    ("margin_mm", S("number", "Grow each well by this (default 0: the drawn flights' box)"), false),
                    ("stair_layers", Arr("string", "Layer patterns for stairs (default *Stair*, *Step*)"), false),
                    ("lift_layers", Arr("string", "Layer patterns for lifts (default *Lift*)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    bool apply = Args.Bool(a, "apply") == true;
                    double margin = Args.Dbl(a, "margin_mm") ?? 0;
                    var stairL = Args.Strings(a, "stair_layers"); if (stairL.Count == 0) stairL = new List<string> { "*Stair*", "*Step*" };
                    var liftL = Args.Strings(a, "lift_layers"); if (liftL.Count == 0) liftL = new List<string> { "*Lift*" };
                    var floorsPlans = PlanFloors(doc, new List<string>()).OrderBy(x => x.level.Elevation).ToList();
                    if (floorsPlans.Count == 0) throw new ArgumentException("No plan DWGs linked in plan views.");
                    var flights = new List<List<Rect>>(); var lifts = new List<List<Rect>>();
                    foreach (var (level, imp) in floorsPlans)
                    {
                        var ents = CadLink.Read(doc, imp, new ReadStats());
                        flights.Add(SurveyDetail.Stairs(CadLink.Segments(ents, stairL)).Select(g => new Rect(g.Min.X, g.Min.Y, g.Max.X, g.Max.Y)).ToList());
                        lifts.Add(Cores.ClosedBoxes(CadLink.Segments(ents, liftL).Select(s => (s.A, s.B)).ToList()));
                    }
                    var wells = Cores.StairWells(flights); var shafts = Cores.Shafts(lifts);
                    var work = new List<(Floor f, Rect r, string kind)>(); var rows = new List<object>();
                    for (int j = 0; j < floorsPlans.Count; j++)
                    {
                        var level = floorsPlans[j].level;
                        var slabs = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => f.LevelId == level.Id).ToList();
                        var tops = slabs.ToDictionary(f => f.Id.Value, f => Faces(f, true));
                        var items = new List<object>();
                        foreach (var (r0, kind) in wells[j].Select(r => (r, "stair well")).Concat(shafts[j].Select(r => (r, "lift shaft"))))
                        {
                            var r = r0.Grow(margin);
                            // solid where any of the centre and four inset points is still over a slab's top
                            var probes = new[] { r.Centre, new Pt(r.X0 + r.W * 0.15, r.Y0 + r.H * 0.15), new Pt(r.X1 - r.W * 0.15, r.Y0 + r.H * 0.15), new Pt(r.X0 + r.W * 0.15, r.Y1 - r.H * 0.15), new Pt(r.X1 - r.W * 0.15, r.Y1 - r.H * 0.15) };
                            var solidIn = slabs.Where(f => probes.Any(p => ZOn(tops[f.Id.Value], p) != null)).ToList();
                            foreach (var f in solidIn) work.Add((f, r, kind));
                            items.Add(new { kind, box = new[] { Math.Round(r.X0), Math.Round(r.Y0), Math.Round(r.X1), Math.Round(r.Y1) }, sizeMm = new[] { Math.Round(r.W), Math.Round(r.H) }, slabsSolidThere = solidIn.Select(f => f.Id.Value).ToList(), open = solidIn.Count == 0 });
                        }
                        rows.Add(new { level = level.Name, flights = flights[j].Count, lifts = lifts[j].Count, wells = items });
                    }
                    var made = new List<long>(); var failed = new List<string>();
                    if (apply && work.Count > 0)
                        BuildTools.Tx.Run(doc, "MCP: survey cores", () =>
                        {
                            foreach (var (f, r, kind) in work)
                            {
                                try
                                {
                                    double z = (doc.GetElement(f.LevelId) as Level)?.Elevation ?? 0;
                                    XYZ P(double x, double y) => new XYZ(x / Mm, y / Mm, z);
                                    var ca = new CurveArray();
                                    var c = new[] { P(r.X0, r.Y0), P(r.X1, r.Y0), P(r.X1, r.Y1), P(r.X0, r.Y1) };
                                    for (int k = 0; k < 4; k++) ca.Append(Line.CreateBound(c[k], c[(k + 1) % 4]));
                                    var op = doc.Create.NewOpening(f, ca, true);
                                    op.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"MCP|survey_cores|{kind}");
                                    made.Add(op.Id.Value);
                                }
                                catch (Exception ex) { failed.Add($"floor {f.Id.Value} ({kind}): {McpServer.Unwrap(ex).Message}"); }
                            }
                        });
                    var answer = new
                    {
                        applied = apply, floors = rows, toCut = work.Count,
                        openingsMade = apply ? made : null, failed = failed.Count > 0 ? failed : null,
                        next = apply ? "Look at each opening in 3D (survey_section through the stair); edit its sketch to the stair's real edge where the well is not a rectangle." :
                               work.Count == 0 ? "Every well is already open (or none found: check stair_layers / lift_layers)." : "Check the wells in a plan view, then apply: true.",
                    };
                    File.WriteAllText(Path.Combine(ReportsFolder(doc), "survey-cores.json"), JsonSerializer.Serialize(answer, JsonOpts));
                    return answer;
                }
            };
        }
    }
}
