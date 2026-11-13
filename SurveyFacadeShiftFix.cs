// 1.23.0: facade_shift_fix: the elements facade_correspondence finds shifted (all their edges match the elevation after one
// move along or up) are moved by that shift. Windows and doors slide in their wall; wall sweeps change their height on the
// wall; Generic Models, columns and the like move. Moves of about a storey are left alone unless asked: a whole part of a
// facade a storey out is a level or height-fit fault (facade_course_check commonOffsets), not many wrong elements.
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> FacadeShiftTools()
        {
            yield return new RevitTool
            {
                Name = "facade_shift_fix",
                Write = true,
                Description = "Move the facade elements facade_correspondence calls shifted (their edges match the elevation's lines after one " +
                              "move along the facade or up) by that move: windows and doors slide in their wall, wall sweeps change height on " +
                              "their wall, Generic Models / columns / other detail move. Walls, floors and roofs are never moved. Moves of " +
                              "2.4-4.8 m up or down (a storey) are skipped unless storey_moves: true: check facade_course_check's commonOffsets " +
                              "first, a level or the elevation's height fit may be the cause. Pinned elements are skipped. apply: false " +
                              "(default) only lists the moves. One undo step.",
                InputSchema = Schema(
                    ("facade", S("string", "Facade id (facade_list / elevation-index.json)"), true),
                    ("apply", S("boolean", "Make the moves (default false: list them)"), false),
                    ("tol_mm", S("number", "As facade_correspondence (default 20)"), false),
                    ("max_move_mm", S("number", "Largest move made (default 1500)"), false),
                    ("storey_moves", S("boolean", "Also make storey-sized moves (default false)"), false),
                    ("ids", Arr("integer", "Only these elements"), false)),
                Run = (app, a) =>
                {
                    bool apply = Args.Bool(a, "apply") == true;
                    var doc = apply ? Writable(app) : Doc(app);
                    var c = Correspond(app, doc, a);
                    if (c.drawn == null) return new { facade = c.fid, note = "The elevation has no building lines in its sheet range: nothing to move against." };
                    double max = Args.Dbl(a, "max_move_mm") ?? 1500; bool storey = Args.Bool(a, "storey_moves") == true;
                    var only = new HashSet<long>((a["ids"] as System.Text.Json.Nodes.JsonArray)?.Select(x => (long)x.GetValue<double>()) ?? Enumerable.Empty<long>());
                    var moves = new List<Dictionary<string, object>>();
                    var todo = new List<(Element e, ElementMatch m)>();
                    foreach (var m in c.m.Elements.Where(x => x.Verdict == "shifted").OrderByDescending(x => x.LengthMm))
                    {
                        if (only.Count > 0 && !only.Contains(m.Element)) continue;
                        var e = doc.GetElement(new ElementId(m.Element));
                        var item = new Dictionary<string, object>
                        {
                            ["id"] = m.Element, ["label"] = c.labels.TryGetValue(m.Element, out var lb) ? lb : null, ["kind"] = m.Kind,
                            ["moveMm"] = new[] { Math.Round(m.ShiftU), Math.Round(m.ShiftZ) }, ["matchedNow"] = Math.Round(m.MatchedShare, 2), ["matchedAfter"] = Math.Round(m.ShiftedShare, 2),
                        };
                        string skip = e == null ? "not in the model" : m.Kind == "wall" || m.Kind == "floor" || m.Kind == "roof" ? "walls, floors and roofs are not moved here (survey_autofix)"
                            : e.Pinned ? "pinned" : Math.Max(Math.Abs(m.ShiftU), Math.Abs(m.ShiftZ)) > max && !(storey && Math.Abs(m.ShiftZ) <= 4800) ? $"more than max_move_mm ({max:0})"
                            : !storey && Math.Abs(m.ShiftZ) >= 2400 && Math.Abs(m.ShiftZ) <= 4800 ? "a storey: check facade_course_check commonOffsets first (storey_moves: true to move anyway)"
                            : e is WallSweep && Math.Abs(m.ShiftU) > 1 ? "a wall sweep moves only up or down" : null;
                        if (skip != null) item["skipped"] = skip; else todo.Add((e, m));
                        moves.Add(item);
                    }
                    var notes = new List<string>(); int done = 0;
                    if (apply && todo.Count > 0)
                        notes = BuildTools.Tx.Run(doc, $"MCP: facade shift fix {c.fid}", () =>
                        {
                            foreach (var (e, m) in todo)
                            {
                                var item = moves.First(x => (long)x["id"] == m.Element);
                                try
                                {
                                    if (e is WallSweep ws)
                                    {
                                        var info = ws.GetWallSweepInfo();
                                        info.Distance += (info.DistanceMeasuredFrom == DistanceMeasuredFrom.Top ? -1 : 1) * m.ShiftZ / Mm;
                                        // no setter for a sweep's placement: made again at the new height, on the same walls
                                        var hosts = ws.GetHostIds().ToList(); var type = ws.GetTypeId();
                                        doc.Delete(ws.Id);
                                        long first = 0; foreach (var h in hosts) { var nws = WallSweep.Create((Wall)doc.GetElement(h), type, info); if (first == 0) first = nws.Id.Value; }
                                        item["newId"] = first;
                                    }
                                    else ElementTransformUtils.MoveElement(doc, e.Id, new XYZ(c.fr.Ux * m.ShiftU / Mm, c.fr.Uy * m.ShiftU / Mm, m.ShiftZ / Mm));
                                    item["moved"] = true; done++;
                                }
                                catch (Exception ex) { item["failed"] = McpServer.Unwrap(ex).Message; }
                            }
                        });
                    return new
                    {
                        facade = c.fid, applied = apply, shifted = moves.Count, moved = done, toMove = todo.Count,
                        moves, notes = notes.Count > 0 ? notes.Take(10).ToList() : null,
                        next = apply ? "facade_check on this facade: coverage and precision should rise; anything still shifted needs a person" : "apply: true to make the moves",
                    };
                }
            };
        }
    }
}
