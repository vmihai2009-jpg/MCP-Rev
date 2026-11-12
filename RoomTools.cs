using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// 1.10.0: rooms by hand. Survey to Revit's build names the rooms its walls enclose from the plan's labels; this
    /// corrects what it missed or got wrong: a room where a label is (in the area the walls enclose there), a name or
    /// number fixed, a room's height set to the survey's.
    /// </summary>
    internal static class RoomTools
    {
        private const double Mm = 304.8;
        private const double M2PerFoot2 = 0.09290304;

        public static IEnumerable<RevitTool> All()
        {
            yield return new RevitTool
            {
                Name = "place_rooms",
                Write = true,
                Description = "Rooms where the survey plan names them, many in one undo step. Each item: a point (model mm, e.g. where dwg_read finds the room label) and level, " +
                              "with name and/or number. If a room is already there it is renamed; otherwise a room is placed in the area the walls enclose at that point " +
                              "(none if the walls leave it open: close the gap first, or use room separation lines). Or room_id to change one room. " +
                              "height_mm sets the room's height above its level (its upper limit), e.g. to the survey's ceiling. Rooms go in phase Existing unless phase is given.",
                InputSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["items"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "One per room",
                            ["items"] = McpServer.Obj(
                                ("x", "number", "A point inside the room, model mm", false),
                                ("y", "number", "", false),
                                ("level", "string", "Its level (needed with x, y)", false),
                                ("room_id", "integer", "Or the room to change", false),
                                ("name", "string", "Room name, e.g. Kitchen", false),
                                ("number", "string", "Room number", false),
                                ("height_mm", "number", "Height above the level (upper limit)", false)),
                        },
                        ["phase"] = new JsonObject { ["type"] = "string", ["description"] = "Phase (default Existing, else the last)" },
                        ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "Do it, report, undo (default false)" },
                    },
                    ["required"] = new JsonArray("items"),
                },
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var items = (a["items"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                    if (items.Count == 0) throw new ArgumentException("items is needed: [{ x, y, level, name }].");
                    var phase = FindPhase(doc, Str(a, "phase"));
                    bool dry = Bool(a, "dry_run") == true;
                    var results = new List<object>();
                    var pre = BuildTools.Tx.Preprocessor(out var notes, out var blocking);
                    using (var t = new Transaction(doc, $"MCP: {items.Count} room{(items.Count == 1 ? "" : "s")}"))
                    {
                        var fo = t.GetFailureHandlingOptions();
                        fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); fo.SetForcedModalHandling(false);
                        t.SetFailureHandlingOptions(fo);
                        t.Start();
                        try
                        {
                            foreach (var it in items)
                            {
                                try { results.Add(One(doc, it, phase)); }
                                catch (Exception ex) { results.Add(new { action = "failed", why = McpServer.Unwrap(ex).Message, item = it.ToJsonString() }); }
                            }
                        }
                        catch { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); throw; }
                        if (dry) t.RollBack();
                        else if (t.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Revit refused the change, so nothing was changed. " + string.Join(" ", blocking()));
                    }
                    var actions = results.Select(r => (string)r.GetType().GetProperty("action")?.GetValue(r)).ToList();
                    return new
                    {
                        placed = actions.Count(x => x == "placed"), renamed = actions.Count(x => x == "renamed" || x == "changed"),
                        notEnclosed = actions.Count(x => x == "not_enclosed"), failed = actions.Count(x => x == "failed"),
                        phase = phase.Name, results,
                        revit = notes().Concat(blocking()).Distinct().Take(20).ToList(),
                        undo = dry ? "Dry run: done and undone, nothing changed." : "One undo step in Revit.",
                        next = actions.Contains("not_enclosed") ? "not_enclosed: the walls do not close around that point. wall_info / set_wall_extents on the walls near it, or a room separation line, then again." : null,
                    };
                }
            };
        }

        private static object One(Document doc, JsonObject it, Phase phase)
        {
            string action;
            Room room = null;
            if (Long(it, "room_id") is long rid)
            {
                room = doc.GetElement(new ElementId(rid)) as Room ?? throw new ArgumentException($"{rid} is not a room.");
                action = "changed";
            }
            else
            {
                if (!(Dbl(it, "x") is double x) || !(Dbl(it, "y") is double y)) throw new ArgumentException("Give x and y (model mm) and level, or room_id.");
                var level = FindLevel(doc, Str(it, "level"));
                var p = new XYZ(x / Mm, y / Mm, level.Elevation + 1000 / Mm);
                room = doc.GetRoomAtPoint(p, phase);
                if (room != null) action = "renamed";
                else
                {
                    room = PlaceAt(doc, level, phase, p);
                    if (room == null) return new { action = "not_enclosed", level = level.Name, at = new[] { Math.Round(x), Math.Round(y) }, name = Str(it, "name") };
                    action = "placed";
                }
            }
            string was = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
            if (Str(it, "name") is string name) room.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set(name);
            if (Str(it, "number") is string number) room.Number = number;
            if (Dbl(it, "height_mm") is double h && h > 0)
            {
                room.get_Parameter(BuiltInParameter.ROOM_UPPER_LEVEL)?.Set(room.LevelId);
                room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET)?.Set(h / Mm);
            }
            doc.Regenerate();
            return new
            {
                action, room_id = room.Id.Value, number = room.Number, name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                was = action == "placed" ? null : was, level = room.Level?.Name, areaM2 = Math.Round(room.Area * M2PerFoot2, 2),
                heightMm = room.get_Parameter(BuiltInParameter.ROOM_HEIGHT) is Parameter rh ? Math.Round(rh.AsDouble() * Mm) : (double?)null,
            };
        }

        /// <summary>
        /// A room in the enclosed area holding p. Revit gives the areas (plan circuits) but not their outlines, so each
        /// free one is tried in turn: placed, kept if it holds p, else undone.
        /// </summary>
        private static Room PlaceAt(Document doc, Level level, Phase phase, XYZ p)
        {
            doc.Regenerate();
            foreach (PlanCircuit circuit in doc.get_PlanTopology(level, phase).Circuits)
            {
                if (circuit.IsRoomLocated) continue;
                using (var st = new SubTransaction(doc))
                {
                    st.Start();
                    Room r = null;
                    try
                    {
                        r = doc.Create.NewRoom(doc.Create.NewRoom(phase), circuit);
                        doc.Regenerate();
                    }
                    catch { r = null; }
                    if (r != null && r.Area > 0 && r.IsPointInRoom(p)) { st.Commit(); return r; }
                    st.RollBack();
                }
            }
            return null;
        }

        private static Phase FindPhase(Document doc, string name)
        {
            var all = doc.Phases.Cast<Phase>().ToList();
            if (all.Count == 0) throw new InvalidOperationException("The model has no phases.");
            if (name != null)
                return all.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No phase called \"{name}\". Phases: {string.Join(", ", all.Select(p => p.Name))}.");
            return all.FirstOrDefault(p => p.Name.Equals("Existing", StringComparison.OrdinalIgnoreCase)) ?? all.Last();
        }

        private static Level FindLevel(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A level name is needed.");
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            return levels.FirstOrDefault(l => l.Name == name)
                ?? levels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"There is no level called \"{name}\". Levels: {string.Join(", ", levels.Select(l => l.Name))}.");
        }

        private static Document Writable(UIApplication app)
        {
            var doc = app?.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No Revit project is open. Ask the user to open one.");
            if (doc.IsReadOnly) throw new InvalidOperationException("This project is read-only.");
            if (doc.IsModifiable) throw new InvalidOperationException("Another command is still changing the model. Ask the user to finish it in Revit, then try again.");
            return doc;
        }

        private static string Str(JsonObject a, string k) => a?[k] is JsonValue v && v.ToString() is string s && s.Length > 0 ? s : null;
        private static bool? Bool(JsonObject a, string k) => a?[k] is JsonValue v && bool.TryParse(v.ToString(), out bool b) ? b : (bool?)null;
        private static double? Dbl(JsonObject a, string k) =>
            a?[k] is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : (double?)null;
        private static long? Long(JsonObject a, string k) => Dbl(a, k) is double d ? (long)d : (long?)null;
    }
}
