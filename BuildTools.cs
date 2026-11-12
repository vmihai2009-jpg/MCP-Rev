using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// 1.4.0: tools that create and reshape elements, so a model built from a survey can be finished by hand to match
    /// it one to one: windows and doors, walls, splits and joins, floors, roofs, openings, levels, façade details
    /// (cornices, string courses, piers, sills) and reading the linked survey CAD in model coordinates.
    /// Every change runs through <see cref="Tx"/>: one undo step, no Revit dialogs (joins that cannot be kept are
    /// unjoined, warnings are dismissed and reported; anything that would need elements deleted rolls back instead
    /// and says which elements).
    /// </summary>
    internal static class BuildTools
    {
        private const double MmPerFoot = 304.8;
        private const string DefaultComment = "MCP";

        public static IEnumerable<RevitTool> All()
        {
            // ---------------------------------------------------------------- reading
            yield return new RevitTool
            {
                Name = "cad_lines",
                Description = "Linework of a linked or imported CAD drawing (DWG) already in the model, in model mm, near a point or in a box: lines, arcs and polylines with their layer. " +
                              "Use it to check the model against the survey plan of a floor (the plan linked into that floor's plan view), or against an elevation in a drafting view " +
                              "(then the coordinates are that view's own 2D ones). Text in a DWG is not readable through Revit.",
                InputSchema = McpServer.Obj(
                    ("import_id", "integer", "The CAD instance (list_cad shows them)", false),
                    ("level", "string", "Or: the CAD linked into this level's plan", false),
                    ("x", "number", "Centre, model mm", false), ("y", "number", "", false),
                    ("radius_mm", "number", "How far around (default 1500)", false),
                    ("box", "number[]", "Or a box [xmin, ymin, xmax, ymax] in model mm", false),
                    ("layer_contains", "string", "Only layers whose name contains this text, e.g. Wall or Window", false),
                    ("min_length_mm", "number", "Skip segments shorter than this", false),
                    ("limit", "integer", "Most segments (default 300)", false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var imp = FindImport(doc, a);
                    double? x = Dbl(a, "x"), y = Dbl(a, "y");
                    var box = Numbers(a, "box");
                    double r = Dbl(a, "radius_mm") ?? 1500, minLen = Dbl(a, "min_length_mm") ?? 0;
                    string layer = Str(a, "layer_contains");
                    int limit = Int(a, "limit") ?? 300;
                    if (box.Count != 4 && (x == null || y == null)) throw new ArgumentException("Give x and y (and radius_mm), or box [xmin, ymin, xmax, ymax].");
                    var segs = new List<(string layer, XYZ p, XYZ q, string kind)>();
                    CollectCad(doc, imp.get_Geometry(new Options { ComputeReferences = false, IncludeNonVisibleObjects = false }), segs, 0);
                    var hits = new List<(double d, string layer, XYZ p, XYZ q, string kind)>();
                    foreach (var s in segs)
                    {
                        if (layer != null && (s.layer ?? "").IndexOf(layer, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        double len = s.p.DistanceTo(s.q) * MmPerFoot;
                        if (len < minLen) continue;
                        double d;
                        if (box.Count == 4)
                        {
                            bool inside = InBox(s.p, box) || InBox(s.q, box) || InBox((s.p + s.q) / 2, box);
                            if (!inside) continue;
                            d = 0;
                        }
                        else
                        {
                            d = DistToSeg(new XYZ(x.Value / MmPerFoot, y.Value / MmPerFoot, 0), Flat(s.p), Flat(s.q)) * MmPerFoot;
                            if (d > r) continue;
                        }
                        hits.Add((d, s.layer, s.p, s.q, s.kind));
                    }
                    var list = hits.OrderBy(h => h.d).ToList();
                    return new
                    {
                        import_id = imp.Id.Value,
                        name = ImportName(doc, imp),
                        view = imp.ViewSpecific ? (doc.GetElement(imp.OwnerViewId) as View)?.Name : null,
                        found = list.Count,
                        layers = list.GroupBy(h => h.layer ?? "").ToDictionary(g => g.Key, g => g.Count()),
                        segments = list.Take(limit).Select(h => new
                        {
                            layer = h.layer, kind = h.kind, d = Math.Round(h.d),
                            line = new[] { R(h.p.X), R(h.p.Y), R(h.q.X), R(h.q.Y) },
                            len = Math.Round(h.p.DistanceTo(h.q) * MmPerFoot),
                        }).ToList(),
                        notShown = Math.Max(0, list.Count - limit),
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "list_cad",
                Description = "Every linked or imported CAD drawing in the model: id, file, whether linked, the view it sits in (and that view's level), its layers.",
                InputSchema = McpServer.Obj(),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    return new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Select(i =>
                    {
                        var v = i.ViewSpecific ? doc.GetElement(i.OwnerViewId) as View : null;
                        var layers = new List<string>();
                        if (i.Category?.SubCategories != null) foreach (Category c in i.Category.SubCategories) layers.Add(c.Name);
                        return new
                        {
                            id = i.Id.Value, name = ImportName(doc, i), linked = i.IsLinked,
                            view = v?.Name, viewType = v?.ViewType.ToString(), level = (v as ViewPlan)?.GenLevel?.Name,
                            layers = layers.OrderBy(n => n).ToList(),
                        };
                    }).ToList();
                }
            };

            yield return new RevitTool
            {
                Name = "wall_info",
                Description = "Everything about one wall needed to repair it: line, thickness, type, base and top (levels, offsets, height), what is hosted in it (windows, doors, openings with " +
                              "their position along the wall, sill and size), what is joined at each end and whether joins are allowed there.",
                InputSchema = McpServer.Obj(("wall_id", "integer", "The wall", true)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var wall = GetWall(doc, a, "wall_id");
                    var lc = (LocationCurve)wall.Location;
                    var c = lc.Curve;
                    var p0 = c.GetEndPoint(0);
                    var dir = (c.GetEndPoint(1) - p0).Normalize();
                    var inserts = Hosted(doc, wall).Select(e =>
                    {
                        var s = new Dictionary<string, object> { ["id"] = e.Id.Value, ["category"] = e.Category?.Name };
                        try { s["type"] = doc.GetElement(e.GetTypeId())?.Name; } catch { }
                        if (e is FamilyInstance fi && fi.Location is LocationPoint lp)
                        {
                            s["at"] = Pt(lp.Point);
                            s["alongMm"] = R((lp.Point - p0).DotProduct(dir));
                            s["sillMm"] = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) is Parameter sp ? R(sp.AsDouble()) : (double?)null;
                            var (w, h) = InsertSize(doc, fi);
                            s["widthMm"] = w; s["heightMm"] = h;
                            s["facingFlipped"] = fi.FacingFlipped;
                        }
                        else if (e is Opening op && op.IsRectBoundary && op.BoundaryRect?.Count == 2)
                        {
                            s["corners"] = new[] { Pt(op.BoundaryRect[0]), Pt(op.BoundaryRect[1]) };
                        }
                        return s;
                    }).ToList();
                    return new
                    {
                        wall_id = wall.Id.Value,
                        type = wall.WallType.Name,
                        kind = wall.WallType.Kind.ToString(),
                        line = new[] { Pt(c.GetEndPoint(0)), Pt(c.GetEndPoint(1)) },
                        lengthMm = R(c.Length),
                        thicknessMm = R(wall.Width),
                        baseLevel = (doc.GetElement(wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId()) as Level)?.Name,
                        baseOffsetMm = R(wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble()),
                        topLevel = (doc.GetElement(wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).AsElementId()) as Level)?.Name,
                        topOffsetMm = R(wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).AsDouble()),
                        heightMm = R(wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble()),
                        comments = wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
                        startJoinAllowed = WallUtils.IsWallJoinAllowedAtEnd(wall, 0),
                        endJoinAllowed = WallUtils.IsWallJoinAllowedAtEnd(wall, 1),
                        joinedAtStart = JoinedAt(lc, 0, wall.Id),
                        joinedAtEnd = JoinedAt(lc, 1, wall.Id),
                        inserts,
                    };
                }
            };

            // ---------------------------------------------------------------- windows and doors
            yield return new RevitTool
            {
                Name = "place_inserts",
                Write = true,
                Description = "Place windows or doors in walls, many in one undo step. For each: where (model mm; it goes into the given wall, or the nearest wall on the level within 1.5 m), " +
                              "sill height, width and height (a type of that size is made in the family if there is none), optional family or type, flip. " +
                              "Use the survey: width from the plan opening, sill and head from the elevation or section.",
                InputSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["items"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "One per window or door",
                            ["items"] = McpServer.Obj(
                                ("category", "string", "Windows or Doors", true),
                                ("x", "number", "Centre of the opening in plan, model mm", true),
                                ("y", "number", "", true),
                                ("wall_id", "integer", "Host wall (else the nearest on the level)", false),
                                ("level", "string", "Level, when no wall_id is given (else the wall's base level)", false),
                                ("width_mm", "number", "Opening width", false),
                                ("height_mm", "number", "Opening height", false),
                                ("sill_mm", "number", "Sill above the level (windows; doors default 0)", false),
                                ("family", "string", "Family name (default: the one the model uses most for this category)", false),
                                ("type_name", "string", "Or an existing type, \"Family: Type\" or the type name", false),
                                ("flip_facing", "boolean", "Turn it to face the other way", false),
                                ("flip_hand", "boolean", "Swap the hinge side (doors)", false),
                                ("mark", "string", "Mark", false),
                                ("comments", "string", "Comments (default MCP)", false),
                                ("phase", "string", "Phase created (default: the host wall's)", false)),
                        },
                    },
                    ["required"] = new JsonArray("items"),
                },
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var items = Objects(a, "items");
                    if (items.Count == 0) throw new ArgumentException("items is needed: one { category, x, y, width_mm, height_mm, sill_mm ... } per window or door.");
                    var made = new List<object>();
                    var failed = new List<object>();
                    var notes = Tx.Run(doc, $"MCP: place {items.Count} window{(items.Count == 1 ? "" : "s")}/door{(items.Count == 1 ? "" : "s")}", () =>
                    {
                        int i = 0;
                        foreach (var it in items)
                        {
                            i++;
                            try { made.Add(PlaceInsert(doc, it)); }
                            catch (Exception ex) { failed.Add(new { item = i, problem = ex.Message }); }
                        }
                        if (made.Count == 0) throw new InvalidOperationException("Nothing could be placed: " + string.Join("; ", failed.Select(f => f.ToString())));
                    });
                    return new { placed = made.Count, made, failed, revit = notes, undo = "One undo step in Revit." };
                }
            };

            yield return new RevitTool
            {
                Name = "set_insert",
                Write = true,
                Description = "Reshape or move a placed window or door: width, height, sill (a type of the new size is made if needed), slide it along its wall to a point, flip. One undo step.",
                InputSchema = McpServer.Obj(
                    ("element_id", "integer", "The window or door", true),
                    ("width_mm", "number", "New width", false), ("height_mm", "number", "New height", false),
                    ("sill_mm", "number", "New sill above its level", false),
                    ("x", "number", "Slide to this point (projected onto its wall), model mm", false), ("y", "number", "", false),
                    ("flip_facing", "boolean", "Flip facing", false), ("flip_hand", "boolean", "Flip hand", false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var fi = doc.GetElement(new ElementId(Long(a, "element_id") ?? 0)) as FamilyInstance ?? throw new ArgumentException("element_id is not a window or door.");
                    var notes = Tx.Run(doc, "MCP: change window/door", () =>
                    {
                        double? w = Dbl(a, "width_mm"), h = Dbl(a, "height_mm");
                        if (w != null || h != null)
                        {
                            var (cw, ch) = InsertSize(doc, fi);
                            var sym = SizedSymbol(doc, fi.Symbol, w ?? cw ?? 0, h ?? ch ?? 0);
                            if (sym.Id != fi.Symbol.Id) fi.Symbol = sym;
                            SetInstanceSize(fi, w, h);
                        }
                        if (Dbl(a, "sill_mm") is double sill) SetSill(fi, sill);
                        if (Dbl(a, "x") is double x && Dbl(a, "y") is double y && fi.Host is Wall host && fi.Location is LocationPoint lp)
                        {
                            var target = Project(((LocationCurve)host.Location).Curve, new XYZ(x / MmPerFoot, y / MmPerFoot, lp.Point.Z));
                            var move = new XYZ(target.X - lp.Point.X, target.Y - lp.Point.Y, 0);
                            if (move.GetLength() > 1e-6) ElementTransformUtils.MoveElement(doc, fi.Id, move);
                        }
                        if (Bool(a, "flip_facing") == true && fi.CanFlipFacing) fi.flipFacing();
                        if (Bool(a, "flip_hand") == true && fi.CanFlipHand) fi.flipHand();
                    });
                    var (nw, nh) = InsertSize(doc, fi);
                    return new { element_id = fi.Id.Value, type = fi.Symbol.Name, widthMm = nw, heightMm = nh, at = (fi.Location as LocationPoint) is LocationPoint p ? Pt(p.Point) : null, revit = notes, undo = "One undo step in Revit." };
                }
            };

            // ---------------------------------------------------------------- walls
            yield return new RevitTool
            {
                Name = "create_walls",
                Write = true,
                Description = "Make walls, many in one undo step. Each: centre line [x1, y1, x2, y2] model mm, base level, thickness (a basic wall type of that thickness is used or made) or a type, " +
                              "top level or height, base and top offsets. Phase Existing by default (survey). joins: false keeps its ends from joining (façade returns, reveals).",
                InputSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["items"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = McpServer.Obj(
                                ("line", "number[]", "[x1, y1, x2, y2] centre line, model mm", true),
                                ("level", "string", "Base level", true),
                                ("thickness_mm", "number", "Thickness", false),
                                ("type_name", "string", "Or a wall type (e.g. Concept Curtain Wall)", false),
                                ("top_level", "string", "Top level (else height_mm)", false),
                                ("height_mm", "number", "Unconnected height (default: up to the next level)", false),
                                ("base_offset_mm", "number", "", false),
                                ("top_offset_mm", "number", "", false),
                                ("joins", "boolean", "Let its ends join other walls (default true)", false),
                                ("comments", "string", "Comments (default MCP)", false),
                                ("phase", "string", "Phase created (default Existing)", false)),
                        },
                    },
                    ["required"] = new JsonArray("items"),
                },
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var items = Objects(a, "items");
                    if (items.Count == 0) throw new ArgumentException("items is needed.");
                    var made = new List<object>();
                    var notes = Tx.Run(doc, $"MCP: make {items.Count} wall{(items.Count == 1 ? "" : "s")}", () =>
                    {
                        foreach (var it in items) made.Add(CreateWall(doc, it));
                    });
                    return new { made = made.Count, walls = made, revit = notes, undo = "One undo step in Revit." };
                }
            };

            yield return new RevitTool
            {
                Name = "split_wall",
                Write = true,
                Description = "Split a straight wall in two at a point (projected onto it). Windows, doors and openings stay where they are, each on the part it falls in. " +
                              "Then give each part its own line or type (set_wall_line, change_type), e.g. where a façade steps in or changes thickness. One undo step.",
                InputSchema = McpServer.Obj(("wall_id", "integer", "The wall", true), ("x", "number", "Split point, model mm", true), ("y", "number", "", true),
                    ("gap_mm", "number", "Leave a gap this wide between the parts (default 0)", false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var wall = GetWall(doc, a, "wall_id");
                    long newId = 0;
                    var notes = Tx.Run(doc, "MCP: split wall", () => newId = SplitWall(doc, wall, Dbl(a, "x") ?? 0, Dbl(a, "y") ?? 0, Dbl(a, "gap_mm") ?? 0));
                    var w2 = doc.GetElement(new ElementId(newId)) as Wall;
                    return new
                    {
                        first = new { wall_id = wall.Id.Value, line = Line2(wall), inserts = Hosted(doc, wall).Select(i => i.Id.Value).ToList() },
                        second = new { wall_id = newId, line = Line2(w2), inserts = Hosted(doc, w2).Select(i => i.Id.Value).ToList() },
                        revit = notes, undo = "One undo step in Revit.",
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "set_wall_joins",
                Write = true,
                Description = "Allow or stop walls joining at their ends (start, end or both). Stop joins before moving a façade wall so the walls around it are not dragged along. One undo step.",
                InputSchema = McpServer.Obj(("wall_ids", "integer[]", "Walls", true), ("allow", "boolean", "true to allow, false to stop", true), ("ends", "string", "start, end or both (default both)", false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var ids = Ids(doc, a, "wall_ids", out var missing);
                    bool allow = Bool(a, "allow") ?? true;
                    string ends = (Str(a, "ends") ?? "both").ToLowerInvariant();
                    int changed = 0;
                    var notes = Tx.Run(doc, allow ? "MCP: allow wall joins" : "MCP: stop wall joins", () =>
                    {
                        foreach (var id in ids)
                        {
                            if (!(doc.GetElement(id) is Wall w)) continue;
                            foreach (int end in ends == "start" ? new[] { 0 } : ends == "end" ? new[] { 1 } : new[] { 0, 1 })
                            {
                                if (allow) WallUtils.AllowWallJoinAtEnd(w, end); else WallUtils.DisallowWallJoinAtEnd(w, end);
                            }
                            changed++;
                        }
                    });
                    return new { changed, notFound = missing, revit = notes, undo = "One undo step in Revit." };
                }
            };

            yield return new RevitTool
            {
                Name = "set_wall_extents",
                Write = true,
                Description = "Set walls' base and top: base level and offset, top level and offset, or unconnected height. For parapets, low walls under windows, walls that should stop at a ceiling or run up two floors. One undo step.",
                InputSchema = McpServer.Obj(("wall_ids", "integer[]", "Walls", true),
                    ("base_level", "string", "", false), ("base_offset_mm", "number", "", false),
                    ("top_level", "string", "Top constraint (\"none\" for unconnected)", false), ("top_offset_mm", "number", "", false),
                    ("height_mm", "number", "Unconnected height (sets top to none)", false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var ids = Ids(doc, a, "wall_ids", out var missing);
                    int changed = 0;
                    var notes = Tx.Run(doc, "MCP: set wall base and top", () =>
                    {
                        foreach (var id in ids)
                        {
                            if (!(doc.GetElement(id) is Wall w)) continue;
                            if (Str(a, "base_level") is string bl) w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).Set(FindLevel(doc, bl).Id);
                            if (Dbl(a, "base_offset_mm") is double bo) w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).Set(bo / MmPerFoot);
                            if (Dbl(a, "height_mm") is double hh)
                            {
                                w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(ElementId.InvalidElementId);
                                w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).Set(hh / MmPerFoot);
                            }
                            else if (Str(a, "top_level") is string tl)
                                w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(tl.Equals("none", StringComparison.OrdinalIgnoreCase) ? ElementId.InvalidElementId : FindLevel(doc, tl).Id);
                            if (Dbl(a, "top_offset_mm") is double to) w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).Set(to / MmPerFoot);
                            changed++;
                        }
                    });
                    return new { changed, notFound = missing, revit = notes, undo = "One undo step in Revit." };
                }
            };

            // ---------------------------------------------------------------- floors, roofs, openings, levels
            yield return new RevitTool
            {
                Name = "create_floor",
                Write = true,
                Description = "Make a floor (slab) from outline loops in model mm: the first loop is the outside, any others are holes (stair wells, lightwells). Thickness picks or makes a floor type. " +
                              "With replace_id the old floor is deleted and this one takes its place (same level and type unless given): the way to correct a slab edge. One undo step.",
                InputSchema = McpServer.Obj(
                    ("loops", "array", "[[ [x,y], [x,y], ... ], [hole...]] model mm, each loop closed implicitly", true),
                    ("level", "string", "Level (default: replaced floor's)", false),
                    ("offset_mm", "number", "Top of slab above the level (default 0)", false),
                    ("thickness_mm", "number", "Thickness (default: replaced floor's, else 300)", false),
                    ("type_name", "string", "Or a floor type", false),
                    ("replace_id", "integer", "A floor to replace", false),
                    ("comments", "string", "Comments (default MCP)", false),
                    ("phase", "string", "Phase created (default Existing)", false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var loops = Loops(a, "loops", 0);
                    if (loops.Count == 0) throw new ArgumentException("loops is needed: [[[x,y],[x,y],[x,y],...]] in model mm.");
                    Floor old = Long(a, "replace_id") is long rid ? doc.GetElement(new ElementId(rid)) as Floor ?? throw new ArgumentException($"{rid} is not a floor.") : null;
                    Level level = Str(a, "level") is string ln ? FindLevel(doc, ln) : old != null ? doc.GetElement(old.LevelId) as Level : throw new ArgumentException("level is needed.");
                    long newId = 0;
                    var notes = Tx.Run(doc, old == null ? "MCP: make floor" : "MCP: reshape floor", () =>
                    {
                        FloorType ft;
                        if (Str(a, "type_name") is string tn) ft = TypeByName<FloorType>(doc, BuiltInCategory.OST_Floors, tn);
                        else if (Dbl(a, "thickness_mm") is double t) ft = FloorTypeOf(doc, t);
                        else if (old != null) ft = old.FloorType;
                        else ft = FloorTypeOf(doc, 300);
                        var f = Floor.Create(doc, loops, ft.Id, level.Id);
                        double offset = Dbl(a, "offset_mm") ?? (old != null ? old.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).AsDouble() * MmPerFoot : 0);
                        f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(offset / MmPerFoot);
                        Finish(doc, f, a, old?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(), old);
                        if (old != null) doc.Delete(old.Id);
                        newId = f.Id.Value;
                    });
                    return new { floor_id = newId, replaced = old?.Id.Value, revit = notes, undo = "One undo step in Revit." };
                }
            };

            yield return new RevitTool
            {
                Name = "create_roof",
                Write = true,
                Description = "Make a footprint roof from an outline in model mm on a level. slopes_deg gives each edge's pitch in order (0 or null = no slope on that edge): a mansard or pitched roof " +
                              "from the survey roof plan and sections. Flat roofs: all 0. One undo step.",
                InputSchema = McpServer.Obj(
                    ("outline", "array", "[[x,y], [x,y], ...] model mm", true),
                    ("level", "string", "Level the roof sits on", true),
                    ("offset_mm", "number", "Base above the level", false),
                    ("slopes_deg", "number[]", "Pitch per edge, degrees", false),
                    ("thickness_mm", "number", "Thickness (default 300)", false),
                    ("type_name", "string", "Or a roof type", false),
                    ("comments", "string", "", false), ("phase", "string", "", false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var pts = PointList(a["outline"] as JsonArray, 0);
                    if (pts.Count < 3) throw new ArgumentException("outline needs at least 3 points.");
                    var level = FindLevel(doc, Str(a, "level"));
                    var slopes = Numbers(a, "slopes_deg");
                    long id = 0;
                    var notes = Tx.Run(doc, "MCP: make roof", () =>
                    {
                        RoofType rt = Str(a, "type_name") is string tn ? TypeByName<RoofType>(doc, BuiltInCategory.OST_Roofs, tn) : RoofTypeOf(doc, Dbl(a, "thickness_mm") ?? 300);
                        var ca = new CurveArray();
                        for (int i = 0; i < pts.Count; i++) ca.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
                        var roof = doc.Create.NewFootPrintRoof(ca, level, rt, out ModelCurveArray map);
                        int k = 0;
                        foreach (ModelCurve mc in map)
                        {
                            double s = k < slopes.Count ? slopes[k] : 0;
                            roof.set_DefinesSlope(mc, s > 0.01);
                            if (s > 0.01) roof.set_SlopeAngle(mc, Math.Tan(s * Math.PI / 180));
                            k++;
                        }
                        if (Dbl(a, "offset_mm") is double off) roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(off / MmPerFoot);
                        Finish(doc, roof, a, null, null);
                        id = roof.Id.Value;
                    });
                    return new { roof_id = id, revit = notes, undo = "One undo step in Revit." };
                }
            };

            yield return new RevitTool
            {
                Name = "create_opening",
                Write = true,
                Description = "Cut an opening. kind floor: a hole in floor host_id from loop. kind shaft: a vertical shaft from level to top_level through every floor and roof, from loop. " +
                              "kind wall: a rectangular hole in wall host_id between two corners (x1,y1 at the bottom of one side, x2,y2 at the top of the other) given as from [x,y,z] to [x,y,z] model mm. One undo step.",
                InputSchema = McpServer.Obj(
                    ("kind", "string", "floor, shaft or wall", true),
                    ("host_id", "integer", "Floor or wall (floor, wall)", false),
                    ("loop", "array", "[[x,y], ...] model mm (floor, shaft)", false),
                    ("level", "string", "Bottom level (shaft)", false), ("top_level", "string", "Top level (shaft)", false),
                    ("from", "number[]", "[x,y,z] model mm (wall)", false), ("to", "number[]", "[x,y,z] model mm (wall)", false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    string kind = (Str(a, "kind") ?? "").ToLowerInvariant();
                    long id = 0;
                    var notes = Tx.Run(doc, $"MCP: {kind} opening", () =>
                    {
                        Opening op;
                        if (kind == "wall")
                        {
                            var w = GetWall(doc, a, "host_id");
                            var f = Numbers(a, "from"); var t = Numbers(a, "to");
                            if (f.Count != 3 || t.Count != 3) throw new ArgumentException("from and to are [x,y,z] in model mm.");
                            op = doc.Create.NewOpening(w, new XYZ(f[0], f[1], f[2]) / MmPerFoot, new XYZ(t[0], t[1], t[2]) / MmPerFoot);
                        }
                        else
                        {
                            var pts = PointList(a["loop"] as JsonArray, 0);
                            if (pts.Count < 3) throw new ArgumentException("loop needs at least 3 points.");
                            var ca = new CurveArray();
                            for (int i = 0; i < pts.Count; i++) ca.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
                            if (kind == "floor")
                            {
                                var host = doc.GetElement(new ElementId(Long(a, "host_id") ?? 0)) ?? throw new ArgumentException("host_id is needed.");
                                op = doc.Create.NewOpening(host, ca, true);
                            }
                            else if (kind == "shaft")
                                op = doc.Create.NewOpening(FindLevel(doc, Str(a, "level")), FindLevel(doc, Str(a, "top_level")), ca);
                            else throw new ArgumentException("kind is floor, shaft or wall.");
                        }
                        id = op.Id.Value;
                    });
                    return new { opening_id = id, revit = notes, undo = "One undo step in Revit." };
                }
            };

            yield return new RevitTool
            {
                Name = "create_level",
                Write = true,
                Description = "Make a level at an elevation (mm, project), with a floor plan for it unless plan is false. For parapets, roof levels, mezzanines and split floors. One undo step.",
                InputSchema = McpServer.Obj(("name", "string", "Name", true), ("elevation_mm", "number", "Elevation in the model's mm", true), ("plan", "boolean", "Make its floor plan (default true)", false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    string name = Str(a, "name") ?? throw new ArgumentException("name is needed.");
                    long id = 0, viewId = 0;
                    var notes = Tx.Run(doc, "MCP: make level", () =>
                    {
                        var l = Level.Create(doc, (Dbl(a, "elevation_mm") ?? 0) / MmPerFoot);
                        l.Name = name;
                        id = l.Id.Value;
                        if (Bool(a, "plan") != false)
                        {
                            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.FloorPlan);
                            if (vft != null) viewId = ViewPlan.Create(doc, vft.Id, l.Id).Id.Value;
                        }
                    });
                    return new { level_id = id, plan_view_id = viewId == 0 ? (long?)null : viewId, revit = notes, undo = "One undo step in Revit." };
                }
            };

            yield return new RevitTool
            {
                Name = "save_model",
                Write = true,
                Description = "Save the open project to its file (as Ctrl+S). Only a project that already has a file; never Save As. Use after a block of work so nothing is lost.",
                InputSchema = McpServer.Obj(),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    if (string.IsNullOrEmpty(doc.PathName)) throw new InvalidOperationException("The project has never been saved: ask the user to Save As first.");
                    if (doc.IsWorkshared) throw new InvalidOperationException("Workshared model: synchronise in Revit instead.");
                    doc.Save();
                    return new { saved = doc.PathName, at = DateTime.Now.ToString("HH:mm:ss") };
                }
            };

            // ---------------------------------------------------------------- façade details
            yield return new RevitTool
            {
                Name = "model_sweep",
                Write = true,
                Description = "Façade detail as a solid swept along a path: cornices, string courses, parapet copings, plinths, sills, hood mouldings, rustication bands. " +
                              "profile: closed outline [[u,v], ...] in mm across the path, u to the LEFT of the path's direction (horizontal), v up, measured from the path. " +
                              "path: [[x,y,z], ...] model mm (z absolute, use the level elevation + height). Made as a DirectShape (Generic Models unless category is given). One undo step.",
                InputSchema = McpServer.Obj(
                    ("profile", "array", "[[u,v], ...] mm, closed implicitly", true),
                    ("path", "array", "[[x,y,z], ...] model mm, 2 or more points", true),
                    ("closed", "boolean", "The path goes back to its first point", false),
                    ("name", "string", "What it is, e.g. Cornice", false),
                    ("category", "string", "Generic Models (default), Walls, Roofs, Floors, Columns...", false),
                    ("comments", "string", "", false), ("phase", "string", "", false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var prof = (a["profile"] as JsonArray)?.OfType<JsonArray>().Select(p => new { u = Num(p, 0), v = Num(p, 1) }).ToList() ?? throw new ArgumentException("profile is needed.");
                    var path = PointList(a["path"] as JsonArray, 0, true);
                    if (prof.Count < 3 || path.Count < 2) throw new ArgumentException("profile needs 3+ points and path 2+.");
                    if (Bool(a, "closed") == true) path.Add(path[0]);
                    long id = 0; int pieces = 0;
                    var notes = Tx.Run(doc, $"MCP: {Str(a, "name") ?? "façade detail"}", () =>
                    {
                        var solids = new List<GeometryObject>();
                        try
                        {
                            solids.Add(SweepWhole(path, prof.Select(p => (p.u, p.v)).ToList()));
                            pieces = 1;
                        }
                        catch
                        {
                            // A path with sharp corners can fail as one sweep: sweep each straight run, mitred at the corners.
                            foreach (var s in SweepPieces(path, prof.Select(p => (p.u, p.v)).ToList())) solids.Add(s);
                            pieces = solids.Count;
                        }
                        id = MakeShape(doc, solids, a).Id.Value;
                    });
                    return new { element_id = id, pieces, revit = notes, undo = "One undo step in Revit." };
                }
            };

            yield return new RevitTool
            {
                Name = "model_extrusion",
                Write = true,
                Description = "Façade detail as a flat outline pushed out: piers, pilasters, quoins, panels, window surrounds, chimney stacks, balcony slabs, blocked-up window infill. " +
                              "outline: [[x,y,z], ...] model mm, flat (all in one plane); depth_mm pushed along direction [dx,dy,dz] (default straight up). " +
                              "For a pier on an elevation: outline in the wall's face plane, direction out of the wall. DirectShape, Generic Models unless category is given. One undo step.",
                InputSchema = McpServer.Obj(
                    ("outline", "array", "[[x,y,z], ...] model mm, flat", true),
                    ("depth_mm", "number", "How far to push it", true),
                    ("direction", "number[]", "[dx,dy,dz], default [0,0,1]", false),
                    ("holes", "array", "Holes in the outline: [[[x,y,z], ...], ...]", false),
                    ("name", "string", "What it is, e.g. Pier", false),
                    ("category", "string", "Generic Models (default), Walls, Roofs, Floors, Columns...", false),
                    ("comments", "string", "", false), ("phase", "string", "", false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var outline = PointList(a["outline"] as JsonArray, 0, true);
                    if (outline.Count < 3) throw new ArgumentException("outline needs 3+ points.");
                    var dirN = Numbers(a, "direction");
                    var dir = dirN.Count == 3 ? new XYZ(dirN[0], dirN[1], dirN[2]) : XYZ.BasisZ;
                    if (dir.GetLength() < 1e-9) throw new ArgumentException("direction cannot be zero.");
                    double depth = (Dbl(a, "depth_mm") ?? 0) / MmPerFoot;
                    if (depth <= 0) throw new ArgumentException("depth_mm must be more than 0.");
                    long id = 0;
                    var notes = Tx.Run(doc, $"MCP: {Str(a, "name") ?? "façade detail"}", () =>
                    {
                        var loops = new List<CurveLoop> { Loop(outline) };
                        foreach (var h in (a["holes"] as JsonArray)?.OfType<JsonArray>() ?? Enumerable.Empty<JsonArray>())
                        {
                            var hp = PointList(h, 0, true);
                            if (hp.Count >= 3) loops.Add(Loop(hp));
                        }
                        var solid = GeometryCreationUtilities.CreateExtrusionGeometry(loops, dir.Normalize(), depth);
                        id = MakeShape(doc, new List<GeometryObject> { solid }, a).Id.Value;
                    });
                    return new { element_id = id, revit = notes, undo = "One undo step in Revit." };
                }
            };
        }

        // ================================================================ transactions without dialogs

        /// <summary>
        /// Runs a change as one undo step with no Revit dialogs. Warnings are dismissed and reported. Errors that Revit
        /// can resolve by unjoining or detaching are resolved that way. Errors whose only way out deletes elements roll
        /// the whole change back, and the error says which elements so it can be done another way.
        /// </summary>
        internal static class Tx
        {
            /// <summary>Also a failure preprocessor on its own, for a transaction run elsewhere (the DWG build): unjoin, never delete.</summary>
            internal static IFailuresPreprocessor Preprocessor(out Func<List<string>> notes, out Func<List<string>> blocking)
            {
                var q = new Quiet();
                notes = () => q.Notes;
                blocking = () => q.Blocking;
                return q;
            }

            public static List<string> Run(Document doc, string name, Action body)
            {
                var pre = new Quiet();
                using (var tx = new Transaction(doc, name))
                {
                    var opts = tx.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(pre);
                    opts.SetClearAfterRollback(true);
                    opts.SetForcedModalHandling(false);
                    tx.SetFailureHandlingOptions(opts);
                    tx.Start();
                    try { body(); }
                    catch
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        throw;
                    }
                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit refused the change, so nothing was changed. " +
                            (pre.Blocking.Count > 0 ? string.Join(" ", pre.Blocking) : "Status: " + status) +
                            " Try another way: stop the joins first (set_wall_joins), split the wall (split_wall), or move the elements named.");
                }
                return pre.Notes.Distinct().Take(20).ToList();
            }

            private sealed class Quiet : IFailuresPreprocessor
            {
                public readonly List<string> Notes = new List<string>();
                public readonly List<string> Blocking = new List<string>();

                public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
                {
                    bool resolved = false;
                    foreach (var m in fa.GetFailureMessages())
                    {
                        string text = m.GetDescriptionText();
                        string ids = string.Join(", ", m.GetFailingElementIds().Select(i => i.Value).Take(12));
                        if (m.GetSeverity() == FailureSeverity.Warning)
                        {
                            Notes.Add($"Warning dismissed: {text}" + (ids.Length > 0 ? $" ({ids})" : ""));
                            fa.DeleteWarning(m);
                            continue;
                        }
                        // Errors: unjoin/detach is safe (nothing is lost); deleting elements is not done silently.
                        if (m.HasResolutions() && m.HasResolutionOfType(FailureResolutionType.DetachElements))
                        {
                            m.SetCurrentResolutionType(FailureResolutionType.DetachElements);
                            fa.ResolveFailure(m);
                            Notes.Add($"Unjoined: {text}" + (ids.Length > 0 ? $" ({ids})" : ""));
                            resolved = true;
                            continue;
                        }
                        Blocking.Add($"\"{text}\"" + (ids.Length > 0 ? $" for elements {ids}." : "."));
                    }
                    if (Blocking.Count > 0) return FailureProcessingResult.ProceedWithRollBack;
                    return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
                }
            }
        }

        // ================================================================ windows and doors

        /// <summary>place_inserts for one item, inside a transaction the caller runs (survey_autofix).</summary>
        internal static object PlaceInsertHere(Document doc, JsonObject it) => PlaceInsert(doc, it);

        /// <summary>set_insert's reshape and slide, inside a transaction the caller runs (survey_autofix).</summary>
        internal static void ReshapeInsert(Document doc, FamilyInstance fi, double? widthMm, double? heightMm, double? sillMm, XYZ slideTo = null)
        {
            if (widthMm != null || heightMm != null)
            {
                var (cw, ch) = InsertSize(doc, fi);
                // families whose size the parameters do not give: head minus sill, and the hosted opening's width
                double? sillNow = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() * MmPerFoot;
                double? headNow = fi.get_Parameter(BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM)?.AsDouble() * MmPerFoot;
                if ((ch == null || ch <= 0) && sillNow != null && headNow != null && headNow > sillNow) ch = Math.Round(headNow.Value - sillNow.Value);
                if (cw == null || cw <= 0) cw = SymbolSize(fi.Symbol).w;
                double newW = widthMm ?? cw ?? 0, newH = heightMm ?? ch ?? 0;
                if (newW <= 0 || newH <= 0) throw new InvalidOperationException($"the window's current {(newW <= 0 ? "width" : "height")} cannot be read: give both width and height");
                var sym = SizedSymbol(doc, fi.Symbol, newW, newH);
                if (sym.Id != fi.Symbol.Id) fi.Symbol = sym;
                SetInstanceSize(fi, widthMm, heightMm);
            }
            if (sillMm is double sill) SetSill(fi, sill);
            if (slideTo != null && fi.Host is Wall host && fi.Location is LocationPoint lp)
            {
                var target = Project(((LocationCurve)host.Location).Curve, new XYZ(slideTo.X, slideTo.Y, lp.Point.Z));
                var move = new XYZ(target.X - lp.Point.X, target.Y - lp.Point.Y, 0);
                if (move.GetLength() > 1e-6) ElementTransformUtils.MoveElement(doc, fi.Id, move);
            }
        }

        internal static (double? w, double? h) InsertSizeOf(Document doc, FamilyInstance fi) => InsertSize(doc, fi);

        private static object PlaceInsert(Document doc, JsonObject it)
        {
            string catName = (Str(it, "category") ?? "Windows").ToLowerInvariant();
            var bic = catName.StartsWith("door") ? BuiltInCategory.OST_Doors : catName.StartsWith("window") ? BuiltInCategory.OST_Windows : throw new ArgumentException("category is Windows or Doors.");
            double x = (Dbl(it, "x") ?? throw new ArgumentException("x is needed.")) / MmPerFoot;
            double y = (Dbl(it, "y") ?? throw new ArgumentException("y is needed.")) / MmPerFoot;

            Wall wall;
            if (Long(it, "wall_id") is long wid) wall = doc.GetElement(new ElementId(wid)) as Wall ?? throw new ArgumentException($"{wid} is not a wall.");
            else
            {
                var level = FindLevel(doc, Str(it, "level") ?? throw new ArgumentException("Give wall_id, or level to find the nearest wall."));
                wall = NearestWall(doc, level, new XYZ(x, y, 0), 1500 / MmPerFoot) ?? throw new InvalidOperationException("No wall within 1.5 m of that point on " + level.Name + ".");
            }
            var hostLevel = doc.GetElement(wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId()) as Level;
            double baseZ = hostLevel.Elevation + wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble();
            var at = Project(((LocationCurve)wall.Location).Curve, new XYZ(x, y, baseZ));

            FamilySymbol sym = BaseSymbol(doc, bic, Str(it, "family"), Str(it, "type_name"));
            double? w = Dbl(it, "width_mm"), h = Dbl(it, "height_mm");
            if (Str(it, "type_name") == null && (w != null || h != null))
            {
                var (bw, bh) = SymbolSize(sym);
                sym = SizedSymbol(doc, sym, w ?? bw ?? 900, h ?? bh ?? 2100);
            }
            if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }

            double sill = Dbl(it, "sill_mm") ?? (bic == BuiltInCategory.OST_Doors ? 0 : 900);
            var fi = doc.Create.NewFamilyInstance(new XYZ(at.X, at.Y, baseZ), sym, wall, hostLevel, StructuralType.NonStructural);
            SetSill(fi, sill);
            SetInstanceSize(fi, w, h);
            doc.Regenerate(); // so the size read back below is the real one
            if (Bool(it, "flip_facing") == true && fi.CanFlipFacing) fi.flipFacing();
            if (Bool(it, "flip_hand") == true && fi.CanFlipHand) fi.flipHand();
            if (Str(it, "mark") is string mark) fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
            fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Str(it, "comments") ?? DefaultComment);
            var phaseId = Str(it, "phase") is string ph ? FindPhase(doc, ph) : wall.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId();
            if (phaseId != null && phaseId != ElementId.InvalidElementId) fi.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phaseId);
            var (fw, fh) = InsertSize(doc, fi);
            return new { id = fi.Id.Value, category = fi.Category.Name, type = $"{sym.FamilyName}: {sym.Name}", wall_id = wall.Id.Value, at = Pt(new XYZ(at.X, at.Y, baseZ)), sillMm = Math.Round(sill), widthMm = fw, heightMm = fh };
        }

        private static FamilySymbol BaseSymbol(Document doc, BuiltInCategory bic, string family, string typeName)
        {
            var symbols = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType().OfType<FamilySymbol>().ToList();
            if (symbols.Count == 0) throw new InvalidOperationException($"The model has no {(bic == BuiltInCategory.OST_Doors ? "door" : "window")} family loaded. Load one in Revit first.");
            if (typeName != null)
                return symbols.FirstOrDefault(s => $"{s.FamilyName}: {s.Name}".Equals(typeName, StringComparison.OrdinalIgnoreCase))
                    ?? symbols.FirstOrDefault(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No type called \"{typeName}\". See list_types.");
            if (family != null)
                return symbols.FirstOrDefault(s => s.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No family called \"{family}\". Families: {string.Join(", ", symbols.Select(s => s.FamilyName).Distinct())}.");
            var used = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().OfType<FamilyInstance>()
                .GroupBy(f => f.Symbol.Family.Id).OrderByDescending(g => g.Count()).FirstOrDefault();
            return used != null ? symbols.First(s => s.Family.Id == used.Key) : symbols[0];
        }

        private static readonly BuiltInParameter[] WidthParams = { BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM };
        private static readonly BuiltInParameter[] HeightParams = { BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM };

        private static Parameter SizeParam(Element e, BuiltInParameter[] bips, string name)
        {
            foreach (var b in bips) if (e.get_Parameter(b) is Parameter p && p.StorageType == StorageType.Double && !p.IsReadOnly) return p;
            var lp = e.LookupParameter(name);
            return lp != null && lp.StorageType == StorageType.Double && !lp.IsReadOnly ? lp : null;
        }

        private static (double? w, double? h) SymbolSize(FamilySymbol s) =>
            (SizeParam(s, WidthParams, "Width") is Parameter pw ? R(pw.AsDouble()) : (double?)null,
             SizeParam(s, HeightParams, "Height") is Parameter ph ? R(ph.AsDouble()) : (double?)null);

        private static (double? w, double? h) InsertSize(Document doc, FamilyInstance fi)
        {
            var w = SizeParam(fi, WidthParams, "Width") ?? SizeParam(fi.Symbol, WidthParams, "Width");
            var h = SizeParam(fi, HeightParams, "Height") ?? SizeParam(fi.Symbol, HeightParams, "Height");
            return (w != null ? R(w.AsDouble()) : (double?)null, h != null ? R(h.AsDouble()) : (double?)null);
        }

        /// <summary>A type of this family with this width and height: an existing one, or a copy of the base type named in the family's own pattern.</summary>
        private static FamilySymbol SizedSymbol(Document doc, FamilySymbol baseSym, double wMm, double hMm)
        {
            wMm = Math.Round(wMm); hMm = Math.Round(hMm);
            var family = baseSym.Family;
            foreach (var sid in family.GetFamilySymbolIds())
            {
                if (!(doc.GetElement(sid) is FamilySymbol s)) continue;
                var (sw, sh) = SymbolSize(s);
                if (sw != null && sh != null && Math.Abs(sw.Value - wMm) < 1 && Math.Abs(sh.Value - hMm) < 1) return s;
            }
            if (SizeParam(baseSym, WidthParams, "Width") == null && SizeParam(baseSym, HeightParams, "Height") == null) return baseSym; // sized per instance
            var m = Regex.Match(baseSym.Name, @"^(.*?)\s*\d+\s*x\s*\d+\s*$");
            string name = (m.Success ? m.Groups[1].Value.Trim() + " " : "") + $"{wMm:0}x{hMm:0}";
            var names = new HashSet<string>(family.GetFamilySymbolIds().Select(i => doc.GetElement(i).Name));
            string unique = name; int n = 2;
            while (names.Contains(unique)) unique = $"{name} ({n++})";
            var copy = (FamilySymbol)baseSym.Duplicate(unique);
            SizeParam(copy, WidthParams, "Width")?.Set(wMm / MmPerFoot);
            SizeParam(copy, HeightParams, "Height")?.Set(hMm / MmPerFoot);
            return copy;
        }

        private static void SetInstanceSize(FamilyInstance fi, double? w, double? h)
        {
            if (w != null && SizeParam(fi, WidthParams, "Width") is Parameter pw) pw.Set(w.Value / MmPerFoot);
            if (h != null && SizeParam(fi, HeightParams, "Height") is Parameter ph) ph.Set(h.Value / MmPerFoot);
        }

        private static void SetSill(FamilyInstance fi, double sillMm)
        {
            var p = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (p != null && !p.IsReadOnly) p.Set(sillMm / MmPerFoot);
        }

        private static Wall NearestWall(Document doc, Level level, XYZ p, double maxFeet)
        {
            Wall best = null; double bestD = maxFeet;
            foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() != level.Id) continue;
                if (!(w.Location is LocationCurve lc)) continue;
                var c = lc.Curve;
                double d = DistToSeg(p, Flat(c.GetEndPoint(0)), Flat(c.GetEndPoint(1)));
                if (d < bestD) { bestD = d; best = w; }
            }
            return best;
        }

        // ================================================================ walls

        private static object CreateWall(Document doc, JsonObject it)
        {
            var line = Numbers(it, "line");
            if (line.Count != 4) throw new ArgumentException("line is [x1, y1, x2, y2] in mm.");
            var level = FindLevel(doc, Str(it, "level"));
            var p = new XYZ(line[0] / MmPerFoot, line[1] / MmPerFoot, level.Elevation);
            var q = new XYZ(line[2] / MmPerFoot, line[3] / MmPerFoot, level.Elevation);
            if (p.DistanceTo(q) < 50 / MmPerFoot) throw new ArgumentException("A wall must be at least 50 mm long.");
            WallType wt = Str(it, "type_name") is string tn ? TypeByName<WallType>(doc, BuiltInCategory.OST_Walls, tn)
                : WallTypeOf(doc, Dbl(it, "thickness_mm") ?? throw new ArgumentException("Give thickness_mm or type_name."));

            double height = (Dbl(it, "height_mm") ?? 3000) / MmPerFoot;
            var wall = Wall.Create(doc, Line.CreateBound(p, q), wt.Id, level.Id, height, (Dbl(it, "base_offset_mm") ?? 0) / MmPerFoot, false, false);
            if (Str(it, "top_level") is string tl) wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(FindLevel(doc, tl).Id);
            else if (Dbl(it, "height_mm") == null)
            {
                var next = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Where(l => l.Elevation > level.Elevation + 0.01).OrderBy(l => l.Elevation).FirstOrDefault();
                if (next != null) wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(next.Id);
            }
            if (Dbl(it, "top_offset_mm") is double to) wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).Set(to / MmPerFoot);
            if (Bool(it, "joins") == false) { WallUtils.DisallowWallJoinAtEnd(wall, 0); WallUtils.DisallowWallJoinAtEnd(wall, 1); }
            Finish(doc, wall, it, null, null);
            return new { id = wall.Id.Value, type = wt.Name, thicknessMm = R(wall.Width) };
        }

        private static long SplitWall(Document doc, Wall wall, double xMm, double yMm, double gapMm)
        {
            if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Line line)) throw new InvalidOperationException("Only a straight wall can be split.");
            var p0 = line.GetEndPoint(0); var p1 = line.GetEndPoint(1);
            var dir = (p1 - p0).Normalize();
            double len = line.Length;
            double t = (new XYZ(xMm / MmPerFoot, yMm / MmPerFoot, p0.Z) - p0).DotProduct(dir);
            double gap = gapMm / MmPerFoot / 2;
            if (t - gap < 50 / MmPerFoot || t + gap > len - 50 / MmPerFoot) throw new ArgumentException("The split point must be inside the wall, at least 50 mm from each end.");

            // What sits in the wall, and on which side of the split.
            var inserts = Hosted(doc, wall);
            double Along(Element e)
            {
                if (e.Location is LocationPoint lp) return (lp.Point - p0).DotProduct(dir);
                if (e is Opening op && op.BoundaryRect?.Count == 2) return ((op.BoundaryRect[0] + op.BoundaryRect[1]) / 2 - p0).DotProduct(dir);
                var bb = e.get_BoundingBox(null);
                return bb == null ? 0 : ((bb.Min + bb.Max) / 2 - p0).DotProduct(dir);
            }
            var moving = inserts.Where(e => Along(e) > t).ToList();

            // The second part: a copy of the wall. Whatever Revit copied with it is removed; the inserts past the split are rebuilt on it.
            var copyId = ElementTransformUtils.CopyElement(doc, wall.Id, XYZ.Zero).First();
            var copy = (Wall)doc.GetElement(copyId);
            var copied = Hosted(doc, copy).Select(e => e.Id).ToList();
            if (copied.Count > 0) doc.Delete(copied);

            var rebuilt = new List<(Element old, Action<Wall> make)>();
            foreach (var e in moving)
            {
                if (e is FamilyInstance fi && fi.Location is LocationPoint lp)
                {
                    var sym = fi.Symbol; var lvl = doc.GetElement(fi.LevelId) as Level ?? doc.GetElement(wall.LevelId) as Level;
                    double sill = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0;
                    bool ff = fi.FacingFlipped, hf = fi.HandFlipped;
                    var (w, h) = InsertSize(doc, fi);
                    string mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                    string com = fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                    var phase = fi.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId();
                    var pt = lp.Point;
                    rebuilt.Add((e, host =>
                    {
                        var n = doc.Create.NewFamilyInstance(pt, sym, host, lvl, StructuralType.NonStructural);
                        n.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(sill);
                        SetInstanceSize(n, w, h);
                        if (n.FacingFlipped != ff && n.CanFlipFacing) n.flipFacing();
                        if (n.HandFlipped != hf && n.CanFlipHand) n.flipHand();
                        if (mark != null) n.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
                        if (com != null) n.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(com);
                        if (phase != null && phase != ElementId.InvalidElementId) n.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phase);
                    }));
                }
                else if (e is Opening op && op.IsRectBoundary && op.BoundaryRect?.Count == 2)
                {
                    var a0 = op.BoundaryRect[0]; var a1 = op.BoundaryRect[1];
                    rebuilt.Add((e, host => doc.Create.NewOpening(host, a0, a1)));
                }
            }
            if (moving.Count > 0) doc.Delete(moving.Select(e => e.Id).ToList());

            // Stop the new ends joining each other, then cut the lines.
            WallUtils.DisallowWallJoinAtEnd(wall, 1);
            WallUtils.DisallowWallJoinAtEnd(copy, 0);
            lc.Curve = Line.CreateBound(p0, p0 + dir * (t - gap));
            ((LocationCurve)copy.Location).Curve = Line.CreateBound(p0 + dir * (t + gap), p1);
            doc.Regenerate();
            foreach (var (_, make) in rebuilt) make(copy);
            WallUtils.AllowWallJoinAtEnd(wall, 1);
            WallUtils.AllowWallJoinAtEnd(copy, 0);
            return copyId.Value;
        }

        /// <summary>
        /// Everything hosted in a wall: what FindInserts gives (inserts that cut, openings) and family instances whose
        /// host is the wall but that do not cut it themselves (e.g. windows placed with a separate wall opening).
        /// </summary>
        internal static List<Element> Hosted(Document doc, Wall wall)
        {
            var ids = new HashSet<ElementId>(wall.FindInserts(true, false, false, false));
            foreach (var fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                if (fi.Host != null && fi.Host.Id == wall.Id) ids.Add(fi.Id);
            return ids.Select(id => doc.GetElement(id)).Where(e => e != null).ToList();
        }

        /// <summary>
        /// Changes a wall's line with its rectangular openings lifted off and put back at the same distance along the
        /// wall and the same heights. Without this, moving the line can turn an opening's sketch inside out and Revit
        /// refuses ("Element is reversed").
        /// </summary>
        internal static void SetLineKeepingOpenings(Document doc, Wall wall, Line newLine)
        {
            var lc = (LocationCurve)wall.Location;
            var old = lc.Curve;
            var p0 = old.GetEndPoint(0);
            var dir = (old.GetEndPoint(1) - p0).Normalize();
            var kept = new List<(double a0, double a1, double z0, double z1)>();
            var openings = Hosted(doc, wall).OfType<Opening>().Where(o => o.IsRectBoundary && o.BoundaryRect?.Count == 2).ToList();
            foreach (var o in openings)
            {
                var b0 = o.BoundaryRect[0]; var b1 = o.BoundaryRect[1];
                kept.Add(((b0 - p0).DotProduct(dir), (b1 - p0).DotProduct(dir), b0.Z, b1.Z));
            }
            if (openings.Count > 0) doc.Delete(openings.Select(o => o.Id).ToList());
            lc.Curve = newLine;
            doc.Regenerate();
            var n0 = newLine.GetEndPoint(0);
            var nd = (newLine.GetEndPoint(1) - n0).Normalize();
            foreach (var (a0, a1, z0, z1) in kept)
            {
                var q0 = n0 + nd * a0; var q1 = n0 + nd * a1;
                doc.Create.NewOpening(wall, new XYZ(q0.X, q0.Y, z0), new XYZ(q1.X, q1.Y, z1));
            }
        }

        internal static WallType WallTypeOf(Document doc, double mm)
        {
            mm = Math.Round(mm);
            var basics = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(w => w.Kind == WallKind.Basic).ToList();
            var hit = basics.FirstOrDefault(w => w.Name.StartsWith("Concept Wall", StringComparison.OrdinalIgnoreCase) && Math.Abs(w.Width * MmPerFoot - mm) < 0.5)
                   ?? basics.FirstOrDefault(w => Math.Abs(w.Width * MmPerFoot - mm) < 0.5 && w.GetCompoundStructure()?.LayerCount == 1);
            if (hit != null) return hit;
            var seed = basics.FirstOrDefault(w => w.Name.StartsWith("Concept Wall", StringComparison.OrdinalIgnoreCase)) ?? basics.FirstOrDefault(w => w.GetCompoundStructure()?.LayerCount == 1) ?? basics.First();
            var copy = (WallType)seed.Duplicate(UniqueTypeName(doc, typeof(WallType), $"Concept Wall {mm:0}mm"));
            SetThickness(copy.GetCompoundStructure(), mm, cs => copy.SetCompoundStructure(cs));
            return copy;
        }

        private static FloorType FloorTypeOf(Document doc, double mm)
        {
            mm = Math.Round(mm);
            var all = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(f => f.GetCompoundStructure() != null).ToList();
            var hit = all.FirstOrDefault(f => f.Name.StartsWith("Concept", StringComparison.OrdinalIgnoreCase) && Math.Abs(f.GetCompoundStructure().GetWidth() * MmPerFoot - mm) < 0.5)
                   ?? all.FirstOrDefault(f => Math.Abs(f.GetCompoundStructure().GetWidth() * MmPerFoot - mm) < 0.5);
            if (hit != null) return hit;
            var seed = all.FirstOrDefault(f => f.Name.StartsWith("Concept", StringComparison.OrdinalIgnoreCase)) ?? doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.FloorType)) as FloorType ?? all.First();
            var copy = (FloorType)seed.Duplicate(UniqueTypeName(doc, typeof(FloorType), $"Concept Floor {mm:0}mm"));
            SetThickness(copy.GetCompoundStructure(), mm, cs => copy.SetCompoundStructure(cs));
            return copy;
        }

        private static RoofType RoofTypeOf(Document doc, double mm)
        {
            mm = Math.Round(mm);
            var all = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().Where(f => f.GetCompoundStructure() != null).ToList();
            var hit = all.FirstOrDefault(f => Math.Abs(f.GetCompoundStructure().GetWidth() * MmPerFoot - mm) < 0.5);
            if (hit != null) return hit;
            var seed = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.RoofType)) as RoofType ?? all.FirstOrDefault() ?? throw new InvalidOperationException("The model has no roof type.");
            var copy = (RoofType)seed.Duplicate(UniqueTypeName(doc, typeof(RoofType), $"Concept Roof {mm:0}mm"));
            SetThickness(copy.GetCompoundStructure(), mm, cs => copy.SetCompoundStructure(cs));
            return copy;
        }

        /// <summary>Makes a compound structure this thick by changing its thickest layer.</summary>
        private static void SetThickness(CompoundStructure cs, double mm, Action<CompoundStructure> save)
        {
            if (cs == null) return;
            var layers = cs.GetLayers();
            int thickest = 0;
            for (int i = 1; i < layers.Count; i++) if (layers[i].Width > layers[thickest].Width) thickest = i;
            double others = layers.Where((l, i) => i != thickest).Sum(l => l.Width);
            double want = mm / MmPerFoot - others;
            if (want <= 0.001) { // too thin for the other layers: one layer only
                cs.SetLayers(new List<CompoundStructureLayer> { new CompoundStructureLayer(mm / MmPerFoot, MaterialFunctionAssignment.Structure, layers[thickest].MaterialId) });
                cs.SetNumberOfShellLayers(ShellLayerType.Exterior, 0);
                cs.SetNumberOfShellLayers(ShellLayerType.Interior, 0);
                cs.StructuralMaterialIndex = 0;
            }
            else cs.SetLayerWidth(thickest, want);
            save(cs);
        }

        private static string UniqueTypeName(Document doc, Type cls, string name)
        {
            var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(cls).Select(e => e.Name));
            string n = name; int k = 2;
            while (names.Contains(n)) n = $"{name} ({k++})";
            return n;
        }

        private static T TypeByName<T>(Document doc, BuiltInCategory bic, string name) where T : ElementType
        {
            var types = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType().OfType<T>().ToList();
            return types.FirstOrDefault(t => $"{t.FamilyName}: {t.Name}".Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? types.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No type called \"{name}\". See list_types.");
        }

        // ================================================================ façade solids

        /// <summary>model_sweep inside a transaction the caller runs: path in feet, profile in mm (u left of the path, v up). The DirectShape's id.</summary>
        internal static long SweepShape(Document doc, List<XYZ> path, List<(double u, double v)> prof, string name, string comments)
        {
            var solids = new List<GeometryObject>();
            try { solids.Add(SweepWhole(path, prof)); }
            catch { foreach (var s in SweepPieces(path, prof)) solids.Add(s); }
            return MakeShape(doc, solids, new JsonObject { ["name"] = name, ["comments"] = comments }).Id.Value;
        }

        private static Solid SweepWhole(List<XYZ> path, List<(double u, double v)> prof)
        {
            var pathLoop = new CurveLoop();
            for (int i = 0; i + 1 < path.Count; i++) pathLoop.Append(Line.CreateBound(path[i], path[i + 1]));
            var profile = ProfileAt(path[0], path[1] - path[0], prof);
            return GeometryCreationUtilities.CreateSweptGeometry(pathLoop, 0, 0, new List<CurveLoop> { profile });
        }

        /// <summary>Each straight run swept on its own, its ends cut on the mitre with the next run, so corners meet cleanly.</summary>
        private static IEnumerable<Solid> SweepPieces(List<XYZ> path, List<(double u, double v)> prof)
        {
            bool closed = path.Count > 3 && path[0].IsAlmostEqualTo(path[path.Count - 1]);
            double reach = prof.Max(p => Math.Max(Math.Abs(p.u), Math.Abs(p.v))) / MmPerFoot * 3 + 1;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                var a = path[i]; var b = path[i + 1];
                var d = (b - a).Normalize();
                // extend both ends, then cut with the mitre planes
                var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { ProfileAt(a - d * reach, d, prof) }, d, a.DistanceTo(b) + 2 * reach);
                XYZ prevDir = i > 0 ? (a - path[i - 1]).Normalize() : closed ? (path[0] - path[path.Count - 2]).Normalize() : null;
                XYZ nextDir = i + 2 < path.Count ? (path[i + 2] - b).Normalize() : closed ? (path[1] - path[0]).Normalize() : null;
                var startN = prevDir != null ? (prevDir + d).Normalize() : d;
                var endN = nextDir != null ? (d + nextDir).Normalize() : d;
                solid = BooleanOperationsUtils.CutWithHalfSpace(solid, Plane.CreateByNormalAndOrigin(startN, a)) ?? solid;
                solid = BooleanOperationsUtils.CutWithHalfSpace(solid, Plane.CreateByNormalAndOrigin(-endN, b)) ?? solid;
                yield return solid;
            }
        }

        private static CurveLoop ProfileAt(XYZ origin, XYZ along, List<(double u, double v)> prof)
        {
            var d = along.Normalize();
            XYZ up, left;
            if (Math.Abs(d.Z) > 0.99) { left = XYZ.BasisX.Negate(); up = d.CrossProduct(left).Normalize(); left = up.CrossProduct(d).Normalize(); }
            else { left = XYZ.BasisZ.CrossProduct(d).Normalize(); up = d.CrossProduct(left).Normalize(); }
            var pts = prof.Select(p => origin + left * (p.u / MmPerFoot) + up * (p.v / MmPerFoot)).ToList();
            return Loop(pts);
        }

        private static CurveLoop Loop(List<XYZ> pts)
        {
            var clean = new List<XYZ>();
            foreach (var p in pts) if (clean.Count == 0 || !clean[clean.Count - 1].IsAlmostEqualTo(p)) clean.Add(p);
            if (clean.Count > 1 && clean[0].IsAlmostEqualTo(clean[clean.Count - 1])) clean.RemoveAt(clean.Count - 1);
            if (clean.Count < 3) throw new ArgumentException("An outline needs 3 different points.");
            var loop = new CurveLoop();
            for (int i = 0; i < clean.Count; i++) loop.Append(Line.CreateBound(clean[i], clean[(i + 1) % clean.Count]));
            return loop;
        }

        private static DirectShape MakeShape(Document doc, List<GeometryObject> geometry, JsonObject a)
        {
            var cat = Str(a, "category") is string cn ? CategoryId(doc, cn) : new ElementId(BuiltInCategory.OST_GenericModel);
            if (!DirectShape.IsValidCategoryId(cat, doc)) throw new ArgumentException($"A shape cannot be made in category \"{Str(a, "category")}\". Use Generic Models, Walls, Floors, Roofs, Columns...");
            var ds = DirectShape.CreateElement(doc, cat);
            ds.ApplicationId = "BarrGazetas.MCP";
            ds.ApplicationDataId = Str(a, "name") ?? "Detail";
            ds.SetShape(geometry);
            try { ds.SetName(Str(a, "name") ?? "Façade detail"); } catch { }
            Finish(doc, ds, a, null, null);
            return ds;
        }

        private static ElementId CategoryId(Document doc, string name)
        {
            if (Enum.TryParse(name, true, out BuiltInCategory bic)) return new ElementId(bic);
            foreach (Category c in doc.Settings.Categories)
                if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Name.Equals(name + "s", StringComparison.OrdinalIgnoreCase)) return c.Id;
            throw new ArgumentException($"No category called \"{name}\".");
        }

        // ================================================================ CAD

        private static ImportInstance FindImport(Document doc, JsonObject a)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
            if (Long(a, "import_id") is long id) return all.FirstOrDefault(i => i.Id.Value == id) ?? throw new ArgumentException($"{id} is not a CAD drawing in the model. See list_cad.");
            if (Str(a, "level") is string ln)
            {
                var level = FindLevel(doc, ln);
                var hit = all.FirstOrDefault(i => i.ViewSpecific && doc.GetElement(i.OwnerViewId) is ViewPlan vp && vp.GenLevel?.Id == level.Id)
                       ?? all.FirstOrDefault(i => !i.ViewSpecific && (doc.GetElement(i.LevelId) as Level)?.Id == level.Id);
                return hit ?? throw new InvalidOperationException($"No CAD drawing is linked into a plan of {level.Name}. See list_cad.");
            }
            throw new ArgumentException("Give import_id or level.");
        }

        private static string ImportName(Document doc, ImportInstance i)
        {
            try { return (doc.GetElement(i.GetTypeId()) as CADLinkType)?.Name ?? i.Category?.Name; } catch { return i.Category?.Name; }
        }

        private static void CollectCad(Document doc, GeometryElement ge, List<(string, XYZ, XYZ, string)> segs, int depth)
        {
            if (ge == null || depth > 6) return;
            foreach (var go in ge)
            {
                string layer = (doc.GetElement(go.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name;
                switch (go)
                {
                    case GeometryInstance gi: CollectCad(doc, gi.GetInstanceGeometry(), segs, depth + 1); break;
                    case Line l when l.IsBound: segs.Add((layer, l.GetEndPoint(0), l.GetEndPoint(1), "line")); break;
                    case PolyLine pl:
                        var c = pl.GetCoordinates();
                        for (int i = 0; i + 1 < c.Count; i++) segs.Add((layer, c[i], c[i + 1], "polyline"));
                        break;
                    case Arc arc when arc.IsBound:
                        var t = arc.Tessellate();
                        for (int i = 0; i + 1 < t.Count; i++) segs.Add((layer, t[i], t[i + 1], "arc"));
                        break;
                    case Curve cv when cv.IsBound:
                        var tc = cv.Tessellate();
                        for (int i = 0; i + 1 < tc.Count; i++) segs.Add((layer, tc[i], tc[i + 1], "curve"));
                        break;
                }
            }
        }

        // ================================================================ helpers

        private static void Finish(Document doc, Element e, JsonObject a, string fallbackComment, Element phaseFrom)
        {
            e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Str(a, "comments") ?? fallbackComment ?? DefaultComment);
            ElementId phase = Str(a, "phase") is string ph ? FindPhase(doc, ph)
                : phaseFrom?.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId() ?? FindPhase(doc, "Existing", false);
            var pp = e.get_Parameter(BuiltInParameter.PHASE_CREATED);
            if (phase != null && phase != ElementId.InvalidElementId && pp != null && !pp.IsReadOnly) pp.Set(phase);
        }

        private static ElementId FindPhase(Document doc, string name, bool required = true)
        {
            foreach (Phase p in doc.Phases) if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Id;
            if (required) throw new ArgumentException($"No phase called \"{name}\".");
            return null;
        }

        private static List<object> JoinedAt(LocationCurve lc, int end, ElementId self)
        {
            var list = new List<object>();
            try { foreach (Element e in lc.get_ElementsAtJoin(end)) if (e.Id != self) list.Add(e.Id.Value); } catch { }
            return list;
        }

        private static XYZ Project(Curve c, XYZ p)
        {
            var r = c.Project(new XYZ(p.X, p.Y, c.GetEndPoint(0).Z));
            return r == null ? p : new XYZ(r.XYZPoint.X, r.XYZPoint.Y, p.Z);
        }

        private static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);

        private static double DistToSeg(XYZ p, XYZ a, XYZ b)
        {
            var ab = b - a; double l2 = ab.DotProduct(ab);
            double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (p - a).DotProduct(ab) / l2));
            return p.DistanceTo(a + ab * t);
        }

        private static bool InBox(XYZ p, List<double> b)
        {
            double x = p.X * MmPerFoot, y = p.Y * MmPerFoot;
            return x >= b[0] && x <= b[2] && y >= b[1] && y <= b[3];
        }

        private static double R(double feet) => Math.Round(feet * MmPerFoot, 1);
        private static double[] Pt(XYZ p) => new[] { R(p.X), R(p.Y), R(p.Z) };
        private static double[][] Line2(Wall w) => w?.Location is LocationCurve lc ? new[] { Pt(lc.Curve.GetEndPoint(0)), Pt(lc.Curve.GetEndPoint(1)) } : null;

        private static Wall GetWall(Document doc, JsonObject a, string key) =>
            doc.GetElement(new ElementId(Long(a, key) ?? 0)) as Wall ?? throw new ArgumentException($"{key} is not a wall.");

        private static List<CurveLoop> Loops(JsonObject a, string key, double z)
        {
            var loops = new List<CurveLoop>();
            foreach (var l in (a[key] as JsonArray)?.OfType<JsonArray>() ?? Enumerable.Empty<JsonArray>())
            {
                var pts = PointList(l, z);
                if (pts.Count >= 3) loops.Add(Loop(pts));
            }
            return loops;
        }

        /// <summary>[[x,y], ...] or [[x,y,z], ...] in mm to feet. Without z the given z (feet) is used.</summary>
        private static List<XYZ> PointList(JsonArray arr, double zFeet, bool needZ = false)
        {
            var list = new List<XYZ>();
            foreach (var p in arr?.OfType<JsonArray>() ?? Enumerable.Empty<JsonArray>())
            {
                if (p.Count < 2) continue;
                double z = p.Count >= 3 ? Num(p, 2) / MmPerFoot : zFeet;
                if (needZ && p.Count < 3) throw new ArgumentException("Points here are [x, y, z] in model mm.");
                list.Add(new XYZ(Num(p, 0) / MmPerFoot, Num(p, 1) / MmPerFoot, z));
            }
            return list;
        }

        private static double Num(JsonArray p, int i) =>
            i < p.Count && p[i] is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : 0;

        private static List<JsonObject> Objects(JsonObject a, string key) => (a[key] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();

        private static Document Doc(UIApplication app) =>
            Reference.Current ?? app?.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No Revit project is open. Ask the user to open one.");

        private static Document Writable(UIApplication app)
        {
            var doc = Doc(app);
            if (doc.IsReadOnly) throw new InvalidOperationException("This project is read-only.");
            if (doc.IsModifiable) throw new InvalidOperationException("Another command is still changing the model. Ask the user to finish it in Revit, then try again.");
            return doc;
        }

        private static Level FindLevel(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A level name is needed.");
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            return levels.FirstOrDefault(l => l.Name == name)
                ?? levels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"There is no level called \"{name}\". Levels: {string.Join(", ", levels.Select(l => l.Name))}.");
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

        private static string Str(JsonObject a, string key) => a[key] is JsonValue v ? v.ToString() : null;
        private static bool? Bool(JsonObject a, string key) => a[key] is JsonValue v && bool.TryParse(v.ToString(), out bool b) ? b : (bool?)null;
        private static long? Long(JsonObject a, string key) =>
            a[key] is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? (long)d : (long?)null;
        private static double? Dbl(JsonObject a, string key) =>
            a[key] is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : (double?)null;
        private static int? Int(JsonObject a, string key) => Long(a, key) is long l ? (int)Math.Max(0, Math.Min(int.MaxValue, l)) : (int?)null;
        private static List<double> Numbers(JsonObject a, string key) =>
            (a[key] as JsonArray)?.OfType<JsonValue>().Select(v => double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : double.NaN)
                .Where(d => !double.IsNaN(d)).ToList() ?? new List<double>();
    }
}
