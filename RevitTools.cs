using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BarrGazetas.RevitMcp
{
    /// <summary>A tool that works on the open Revit session. Run is always called on Revit's own thread.</summary>
    internal sealed class RevitTool
    {
        public string Name, Description;
        public JsonObject InputSchema;
        public bool Write;
        public Func<UIApplication, JsonObject, object> Run;
    }

    /// <summary>
    /// The hub's own tools: general Revit work for any model. Tools about one add-in's output belong in that
    /// add-in (see Providers). To add one here: a name, a schema and a Run body; put changes in a Transaction
    /// named "MCP: ..." so they are single undo steps. Lengths go out in millimetres.
    /// </summary>
    internal static class RevitTools
    {
        private const double MmPerFoot = 304.8;
        private const double M2PerFoot2 = 0.09290304;

        public static IEnumerable<RevitTool> All()
        {
            yield return new RevitTool
            {
                Name = "get_model_info",
                Description = "The open Revit project: title, path, active view, levels, phases, worksets, length units, and how many elements each model category holds.",
                InputSchema = McpServer.Obj(),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var counts = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                        .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model)
                        .GroupBy(e => e.Category.Name).OrderByDescending(g => g.Count()).Take(40)
                        .ToDictionary(g => g.Key, g => g.Count());
                    return new
                    {
                        title = doc.Title,
                        path = doc.PathName,
                        isWorkshared = doc.IsWorkshared,
                        isFamilyDocument = doc.IsFamilyDocument,
                        lengthUnit = UnitName(doc),
                        activeView = doc.ActiveView == null ? null : new { id = doc.ActiveView.Id.Value, name = doc.ActiveView.Name, type = doc.ActiveView.ViewType.ToString() },
                        levels = Levels(doc).Select(l => new { id = l.Id.Value, name = l.Name, elevationMm = Mm(l.Elevation) }).ToList(),
                        phases = doc.Phases.Cast<Phase>().Select(p => p.Name).ToList(),
                        worksets = doc.IsWorkshared
                            ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Select(w => w.Name).ToList()
                            : new List<string>(),
                        elementsByCategory = counts,
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "get_selection",
                Description = "The elements the user has selected in Revit right now.",
                InputSchema = McpServer.Obj(("limit", "integer", "Most elements to return (default 100)", false)),
                Run = (app, a) =>
                {
                    var uidoc = UiDoc(app);
                    var ids = uidoc.Selection.GetElementIds();
                    int limit = Int(a, "limit") ?? 100;
                    return new { selected = ids.Count, elements = ids.Take(limit).Select(id => Summary(uidoc.Document, uidoc.Document.GetElement(id))).ToList() };
                }
            };

            yield return new RevitTool
            {
                Name = "select_elements",
                Description = "Select elements in Revit so the user sees them, and optionally zoom to them.",
                InputSchema = McpServer.Obj(("element_ids", "integer[]", "Element ids", true), ("zoom", "boolean", "Zoom the view to them", false)),
                Run = (app, a) =>
                {
                    var uidoc = UiDoc(app);
                    var ids = Ids(uidoc.Document, a, "element_ids", out var missing);
                    uidoc.Selection.SetElementIds(ids);
                    if (Bool(a, "zoom") == true && ids.Count > 0) uidoc.ShowElements(ids);
                    return new { selected = ids.Count, notFound = missing };
                }
            };

            yield return new RevitTool
            {
                Name = "isolate_elements",
                Description = "Show only these elements in the active view (Revit's temporary isolate), or with reset put the view back. Nothing is hidden for good.",
                InputSchema = McpServer.Obj(("element_ids", "integer[]", "Elements to isolate", false), ("reset", "boolean", "Put the view back to normal", false)),
                Run = (app, a) =>
                {
                    var uidoc = UiDoc(app);
                    var doc = uidoc.Document;
                    var view = doc.ActiveView ?? throw new InvalidOperationException("There is no active view.");
                    bool reset = Bool(a, "reset") == true;
                    var ids = reset ? new List<ElementId>() : Ids(doc, a, "element_ids", out _);
                    if (!reset && ids.Count == 0) throw new ArgumentException("Give element_ids to isolate, or reset: true.");
                    using (var tx = new Transaction(doc, reset ? "MCP: reset isolate" : "MCP: isolate elements"))
                    {
                        tx.Start();
                        if (reset) view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                        else view.IsolateElementsTemporary(ids);
                        tx.Commit();
                    }
                    return new { view = view.Name, isolated = ids.Count, reset };
                }
            };

            yield return new RevitTool
            {
                Name = "query_elements",
                Description = "List the elements of a category, e.g. Doors or OST_Doors. Filter by level, by the active view, or by text in a parameter. " +
                              "Name parameters to get their values in the same call (saves asking element by element). Returns the total and up to 'limit' elements.",
                InputSchema = McpServer.Obj(
                    ("category", "string", "Category name ('Walls', 'Doors') or BuiltInCategory ('OST_Walls')", true),
                    ("level", "string", "Only elements on this level (name)", false),
                    ("active_view_only", "boolean", "Only elements visible in the active view", false),
                    ("parameter_name", "string", "Filter: a parameter to test", false),
                    ("parameter_contains", "string", "Filter: text its value must contain (not case-sensitive). Empty text finds elements where it is blank.", false),
                    ("parameters", "string[]", "Parameter names whose values to include for each element", false),
                    ("limit", "integer", "Most elements to return (default 100)", false),
                    ("offset", "integer", "Skip this many first, to page through", false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var cat = ResolveCategory(doc, Str(a, "category"));
                    var collector = Bool(a, "active_view_only") == true && doc.ActiveView != null
                        ? new FilteredElementCollector(doc, doc.ActiveView.Id) : new FilteredElementCollector(doc);
                    IEnumerable<Element> elements = collector.OfCategoryId(cat.Id).WhereElementIsNotElementType();

                    if (Str(a, "level") is string levelName)
                    {
                        var level = FindLevel(doc, levelName);
                        elements = elements.Where(e => LevelOf(doc, e)?.Id == level.Id);
                    }
                    string pName = Str(a, "parameter_name");
                    if (pName != null && a.ContainsKey("parameter_contains"))
                    {
                        string want = Str(a, "parameter_contains") ?? "";
                        elements = elements.Where(e =>
                        {
                            string v = ParamValue(Param(doc, e, pName)) ?? "";
                            return want.Length == 0 ? v.Length == 0 : v.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0;
                        });
                    }
                    var all = elements.ToList();
                    int limit = Int(a, "limit") ?? 100, offset = Math.Max(0, Int(a, "offset") ?? 0);
                    var extra = Strings(a, "parameters");
                    return new
                    {
                        category = cat.Name,
                        total = all.Count,
                        elements = all.Skip(offset).Take(limit).Select(e =>
                        {
                            var s = Summary(doc, e);
                            if (extra.Count > 0) s["parameters"] = extra.ToDictionary(n => n, n => (object)ParamValue(Param(doc, e, n)));
                            return s;
                        }).ToList(),
                        notShown = Math.Max(0, all.Count - offset - limit),
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "get_elements",
                Description = "Details of particular elements by id: category, type, level, where they are (point or line, mm), size of their bounding box, phase, workset, Comments and Mark.",
                InputSchema = McpServer.Obj(("element_ids", "integer[]", "Element ids (up to 100)", true)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var ids = Ids(doc, a, "element_ids", out var missing);
                    return new
                    {
                        elements = ids.Take(100).Select(id =>
                        {
                            var e = doc.GetElement(id);
                            var s = Summary(doc, e);
                            if (e.Location is LocationPoint lp) s["at"] = Point(lp.Point);
                            else if (e.Location is LocationCurve lc && lc.Curve.IsBound)
                            {
                                s["line"] = new[] { Point(lc.Curve.GetEndPoint(0)), Point(lc.Curve.GetEndPoint(1)) };
                                s["lengthMm"] = Mm(lc.Curve.Length);
                            }
                            if (e.get_BoundingBox(null) is BoundingBoxXYZ bb) { s["boxMin"] = Point(bb.Min); s["boxMax"] = Point(bb.Max); }
                            if (e is Wall w) s["thicknessMm"] = Mm(w.Width);
                            if (e is SpatialElement room) { s["areaM2"] = Math.Round(room.Area * M2PerFoot2, 2); s["number"] = room.Number; }
                            AddIfSet(s, "phaseCreated", e.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsValueString());
                            AddIfSet(s, "workset", doc.IsWorkshared ? e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM)?.AsValueString() : null);
                            AddIfSet(s, "comments", e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString());
                            AddIfSet(s, "mark", e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString());
                            return s;
                        }).ToList(),
                        notFound = missing,
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "get_element_parameters",
                Description = "Every parameter of one element (name, value, kind, read-only), and optionally its type's parameters.",
                InputSchema = McpServer.Obj(("element_id", "integer", "Element id", true), ("include_type", "boolean", "Also the type's parameters", false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var e = GetElement(doc, a);
                    object type = null;
                    if (Bool(a, "include_type") == true && doc.GetElement(e.GetTypeId()) is Element t)
                        type = new { id = t.Id.Value, name = t.Name, parameters = Params(t) };
                    return new { element = Summary(doc, e), parameters = Params(e), type };
                }
            };

            yield return new RevitTool
            {
                Name = "set_parameters",
                Write = true,
                Description = "Set parameter values on one or many elements in a single undo step. Values are text: lengths like \"1200\" or \"1200 mm\" are read in the project's units; " +
                              "yes/no as true or false. A type parameter is set by giving the type's element id (it changes every element of that type).",
                InputSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["changes"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "One entry per value to set",
                            ["items"] = McpServer.Obj(
                                ("element_id", "integer", "Element (or type) id", true),
                                ("parameter_name", "string", "Parameter name exactly as in Properties", true),
                                ("value", "string", "New value as text", true)),
                        },
                    },
                    ["required"] = new JsonArray("changes"),
                },
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var changes = (a["changes"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                    if (changes.Count == 0) throw new ArgumentException("\"changes\" is needed: a list of { element_id, parameter_name, value }.");
                    var done = new List<object>();
                    var failed = new List<object>();
                    using (var tx = new Transaction(doc, changes.Count == 1 ? $"MCP: set {Str(changes[0], "parameter_name")}" : $"MCP: set {changes.Count} parameters"))
                    {
                        tx.Start();
                        foreach (var c in changes)
                        {
                            long id = Long(c, "element_id") ?? 0;
                            string name = Str(c, "parameter_name"), value = Str(c, "value") ?? "";
                            try
                            {
                                var e = doc.GetElement(new ElementId(id)) ?? throw new InvalidOperationException("no such element");
                                var p = e.LookupParameter(name ?? "") ?? throw new InvalidOperationException(
                                    Param(doc, e, name) != null ? "that is a type parameter: give the type's id (" + e.GetTypeId().Value + ") to change it for every element of the type" : "no such parameter");
                                if (p.IsReadOnly) throw new InvalidOperationException("read-only");
                                if (!SetValue(p, value)) throw new InvalidOperationException("Revit did not accept the value");
                                done.Add(new { element_id = id, parameter = name, value = ParamValue(p) });
                            }
                            catch (Exception ex) { failed.Add(new { element_id = id, parameter = name, value, problem = ex.Message }); }
                        }
                        if (done.Count == 0) tx.RollBack(); else tx.Commit();
                    }
                    return new { changed = done.Count, failed, values = done.Take(50).ToList(), undo = done.Count == 0 ? null : "One undo step in Revit." };
                }
            };

            yield return new RevitTool
            {
                Name = "delete_elements",
                Write = true,
                Description = "Delete elements. Without confirm it deletes nothing and lists what would go. Elements that depend on them (hosted doors, tags, dimensions) go too. One undo step. Ask the user first.",
                InputSchema = McpServer.Obj(("element_ids", "integer[]", "Element ids", true), ("confirm", "boolean", "true to delete; otherwise a preview", false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var ids = Ids(doc, a, "element_ids", out var missing);
                    if (ids.Count == 0) throw new ArgumentException("None of those elements exist.");
                    if (Bool(a, "confirm") != true)
                        return new
                        {
                            preview = true, wouldDelete = ids.Count, notFound = missing,
                            elements = ids.Take(40).Select(id => Summary(doc, doc.GetElement(id))).ToList(),
                            next = "Nothing was deleted. Ask the user, then call again with confirm: true.",
                        };
                    int removed;
                    using (var tx = new Transaction(doc, $"MCP: delete {ids.Count} element{(ids.Count == 1 ? "" : "s")}"))
                    {
                        tx.Start();
                        removed = doc.Delete(ids).Count;
                        tx.Commit();
                    }
                    return new { deleted = ids.Count, removedWithDependents = removed, notFound = missing, undo = "One undo step in Revit." };
                }
            };

            yield return new RevitTool
            {
                Name = "list_levels",
                Description = "Levels, lowest first, with elevations.",
                InputSchema = McpServer.Obj(),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var units = doc.GetUnits();
                    return Levels(doc).Select(l => new { id = l.Id.Value, name = l.Name, elevationMm = Mm(l.Elevation), display = UnitFormatUtils.Format(units, SpecTypeId.Length, l.Elevation, false) }).ToList();
                }
            };

            yield return new RevitTool
            {
                Name = "list_views",
                Description = "Views and sheets (not templates). Filter by kind (FloorPlan, CeilingPlan, Section, Elevation, ThreeD, DrawingSheet, Schedule, DraftingView...) or by text in the name.",
                InputSchema = McpServer.Obj(("view_type", "string", "Kind of view", false), ("name_contains", "string", "Text in the view's name", false), ("limit", "integer", "Most to return (default 200)", false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    string kind = Str(a, "view_type"), text = Str(a, "name_contains");
                    var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .Where(v => !v.IsTemplate && (kind == null || v.ViewType.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase))
                                    && (text == null || v.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0))
                        .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name).ToList();
                    int limit = Int(a, "limit") ?? 200;
                    return new
                    {
                        total = views.Count,
                        byType = views.GroupBy(v => v.ViewType.ToString()).ToDictionary(g => g.Key, g => g.Count()),
                        views = views.Take(limit).Select(v => new { id = v.Id.Value, name = v.Name, type = v.ViewType.ToString(), sheetNumber = (v as ViewSheet)?.SheetNumber }).ToList(),
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "open_view",
                Description = "Make a view the active one in Revit, by id or exact name.",
                InputSchema = McpServer.Obj(("view_id", "integer", "View id", false), ("view_name", "string", "Exact view name", false)),
                Run = (app, a) =>
                {
                    var uidoc = UiDoc(app);
                    var doc = uidoc.Document;
                    View view = Long(a, "view_id") is long id
                        ? doc.GetElement(new ElementId(id)) as View
                        : new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && v.Name == Str(a, "view_name"));
                    if (view == null || view.IsTemplate) throw new InvalidOperationException("There is no such view. See list_views.");
                    uidoc.RequestViewChange(view);
                    return new { opening = view.Name, type = view.ViewType.ToString() };
                }
            };

            yield return new RevitTool
            {
                Name = "list_types",
                Description = "The types of a category (e.g. wall types, door types) with how many elements use each.",
                InputSchema = McpServer.Obj(("category", "string", "Category name or BuiltInCategory", true)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var cat = ResolveCategory(doc, Str(a, "category"));
                    var used = new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsNotElementType()
                        .GroupBy(e => e.GetTypeId()).ToDictionary(g => g.Key, g => g.Count());
                    return new
                    {
                        category = cat.Name,
                        types = new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsElementType().Cast<ElementType>()
                            .Select(t => new { id = t.Id.Value, family = t.FamilyName, name = t.Name, elements = used.TryGetValue(t.Id, out int n) ? n : 0 })
                            .OrderByDescending(t => t.elements).ThenBy(t => t.family).ThenBy(t => t.name).ToList(),
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "list_rooms",
                Description = "Rooms with number, name, level and area, and how many are unplaced or not enclosed.",
                InputSchema = McpServer.Obj(("level", "string", "Only this level (name)", false), ("limit", "integer", "Most rooms to return (default 300)", false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<SpatialElement>().ToList();
                    if (Str(a, "level") is string levelName)
                    {
                        var level = FindLevel(doc, levelName);
                        rooms = rooms.Where(r => r.LevelId == level.Id).ToList();
                    }
                    var placed = rooms.Where(r => r.Area > 0).OrderBy(r => r.Level?.Elevation ?? 0).ThenBy(r => r.Number, StringComparer.OrdinalIgnoreCase).ToList();
                    int limit = Int(a, "limit") ?? 300;
                    return new
                    {
                        rooms = placed.Count,
                        unplacedOrNotEnclosed = rooms.Count - placed.Count,
                        totalAreaM2 = Math.Round(placed.Sum(r => r.Area) * M2PerFoot2, 1),
                        list = placed.Take(limit).Select(r => new
                        {
                            id = r.Id.Value, number = r.Number, name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                            level = r.Level?.Name, areaM2 = Math.Round(r.Area * M2PerFoot2, 2),
                        }).ToList(),
                        notShown = Math.Max(0, placed.Count - limit),
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "list_schedules",
                Description = "The schedules in the project.",
                InputSchema = McpServer.Obj(),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    return new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                        .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
                        .OrderBy(s => s.Name)
                        .Select(s => new { id = s.Id.Value, name = s.Name, category = Category.GetCategory(doc, s.Definition.CategoryId)?.Name }).ToList();
                }
            };

            yield return new RevitTool
            {
                Name = "get_schedule_data",
                Description = "A schedule's contents as rows of text, as Revit shows them (the first rows are its headings).",
                InputSchema = McpServer.Obj(("schedule_id", "integer", "Schedule id", false), ("schedule_name", "string", "Or its exact name", false), ("max_rows", "integer", "Most rows to return (default 200)", false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    ViewSchedule schedule = Long(a, "schedule_id") is long id
                        ? doc.GetElement(new ElementId(id)) as ViewSchedule
                        : new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(s => s.Name == Str(a, "schedule_name"));
                    if (schedule == null) throw new InvalidOperationException("There is no such schedule. See list_schedules.");
                    var body = schedule.GetTableData().GetSectionData(SectionType.Body);
                    int rows = body.NumberOfRows, cols = body.NumberOfColumns, max = Int(a, "max_rows") ?? 200;
                    var data = new List<List<string>>();
                    for (int r = 0; r < Math.Min(rows, max); r++)
                    {
                        var row = new List<string>();
                        for (int c = 0; c < cols; c++) row.Add(schedule.GetCellText(SectionType.Body, r, c));
                        data.Add(row);
                    }
                    return new { name = schedule.Name, rows, columns = cols, data, notShown = Math.Max(0, rows - max) };
                }
            };

            yield return new RevitTool
            {
                Name = "get_warnings",
                Description = "Revit's warnings for the model, grouped by message with counts and example element ids. Good for a quick health check.",
                InputSchema = McpServer.Obj(("limit", "integer", "Most kinds of warning to return (default 30)", false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var warnings = doc.GetWarnings();
                    int limit = Int(a, "limit") ?? 30;
                    var groups = warnings.GroupBy(w => w.GetDescriptionText()).OrderByDescending(g => g.Count()).ToList();
                    return new
                    {
                        warnings = warnings.Count,
                        kinds = groups.Count,
                        list = groups.Take(limit).Select(g => new
                        {
                            message = g.Key, count = g.Count(),
                            exampleElementIds = g.SelectMany(w => w.GetFailingElements()).Select(id => id.Value).Distinct().Take(12).ToList(),
                        }).ToList(),
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "list_links",
                Description = "Linked Revit models and linked or imported CAD files (DWG), with whether each is loaded.",
                InputSchema = McpServer.Obj(),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    return new
                    {
                        revit = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                            .Select(l => new { id = l.Id.Value, name = l.Name, loaded = l.GetLinkDocument() != null, model = l.GetLinkDocument()?.Title }).ToList(),
                        cad = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                            .Select(i => new
                            {
                                id = i.Id.Value, name = i.Category?.Name, linked = i.IsLinked,
                                view = i.ViewSpecific ? (doc.GetElement(i.OwnerViewId) as View)?.Name : null,
                            }).ToList(),
                    };
                }
            };
        }

        /// <summary>Tools that change geometry: added in 1.3.0 for repairing a model against a survey, but general.</summary>
        public static IEnumerable<RevitTool> Repair()
        {
            yield return new RevitTool
            {
                Name = "set_wall_line",
                Write = true,
                Description = "Give a straight wall a new location line: [x1, y1, x2, y2] in model mm (its height, level and type stay). Walls joined to it follow. One undo step.",
                InputSchema = McpServer.Obj(("wall_id", "integer", "The wall", true), ("line", "number[]", "[x1, y1, x2, y2], model mm", true)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var wall = doc.GetElement(new ElementId(Long(a, "wall_id") ?? 0)) as Wall ?? throw new ArgumentException("wall_id is not a wall.");
                    var line = Numbers(a, "line");
                    if (line.Count != 4) throw new ArgumentException("line is [x1, y1, x2, y2] in mm.");
                    if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Line)) throw new InvalidOperationException("Only a straight wall can be given a new line.");
                    double z = lc.Curve.GetEndPoint(0).Z;
                    var p = new XYZ(line[0] / MmPerFoot, line[1] / MmPerFoot, z);
                    var q = new XYZ(line[2] / MmPerFoot, line[3] / MmPerFoot, z);
                    if (p.DistanceTo(q) < 100 / MmPerFoot) throw new ArgumentException("A wall line must be at least 100 mm long.");
                    var notes = BuildTools.SetWallLineSafely(doc, wall, Line.CreateBound(p, q), "MCP: set wall line");
                    var now = ((LocationCurve)wall.Location).Curve;
                    return new { wall_id = wall.Id.Value, line = new[] { Point(now.GetEndPoint(0)), Point(now.GetEndPoint(1)) }, revit = notes, undo = "One undo step in Revit." };
                }
            };

            yield return new RevitTool
            {
                Name = "move_elements",
                Write = true,
                Description = "Move elements by a distance in model mm (dx, dy, dz). Hosted windows and doors move with their walls. One undo step.",
                InputSchema = McpServer.Obj(("element_ids", "integer[]", "Elements to move", true), ("dx", "number", "mm", false), ("dy", "number", "mm", false), ("dz", "number", "mm", false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var ids = Ids(doc, a, "element_ids", out var missing);
                    if (ids.Count == 0) throw new ArgumentException("None of those elements exist.");
                    double dx = Dbl(a, "dx") ?? 0, dy = Dbl(a, "dy") ?? 0, dz = Dbl(a, "dz") ?? 0;
                    if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) < 0.5) throw new ArgumentException("Give a distance: dx, dy and/or dz in mm.");
                    var pinned = ids.Where(id => doc.GetElement(id).Pinned).Select(id => id.Value).ToList();
                    if (pinned.Count > 0) throw new InvalidOperationException("Pinned, so not moved: " + string.Join(", ", pinned) + ". Unpin them in Revit first if they should move.");
                    var notes = BuildTools.Tx.Run(doc, $"MCP: move {ids.Count} element{(ids.Count == 1 ? "" : "s")}",
                        () => ElementTransformUtils.MoveElements(doc, ids, new XYZ(dx / MmPerFoot, dy / MmPerFoot, dz / MmPerFoot)));
                    return new { moved = ids.Count, notFound = missing, revit = notes, undo = "One undo step in Revit." };
                }
            };

            yield return new RevitTool
            {
                Name = "change_type",
                Write = true,
                Description = "Change elements to another type of the same category (list_types shows them), by type id or by name (\"Family: Type\" or just the type name). One undo step.",
                InputSchema = McpServer.Obj(("element_ids", "integer[]", "Elements to change", true), ("type_id", "integer", "The new type's id", false), ("type_name", "string", "Or its name", false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var ids = Ids(doc, a, "element_ids", out var missing);
                    if (ids.Count == 0) throw new ArgumentException("None of those elements exist.");
                    var first = doc.GetElement(ids[0]);
                    ElementType type;
                    if (Long(a, "type_id") is long tid) type = doc.GetElement(new ElementId(tid)) as ElementType ?? throw new ArgumentException($"{tid} is not a type.");
                    else
                    {
                        string name = Str(a, "type_name") ?? throw new ArgumentException("Give type_id or type_name.");
                        var types = new FilteredElementCollector(doc).OfCategoryId(first.Category.Id).WhereElementIsElementType().Cast<ElementType>().ToList();
                        type = types.FirstOrDefault(t => $"{t.FamilyName}: {t.Name}".Equals(name, StringComparison.OrdinalIgnoreCase))
                            ?? types.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new ArgumentException($"No {first.Category.Name} type called \"{name}\". See list_types.");
                    }
                    var changed = new List<long>();
                    var failed = new List<object>();
                    var notes = BuildTools.Tx.Run(doc, $"MCP: change type to {type.Name}", () =>
                    {
                        foreach (var id in ids)
                        {
                            var e = doc.GetElement(id);
                            try
                            {
                                if (!e.IsValidType(type.Id)) throw new InvalidOperationException("that type does not fit this element");
                                e.ChangeTypeId(type.Id);
                                changed.Add(id.Value);
                            }
                            catch (Exception ex) { failed.Add(new { element_id = id.Value, problem = ex.Message }); }
                        }
                        if (changed.Count == 0) throw new InvalidOperationException("No element could take that type: " + string.Join("; ", failed.Select(f => f.ToString())));
                    });
                    return new { type = $"{type.FamilyName}: {type.Name}", changed = changed.Count, failed, notFound = missing, revit = notes, undo = "One undo step in Revit." };
                }
            };
        }

        // ---------- helpers ----------

        private static UIDocument UiDoc(UIApplication app) =>
            app?.ActiveUIDocument ?? throw new InvalidOperationException("No Revit project is open. Ask the user to open one.");

        private static Document Doc(UIApplication app) => Reference.Current ?? UiDoc(app).Document;

        private static Document Writable(UIApplication app)
        {
            var doc = Doc(app);
            if (doc.IsReadOnly) throw new InvalidOperationException("This project is read-only.");
            if (doc.IsModifiable) throw new InvalidOperationException("Another command is still changing the model. Ask the user to finish it in Revit, then try again.");
            return doc;
        }

        private static double Mm(double feet) => Math.Round(feet * MmPerFoot, 1);
        private static double[] Point(XYZ p) => new[] { Mm(p.X), Mm(p.Y), Mm(p.Z) };

        private static string UnitName(Document doc)
        {
            try { return LabelUtils.GetLabelForUnit(doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId()); }
            catch { return null; }
        }

        private static List<Level> Levels(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();

        private static Level FindLevel(Document doc, string name)
        {
            var levels = Levels(doc);
            return levels.FirstOrDefault(l => l.Name == name)
                ?? levels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"There is no level called \"{name}\". Levels: {string.Join(", ", levels.Select(l => l.Name))}.");
        }

        private static Level LevelOf(Document doc, Element e)
        {
            if (doc.GetElement(e.LevelId) is Level l) return l;
            foreach (var bip in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM })
                if (e.get_Parameter(bip) is Parameter p && p.StorageType == StorageType.ElementId && doc.GetElement(p.AsElementId()) is Level pl) return pl;
            return null;
        }

        private static Element GetElement(Document doc, JsonObject a)
        {
            long id = Long(a, "element_id") ?? throw new ArgumentException("element_id is needed.");
            return doc.GetElement(new ElementId(id)) ?? throw new InvalidOperationException($"There is no element {id}.");
        }

        private static List<ElementId> Ids(Document doc, JsonObject a, string key, out List<long> missing)
        {
            var ids = new List<ElementId>();
            missing = new List<long>();
            foreach (var node in a[key] as JsonArray ?? new JsonArray())
            {
                if (!(node is JsonValue v) || !long.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out long n)) continue;
                var id = new ElementId(n);
                if (doc.GetElement(id) != null) ids.Add(id); else missing.Add(n);
            }
            return ids;
        }

        private static Category ResolveCategory(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("category is needed, e.g. Walls.");
            if (Enum.TryParse(name, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
            {
                var c = Category.GetCategory(doc, bic);
                if (c != null) return c;
            }
            foreach (Category c in doc.Settings.Categories)
                if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return c;
            // "Door" for "Doors", "wall" for "Walls"
            foreach (Category c in doc.Settings.Categories)
                if (c.Name.Equals(name + "s", StringComparison.OrdinalIgnoreCase)) return c;
            throw new InvalidOperationException($"There is no category called \"{name}\". Use the name Revit shows (Walls, Doors, Rooms, Generic Models...) or a BuiltInCategory (OST_Walls).");
        }

        private static Dictionary<string, object> Summary(Document doc, Element e)
        {
            var s = new Dictionary<string, object>();
            if (e == null) return s;
            s["id"] = e.Id.Value;
            string name = null; try { name = e.Name; } catch { }
            AddIfSet(s, "name", name);
            AddIfSet(s, "category", e.Category?.Name);
            AddIfSet(s, "type", doc.GetElement(e.GetTypeId())?.Name);
            AddIfSet(s, "level", LevelOf(doc, e)?.Name);
            return s;
        }

        private static void AddIfSet(Dictionary<string, object> s, string key, string value)
        {
            if (!string.IsNullOrEmpty(value)) s[key] = value;
        }

        /// <summary>A parameter by name on the element or, failing that, on its type.</summary>
        private static Parameter Param(Document doc, Element e, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return e.LookupParameter(name) ?? doc.GetElement(e.GetTypeId())?.LookupParameter(name);
        }

        private static List<object> Params(Element e) =>
            e.Parameters.Cast<Parameter>().Where(p => p.Definition != null).OrderBy(p => p.Definition.Name)
                .Select(p => (object)new { name = p.Definition.Name, value = ParamValue(p), kind = p.StorageType.ToString(), readOnly = p.IsReadOnly }).ToList();

        private static string ParamValue(Parameter p)
        {
            if (p == null || !p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString();
                case StorageType.Integer: return p.AsValueString() ?? p.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double: return p.AsValueString() ?? p.AsDouble().ToString(CultureInfo.InvariantCulture);
                case StorageType.ElementId: return p.AsValueString() ?? p.AsElementId().Value.ToString(CultureInfo.InvariantCulture);
                default: return null;
            }
        }

        private static bool SetValue(Parameter p, string value)
        {
            switch (p.StorageType)
            {
                case StorageType.String: return p.Set(value);
                case StorageType.Integer:
                    if (bool.TryParse(value, out bool b)) return p.Set(b ? 1 : 0);
                    if (value.Equals("yes", StringComparison.OrdinalIgnoreCase)) return p.Set(1);
                    if (value.Equals("no", StringComparison.OrdinalIgnoreCase)) return p.Set(0);
                    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? p.Set(i) : p.SetValueString(value);
                case StorageType.Double:
                    try { if (p.SetValueString(value)) return true; } catch { }
                    return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && p.Set(d);
                case StorageType.ElementId:
                    return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && p.Set(new ElementId(id));
                default: return false;
            }
        }

        // Arguments arrive as loosely typed JSON: a number may come as text and the other way round.
        private static string Str(JsonObject a, string key) => a[key] is JsonValue v ? v.ToString() : null;
        private static bool? Bool(JsonObject a, string key) => a[key] is JsonValue v && bool.TryParse(v.ToString(), out bool b) ? b : (bool?)null;
        private static long? Long(JsonObject a, string key) =>
            a[key] is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? (long)d : (long?)null;
        private static double? Dbl(JsonObject a, string key) =>
            a[key] is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : (double?)null;
        private static List<double> Numbers(JsonObject a, string key) =>
            (a[key] as JsonArray)?.OfType<JsonValue>().Select(v => double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : double.NaN)
                .Where(d => !double.IsNaN(d)).ToList() ?? new List<double>();
        private static int? Int(JsonObject a, string key) => Long(a, key) is long l ? (int)Math.Max(0, Math.Min(int.MaxValue, l)) : (int?)null;
        private static List<string> Strings(JsonObject a, string key) =>
            (a[key] as JsonArray)?.OfType<JsonValue>().Select(v => v.ToString()).Where(s => s.Length > 0).ToList() ?? new List<string>();
    }
}
