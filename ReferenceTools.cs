using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// Reference models: other .rvt files opened in the background of this Revit (not shown, never saved) so the
    /// read tools can look at them. Every read tool takes "document": the name a reference was opened under.
    /// </summary>
    internal static class Reference
    {
        private static readonly Dictionary<string, Document> Opened = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);
        [ThreadStatic] private static Document _current;

        /// <summary>The reference a read tool was pointed at for this call, or null for the active project.</summary>
        public static Document Current => _current != null && _current.IsValidObject ? _current : null;

        public static IDisposable Use(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Equals("active", StringComparison.OrdinalIgnoreCase)) return new Scope(null);
            return new Scope(Get(name));
        }

        public static Document Get(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("document is needed: the name given to reference_open.");
            if (Opened.TryGetValue(name, out var d) && d.IsValidObject) return d;
            Opened.Remove(name ?? "");
            throw new InvalidOperationException($"No reference model is open as \"{name}\". Open one with reference_open." +
                (Opened.Count > 0 ? " Open: " + string.Join(", ", Opened.Keys) + "." : ""));
        }

        public static IReadOnlyDictionary<string, Document> All
        {
            get { foreach (var k in Opened.Where(p => !p.Value.IsValidObject).Select(p => p.Key).ToList()) Opened.Remove(k); return Opened; }
        }

        public static void Add(string name, Document d) => Opened[name] = d;
        public static void Remove(string name) => Opened.Remove(name);

        private sealed class Scope : IDisposable
        {
            private readonly Document _before;
            public Scope(Document d) { _before = _current; _current = d; }
            public void Dispose() => _current = _before;
        }
    }

    internal static class ReferenceTools
    {
        private const double Mm = 304.8;

        public static IEnumerable<RevitTool> All()
        {
            yield return new RevitTool
            {
                Name = "reference_open",
                Description = "Open another .rvt (e.g. a colleague's model of the same building) in the background of this Revit, read only: not shown, never saved, " +
                              "detached from its central model so the central is not touched. Dialogs are answered automatically. Then every read tool " +
                              "(query_elements, get_elements, list_types, family_detail, element_geometry, ...) takes document: <name> to look at it, and " +
                              "reference_compare / reference_copy work across the two. A big model takes a minute or two (comes back as a job).",
                InputSchema = McpServer.Obj(
                    ("path", "string", "Full path of the .rvt", false),
                    ("title", "string", "Or the title of a model already open in this Revit, to use it as a reference", false),
                    ("name", "string", "Short name to call it by (default ref)", false),
                    ("worksets", "string", "all (default) or none: none opens faster but leaves user worksets closed", false)),
                Run = (app, a) =>
                {
                    string path = Args.Str(a, "path"), title = Args.Str(a, "title");
                    string name = Args.Str(a, "name") ?? "ref";
                    if (Reference.All.TryGetValue(name, out var already)) return new { name, alreadyOpen = true, title = already.Title };
                    // a model already open in this Revit (in a window, or from before a reload) is used as it is
                    string stem = path != null ? Path.GetFileNameWithoutExtension(path) : title;
                    if (stem == null) throw new ArgumentException("path is needed (or title, for a model already open in this Revit).");
                    foreach (Document d in app.Application.Documents)
                        if (!d.IsFamilyDocument && !d.IsLinked && (string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase) || d.Title.StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
                        {
                            Reference.Add(name, d);
                            return new { name, adopted = true, title = d.Title, active = app.ActiveUIDocument?.Document?.Equals(d) == true, location = Location(d) };
                        }
                    if (path == null || !File.Exists(path)) throw new FileNotFoundException("There is no file at " + path + ", and no open model called " + stem + ".");

                    var info = BasicFileInfo.Extract(path);
                    var opts = new OpenOptions { Audit = false, AllowOpeningLocalByWrongUser = true };
                    if (info.IsWorkshared)
                    {
                        opts.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                        opts.SetOpenWorksetsConfiguration(new WorksetConfiguration(
                            Args.Str(a, "worksets") == "none" ? WorksetConfigurationOption.CloseAllWorksets : WorksetConfigurationOption.OpenAllWorksets));
                    }
                    var notes = new List<string>();
                    EventHandler<DialogBoxShowingEventArgs> dialogs = (s, e) => Answer(e, notes);
                    EventHandler<FailuresProcessingEventArgs> failures = (s, e) => Quiet(e, notes);
                    app.DialogBoxShowing += dialogs;
                    app.Application.FailuresProcessing += failures;
                    Document doc;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try { doc = app.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), opts); }
                    finally { app.DialogBoxShowing -= dialogs; app.Application.FailuresProcessing -= failures; }
                    Reference.Add(name, doc);
                    return new
                    {
                        name, title = doc.Title, seconds = Math.Round(watch.Elapsed.TotalSeconds),
                        wasWorkshared = info.IsWorkshared, savedIn = info.Format, detached = info.IsWorkshared,
                        location = Location(doc),
                        levels = Levels(doc).Select(l => new { l.Name, elevationMm = Math.Round(l.Elevation * Mm) }).ToList(),
                        answered = notes.Distinct().Take(15).ToList(),
                        next = $"reference_inventory document={name}; reference_compare to line it up with the active project.",
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "reference_close",
                Description = "Close a reference model opened with reference_open, without saving.",
                InputSchema = McpServer.Obj(("name", "string", "Its name (default ref)", false)),
                Run = (app, a) =>
                {
                    string name = Args.Str(a, "name") ?? "ref";
                    var doc = Reference.Get(name);
                    Reference.Remove(name);
                    doc.Close(false);
                    return new { closed = name };
                }
            };

            yield return new RevitTool
            {
                Name = "reference_inventory",
                Description = "What a model is made of, for learning how it was built: categories, families (loadable or in-place) with their types " +
                              "and how many of each are placed, DirectShapes, groups, links and imports, levels and the survey position. " +
                              "Filter by category to see one in full. Works on the active project too (leave document out).",
                InputSchema = McpServer.Obj(
                    ("category", "string", "Only this category (Windows, Columns, Roofs, Generic Models...)", false),
                    ("limit", "integer", "Most families to list (default 60)", false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    Category only = Args.Str(a, "category") is string cn ? ResolveCategory(doc, cn) : null;
                    int limit = Args.Int(a, "limit", 60);
                    var instances = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                        .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && (only == null || e.Category.Id == only.Id)).ToList();
                    var byFamily = instances.OfType<FamilyInstance>().Where(fi => fi.Symbol != null)
                        .GroupBy(fi => fi.Symbol.Family.Id.Value).ToDictionary(g => g.Key, g => g.ToList());
                    var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                        .Where(f => only == null || f.FamilyCategory?.Id == only.Id)
                        .Select(f =>
                        {
                            byFamily.TryGetValue(f.Id.Value, out var placed);
                            placed ??= new List<FamilyInstance>();
                            var types = f.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol).Where(s => s != null)
                                .Select(s => new { s.Name, placed = placed.Count(p => p.Symbol.Id == s.Id) }).OrderByDescending(t => t.placed).ToList();
                            return new
                            {
                                id = f.Id.Value, f.Name, category = f.FamilyCategory?.Name, inPlace = f.IsInPlace,
                                nested = placed.Count(p => p.SuperComponent != null), placed = placed.Count(p => p.SuperComponent == null),
                                types = types.Count, typeList = types.Take(only != null ? 40 : 6).ToList(),
                            };
                        })
                        .Where(f => f.placed + f.nested > 0 || only != null)
                        .OrderByDescending(f => f.placed + f.nested).ToList();
                    var system = instances.Where(e => !(e is FamilyInstance)).GroupBy(e => e.Category.Name)
                        .Select(g => new
                        {
                            category = g.Key, count = g.Count(),
                            types = g.GroupBy(e => doc.GetElement(e.GetTypeId())?.Name ?? e.GetType().Name).OrderByDescending(t => t.Count())
                                .Take(only != null ? 40 : 8).ToDictionary(t => t.Key, t => t.Count()),
                        }).OrderByDescending(g => g.count).ToList();
                    return new
                    {
                        title = doc.Title,
                        location = Location(doc),
                        levels = only != null ? null : Levels(doc).Select(l => new { l.Name, elevationMm = Math.Round(l.Elevation * Mm) }).ToList(),
                        loadableFamilies = families.Where(f => !f.inPlace).Take(limit).ToList(),
                        inPlaceFamilies = families.Where(f => f.inPlace).Take(limit).ToList(),
                        systemElements = system,
                        directShapes = instances.OfType<DirectShape>().GroupBy(d => d.Category.Name).ToDictionary(g => g.Key, g => g.Count()),
                        modelGroups = only != null ? null : new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>()
                            .GroupBy(g => g.GroupType?.Name ?? "?").OrderByDescending(g => g.Count()).Take(20).ToDictionary(g => g.Key, g => g.Count()),
                        links = only != null ? null : new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Select(l => l.Name).Distinct().Take(20).ToList(),
                        imports = only != null ? null : new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                            .Select(i => doc.GetElement(i.GetTypeId())?.Name ?? i.Name).GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count()),
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "family_detail",
                Description = "How a family is built: for a loadable family, opens it (in memory, not saved) and lists its parameters with formulas and " +
                              "each type's values, its solids and voids (extrusion, sweep, blend, revolve) with sizes and subcategories, nested families " +
                              "and profiles; for an in-place family or any other element, the solids it is made of (sizes, volumes, materials). " +
                              "Give family (name) or element_id.",
                InputSchema = McpServer.Obj(
                    ("family", "string", "Family name (exact or the start of it)", false),
                    ("element_id", "integer", "Or an element: its family is shown", false),
                    ("types", "integer", "Most types to list values for (default 12)", false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    Element el = Args.Long(a, "element_id") is long eid ? doc.GetElement(new ElementId(eid)) ?? throw new InvalidOperationException($"There is no element {eid}.") : null;
                    Family fam = (el as FamilyInstance)?.Symbol?.Family;
                    if (fam == null && Args.Str(a, "family") is string fname)
                    {
                        var fams = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
                        fam = fams.FirstOrDefault(f => f.Name.Equals(fname, StringComparison.OrdinalIgnoreCase))
                              ?? fams.FirstOrDefault(f => f.Name.StartsWith(fname, StringComparison.OrdinalIgnoreCase))
                              ?? fams.FirstOrDefault(f => f.Name.IndexOf(fname, StringComparison.OrdinalIgnoreCase) >= 0)
                              ?? throw new InvalidOperationException($"No family called \"{fname}\".");
                    }
                    if (fam == null && el == null) throw new ArgumentException("Give family or element_id.");
                    if (fam == null || fam.IsInPlace || !fam.IsEditable)
                    {
                        el ??= new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().FirstOrDefault(fi => fi.Symbol.Family.Id == fam.Id)
                               ?? throw new InvalidOperationException("That in-place family has no instance to look at.");
                        return new { family = fam?.Name, inPlace = fam?.IsInPlace, category = el.Category?.Name, element = el.Id.Value, solids = SolidsReport(doc, el, 25) };
                    }
                    var fdoc = doc.EditFamily(fam);
                    try
                    {
                        var fm = fdoc.FamilyManager;
                        var owner = fdoc.OwnerFamily;
                        var parameters = fm.Parameters.Cast<FamilyParameter>().OrderBy(p => p.Definition.Name)
                            .Select(p => new { name = p.Definition.Name, instance = p.IsInstance, formula = string.IsNullOrEmpty(p.Formula) ? null : p.Formula, kind = p.StorageType.ToString() }).ToList();
                        int nTypes = Args.Int(a, "types", 12);
                        var dims = fm.Parameters.Cast<FamilyParameter>().Where(p => p.StorageType == StorageType.Double || p.StorageType == StorageType.Integer).ToList();
                        var types = fm.Types.Cast<FamilyType>().Take(nTypes).Select(t => new
                        {
                            t.Name,
                            values = dims.Where(p => t.HasValue(p)).ToDictionary(p => p.Definition.Name, p => { try { return t.AsValueString(p); } catch { return null; } }),
                        }).ToList();
                        var forms = new FilteredElementCollector(fdoc).OfClass(typeof(GenericForm)).Cast<GenericForm>().Select(g => FormInfo(fdoc, g)).ToList();
                        var nested = new FilteredElementCollector(fdoc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                            .GroupBy(fi => fi.Symbol.FamilyName + " : " + fi.Symbol.Name).ToDictionary(g => g.Key, g => g.Count());
                        var symbolic = new FilteredElementCollector(fdoc).WhereElementIsNotElementType().Where(e => e is CurveElement).Count();
                        return new
                        {
                            family = fam.Name, category = owner.FamilyCategory?.Name,
                            hosting = owner.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR)?.AsValueString(),
                            workPlaneBased = owner.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED)?.AsInteger() == 1,
                            typesTotal = fm.Types.Size, types,
                            parameters,
                            solids = forms.Count(f => f.solid), voids = forms.Count(f => !f.solid), forms = forms.Take(60).ToList(),
                            nested, linesAndSketches = symbolic,
                            referencePlanes = new FilteredElementCollector(fdoc).OfClass(typeof(ReferencePlane)).GetElementCount(),
                        };
                    }
                    finally { fdoc.Close(false); }
                }
            };

            yield return new RevitTool
            {
                Name = "element_geometry",
                Description = "An element's actual 3D geometry: its solids (sizes, volumes, materials) and, if a cut plane is given, the outline where the " +
                              "plane cuts it, as loops in the plane (mm). A vertical cut gives (along, height) pairs: a cornice's or a column's profile; a " +
                              "horizontal cut (normal 0,0,1) gives (x, y) in plan. Positions are the model's internal mm.",
                InputSchema = McpServer.Obj(
                    ("element_id", "integer", "The element", true),
                    ("origin", "number[]", "A point on the cut plane [x, y, z] mm", false),
                    ("normal", "number[]", "The plane's normal [x, y, z]; [0,0,1] for a plan cut", false),
                    ("max_points", "integer", "Most points per loop (default 200)", false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    long id = Args.Long(a, "element_id") ?? throw new ArgumentException("element_id is needed.");
                    var el = doc.GetElement(new ElementId(id)) ?? throw new InvalidOperationException($"There is no element {id}.");
                    var o = Args.Numbers(a, "origin"); var n = Args.Numbers(a, "normal");
                    object cut = null;
                    if (o.Count == 3 && n.Count == 3)
                        cut = Cut(el, new XYZ(o[0] / Mm, o[1] / Mm, o[2] / Mm), new XYZ(n[0], n[1], n[2]).Normalize(), Args.Int(a, "max_points", 200));
                    return new { element = id, category = el.Category?.Name, solids = SolidsReport(doc, el, 25), cut };
                }
            };

            yield return new RevitTool
            {
                Name = "type_structure",
                Description = "The layers of wall, floor, roof or ceiling types (function, material, thickness mm), how many elements use each, and for " +
                              "stacked/curtain walls what they are. Name a type or give a category.",
                InputSchema = McpServer.Obj(
                    ("category", "string", "Walls, Floors, Roofs or Ceilings (default Walls)", false),
                    ("type", "string", "Only types whose name contains this", false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var cat = ResolveCategory(doc, Args.Str(a, "category") ?? "Walls");
                    string want = Args.Str(a, "type");
                    var used = new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsNotElementType()
                        .GroupBy(e => e.GetTypeId().Value).ToDictionary(g => g.Key, g => g.Count());
                    return new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsElementType()
                        .Where(t => want == null || t.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(t =>
                        {
                            used.TryGetValue(t.Id.Value, out int count);
                            var cs = (t as HostObjAttributes)?.GetCompoundStructure();
                            return new
                            {
                                id = t.Id.Value, t.Name, used = count,
                                kind = (t as WallType)?.Kind.ToString(),
                                totalMm = cs == null ? (double?)null : Math.Round(cs.GetWidth() * Mm, 1),
                                layers = cs?.GetLayers().Select(l => new { function = l.Function.ToString(), material = doc.GetElement(l.MaterialId)?.Name, mm = Math.Round(l.Width * Mm, 1) }).ToList(),
                            };
                        })
                        .OrderByDescending(t => t.used).Take(60).ToList();
                }
            };

            yield return new RevitTool
            {
                Name = "element_boxes",
                Description = "Elements of a category with family, type, in-place or not, level, host, and their box (min/max mm) in the model's own " +
                              "frame or in shared (survey) coordinates. Filter by level or by text in the family/type name.",
                InputSchema = McpServer.Obj(
                    ("category", "string", "Category", true),
                    ("level", "string", "Only on this level", false),
                    ("name_contains", "string", "Only where family or type name contains this", false),
                    ("shared", "boolean", "Boxes in shared coordinates (E, N, elevation) instead of internal", false),
                    ("limit", "integer", "Most to list (default 100)", false),
                    ("offset", "integer", "Skip this many first", false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var cat = ResolveCategory(doc, Args.Str(a, "category"));
                    string lvl = Args.Str(a, "level"), text = Args.Str(a, "name_contains");
                    var tf = Args.Bool(a, "shared") == true ? doc.ActiveProjectLocation.GetTotalTransform() : Transform.Identity;
                    var all = new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsNotElementType()
                        .Where(e => !(e is FamilyInstance fi && fi.SuperComponent != null))
                        .Where(e => lvl == null || string.Equals(LevelName(doc, e), lvl, StringComparison.OrdinalIgnoreCase))
                        .Where(e => text == null || Label(doc, e).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                    int limit = Args.Int(a, "limit", 100), offset = Math.Max(0, Args.Int(a, "offset", 0));
                    return new
                    {
                        category = cat.Name, total = all.Count,
                        elements = all.Skip(offset).Take(limit).Select(e =>
                        {
                            var bb = e.get_BoundingBox(null);
                            var (mn, mx) = bb == null ? (null, null) : Box(bb, tf);
                            return new
                            {
                                id = e.Id.Value, label = Label(doc, e), level = LevelName(doc, e),
                                inPlace = (e as FamilyInstance)?.Symbol?.Family?.IsInPlace == true ? true : (bool?)null,
                                host = (e as FamilyInstance)?.Host?.Id.Value,
                                min = mn, max = mx,
                                size = bb == null ? null : new[] { R(mx[0] - mn[0]), R(mx[1] - mn[1]), R(mx[2] - mn[2]) },
                            };
                        }).ToList(),
                        notShown = Math.Max(0, all.Count - offset - limit),
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "reference_compare",
                Description = "Compare the active project with a reference model, category by category: lines them up (shared coordinates, then a " +
                              "best fit on the walls if align is auto), pairs elements by position and size, and reports how far apart the pairs are, " +
                              "what the reference has that ours lacks (with where), and what ours has extra. Reference positions are given in OUR frame.",
                InputSchema = McpServer.Obj(
                    ("document", "string", "The reference's name (from reference_open)", true),
                    ("categories", "string[]", "Categories to compare (default Walls, Windows, Doors, Floors, Roofs, Ceilings, Stairs, Columns, Structural Columns, Generic Models)", false),
                    ("align", "string", "shared (default: trust both survey positions), auto (shared then fit on walls), internal (no move)", false),
                    ("offset_mm", "number[]", "Extra shift [dx, dy] applied to the reference before fitting", false),
                    ("rotate_deg", "number", "Extra rotation of the reference (degrees) before fitting", false),
                    ("max_distance_mm", "number", "Furthest two elements may be apart and still pair (default 1000)", false),
                    ("list", "integer", "How many missing items and worst pairs to list per category (default 15)", false)),
                Run = (app, a) =>
                {
                    var ours = app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No project is open.");
                    var refDoc = Reference.Get(Args.Str(a, "document"));
                    var (move, fit) = Alignment(ours, refDoc, a);
                    var cats = Args.Strings(a, "categories");
                    if (cats.Count == 0) cats = new List<string> { "Walls", "Windows", "Doors", "Floors", "Roofs", "Ceilings", "Stairs", "Columns", "Structural Columns", "Generic Models" };
                    double maxD = Args.Dbl(a, "max_distance_mm") ?? 1000;
                    int list = Args.Int(a, "list", 15);
                    var report = new List<object>();
                    foreach (var cn in cats)
                    {
                        Category rc, oc;
                        try { rc = ResolveCategory(refDoc, cn); oc = ResolveCategory(ours, cn); } catch { report.Add(new { category = cn, error = "not a category in both" }); continue; }
                        var ri = Items(refDoc, rc, move); var oi = Items(ours, oc, Transform.Identity);
                        var m = ModelCompare.Match(ri, oi, maxD);
                        var offs = m.Pairs.Select(p => p.C3()).OrderBy(v => v).ToList();
                        report.Add(new
                        {
                            category = cn, reference = ri.Count, ours = oi.Count, paired = m.Pairs.Count,
                            medianOffsetMm = offs.Count == 0 ? (double?)null : R(offs[offs.Count / 2]),
                            within25 = m.Pairs.Count(p => p.C3() <= 25), within100 = m.Pairs.Count(p => p.C3() <= 100),
                            sizeOff50 = m.Pairs.Count(p => p.SizeDiff > 50),
                            worst = m.Pairs.OrderByDescending(p => p.C3() + p.SizeDiff).Take(list).Select(p => new
                            {
                                refId = p.Ref.Id, oursId = p.Ours.Id, refLabel = p.Ref.Label, oursLabel = p.Ours.Label,
                                planMm = R(p.Offset2D), zMm = R(p.OffsetZ), sizeMm = R(p.SizeDiff),
                            }).ToList(),
                            missing = m.Missing.OrderByDescending(x => x.Sx * x.Sy * x.Sz).Take(list).Select(x => new
                            {
                                refId = x.Id, x.Label, at = new[] { R(x.C.X), R(x.C.Y), R(x.C.Z) }, size = new[] { R(x.Sx), R(x.Sy), R(x.Sz) },
                            }).ToList(),
                            missingCount = m.Missing.Count,
                            extraCount = m.Extra.Count,
                            extra = m.Extra.Take(Math.Min(list, 8)).Select(x => new { oursId = x.Id, x.Label }).ToList(),
                        });
                    }
                    return new { alignment = fit, categories = report };
                }
            };

            yield return new RevitTool
            {
                Name = "reference_copy",
                Write = true,
                Description = "Copy elements, or just their families/types, from a reference model into the active project, moved into our frame the " +
                              "same way reference_compare lines them up. Use for families worth reusing (types_only: then place them with place_inserts " +
                              "or model tools at survey positions) or for standalone detail (in-place cornices, DirectShapes). Hosted windows/doors " +
                              "are not copied without their wall. dry_run (default) says what would come. Copies are marked 'BGMCP ref copy' in Comments. One undo step.",
                InputSchema = McpServer.Obj(
                    ("document", "string", "The reference's name", true),
                    ("element_ids", "integer[]", "Reference elements to copy", false),
                    ("category", "string", "Or every element of this category...", false),
                    ("name_contains", "string", "...whose family/type name contains this", false),
                    ("types_only", "boolean", "Copy the types (and their families) only, no instances", false),
                    ("align", "string", "shared (default), auto or internal, as reference_compare", false),
                    ("offset_mm", "number[]", "Extra shift [dx, dy] before fitting", false),
                    ("rotate_deg", "number", "Extra rotation before fitting", false),
                    ("dry_run", "boolean", "Only report (default true)", false)),
                Run = (app, a) =>
                {
                    var ours = app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No project is open.");
                    if (ours.IsReadOnly || ours.IsModifiable) throw new InvalidOperationException("The project can't be changed right now.");
                    var refDoc = Reference.Get(Args.Str(a, "document"));
                    var picked = new List<Element>();
                    foreach (var n in a["element_ids"] as JsonArray ?? new JsonArray())
                        if (n is JsonValue v && long.TryParse(v.ToString(), out long id) && refDoc.GetElement(new ElementId(id)) is Element e) picked.Add(e);
                    if (Args.Str(a, "category") is string cn)
                    {
                        var cat = ResolveCategory(refDoc, cn);
                        string text = Args.Str(a, "name_contains");
                        picked.AddRange(new FilteredElementCollector(refDoc).OfCategoryId(cat.Id).WhereElementIsNotElementType()
                            .Where(e => !(e is FamilyInstance fi && fi.SuperComponent != null))
                            .Where(e => text == null || Label(refDoc, e).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0));
                    }
                    picked = picked.GroupBy(e => e.Id.Value).Select(g => g.First()).ToList();
                    if (picked.Count == 0) throw new ArgumentException("Nothing to copy: give element_ids, or a category (and name_contains).");
                    bool typesOnly = Args.Bool(a, "types_only") == true, dry = Args.Bool(a, "dry_run") != false;

                    List<ElementId> ids; Transform tf; object fit = null; var skipped = new List<object>();
                    if (typesOnly)
                    {
                        ids = picked.Select(e => e is ElementType ? e.Id : e.GetTypeId()).Where(i => i != ElementId.InvalidElementId).Distinct().ToList();
                        // in-place families have no type that can travel on its own
                        ids = ids.Where(i => !(refDoc.GetElement(i) is FamilySymbol fs && fs.Family.IsInPlace)).ToList();
                        tf = Transform.Identity;
                    }
                    else
                    {
                        var set = new HashSet<long>(picked.Select(e => e.Id.Value));
                        ids = new List<ElementId>();
                        foreach (var e in picked)
                        {
                            if (e is FamilyInstance fi && fi.Host != null && !(fi.Host is Level) && !set.Contains(fi.Host.Id.Value))
                            { skipped.Add(new { id = e.Id.Value, label = Label(refDoc, e), why = $"hosted on {fi.Host.Category?.Name} {fi.Host.Id.Value}" }); continue; }
                            ids.Add(e.Id);
                        }
                        var al = Alignment(ours, refDoc, a);
                        fit = al.fit;
                        tf = al.move;
                    }
                    var summary = ids.Select(i => refDoc.GetElement(i)).GroupBy(e => e.Category?.Name ?? e.GetType().Name).ToDictionary(g => g.Key, g => g.Count());
                    if (dry || ids.Count == 0)
                        return new { dryRun = true, wouldCopy = ids.Count, byCategory = summary, alignment = fit, skipped = skipped.Take(20).ToList(), skippedCount = skipped.Count };

                    ICollection<ElementId> made = null;
                    var notes = BuildTools.Tx.Run(ours, "MCP: copy from reference", () =>
                    {
                        var opts = new CopyPasteOptions();
                        opts.SetDuplicateTypeNamesHandler(new KeepOurs());
                        made = ElementTransformUtils.CopyElements(refDoc, ids, ours, tf, opts);
                        foreach (var nid in made)
                            if (ours.GetElement(nid) is Element ne && !(ne is ElementType)
                                && ne.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) is Parameter c && !c.IsReadOnly) c.Set("BGMCP ref copy");
                    });
                    return new
                    {
                        copied = made.Count, byCategory = summary,
                        ids = made.Take(200).Select(i => i.Value).ToList(),
                        alignment = fit, skipped = skipped.Take(20).ToList(), skippedCount = skipped.Count,
                        revit = notes, undo = "One undo step in Revit.",
                    };
                }
            };
        }

        // ================================================================ alignment

        /// <summary>The move taking reference internal coordinates into ours, and how it was found.</summary>
        internal static (Transform move, object fit) Alignment(Document ours, Document refDoc, JsonObject a)
        {
            string mode = Args.Str(a, "align") ?? "shared";
            Transform t = mode == "internal" ? Transform.Identity
                : ours.ActiveProjectLocation.GetTotalTransform().Inverse.Multiply(refDoc.ActiveProjectLocation.GetTotalTransform());
            var extraOff = Args.Numbers(a, "offset_mm");
            double extraRot = (Args.Dbl(a, "rotate_deg") ?? 0) * Math.PI / 180;
            if (extraOff.Count >= 2 || extraRot != 0)
                t = Transform.CreateTranslation(new XYZ(extraOff.ElementAtOrDefault(0) / Mm, extraOff.ElementAtOrDefault(1) / Mm, 0))
                    .Multiply(Transform.CreateRotation(XYZ.BasisZ, extraRot)).Multiply(t);
            if (mode != "auto")
                return (t, new { mode, angleDeg = R(Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180 / Math.PI, 3), shiftMm = new[] { R(t.Origin.X * Mm), R(t.Origin.Y * Mm), R(t.Origin.Z * Mm) } });
            var from = WallSamples(refDoc).Select(p => ToPt(t.OfPoint(p))).ToList();
            var to = WallSamples(ours).Select(ToPt).ToList();
            var (move, rms, paired) = ModelCompare.Align(from, to, PlanMove.Identity);
            var total = ToTransform(move).Multiply(t);
            return (total, new
            {
                mode, refinedAngleDeg = R(move.Angle * 180 / Math.PI, 4), refinedShiftMm = new[] { R(move.Dx), R(move.Dy) },
                rmsMm = R(rms), pairedPoints = paired, ofPoints = from.Count,
                angleDeg = R(Math.Atan2(total.BasisX.Y, total.BasisX.X) * 180 / Math.PI, 3),
                shiftMm = new[] { R(total.Origin.X * Mm), R(total.Origin.Y * Mm), R(total.Origin.Z * Mm) },
            });
        }

        private static Transform ToTransform(PlanMove m) =>
            Transform.CreateTranslation(new XYZ(m.Dx / Mm, m.Dy / Mm, 0)).Multiply(Transform.CreateRotation(XYZ.BasisZ, m.Angle));

        private static IEnumerable<XYZ> WallSamples(Document d)
        {
            var pts = new List<XYZ>();
            foreach (var w in new FilteredElementCollector(d).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (!(w.Location is LocationCurve lc) || !lc.Curve.IsBound) continue;
                var c = lc.Curve;
                int n = Math.Max(1, (int)(c.Length * Mm / 500));
                for (int i = 0; i <= n; i++) pts.Add(c.Evaluate((double)i / n, true));
            }
            if (pts.Count <= 6000) return pts;
            int step = pts.Count / 6000 + 1;
            return pts.Where((_, i) => i % step == 0);
        }

        private static List<CmpItem> Items(Document d, Category cat, Transform tf)
        {
            var items = new List<CmpItem>();
            foreach (var e in new FilteredElementCollector(d).OfCategoryId(cat.Id).WhereElementIsNotElementType())
            {
                if (e is FamilyInstance fi && fi.SuperComponent != null) continue;
                var bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                var (mn, mx) = Box(bb, tf);
                items.Add(new CmpItem
                {
                    Id = e.Id.Value, Key = cat.Name, Label = Label(d, e),
                    C = new Pt((mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, (mn[2] + mx[2]) / 2),
                    Sx = mx[0] - mn[0], Sy = mx[1] - mn[1], Sz = mx[2] - mn[2],
                });
            }
            return items;
        }

        private static double C3(this CmpPair p) => Math.Sqrt(p.Offset2D * p.Offset2D + p.OffsetZ * p.OffsetZ);

        // ================================================================ geometry

        private static IEnumerable<Solid> Solids(GeometryElement ge)
        {
            if (ge == null) yield break;
            foreach (var o in ge)
            {
                if (o is Solid s && s.Volume > 1e-9) yield return s;
                else if (o is GeometryInstance gi) foreach (var x in Solids(gi.GetInstanceGeometry())) yield return x;
            }
        }

        private static object SolidsReport(Document doc, Element el, int max)
        {
            var solids = Solids(el.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine })).ToList();
            return new
            {
                count = solids.Count,
                volumeM3 = Math.Round(solids.Sum(s => s.Volume) * Math.Pow(Mm / 1000, 3), 3),
                list = solids.OrderByDescending(s => s.Volume).Take(max).Select(s =>
                {
                    var bb = s.GetBoundingBox();
                    var t = bb.Transform;
                    var p0 = t.OfPoint(bb.Min); var p1 = t.OfPoint(bb.Max);
                    var mats = s.Faces.Cast<Face>().Select(f => doc.GetElement(f.MaterialElementId)?.Name).Where(x => x != null).Distinct().ToList();
                    return new
                    {
                        min = P(new XYZ(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Min(p0.Z, p1.Z))),
                        max = P(new XYZ(Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y), Math.Max(p0.Z, p1.Z))),
                        volumeM3 = Math.Round(s.Volume * Math.Pow(Mm / 1000, 3), 4), faces = s.Faces.Size, materials = mats,
                    };
                }).ToList(),
            };
        }

        private static object Cut(Element el, XYZ origin, XYZ normal, int maxPoints)
        {
            bool vertical = Math.Abs(normal.Z) < 0.9;
            XYZ ex = vertical ? new XYZ(-normal.Y, normal.X, 0).Normalize() : XYZ.BasisX;
            XYZ ey = vertical ? XYZ.BasisZ : XYZ.BasisY;
            // the cut keeps the part on the normal's side; its face on the plane is the section
            var plane = Plane.CreateByNormalAndOrigin(normal, origin);
            var loops = new List<object>();
            foreach (var s in Solids(el.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine })))
            {
                Solid part;
                try { part = BooleanOperationsUtils.CutWithHalfSpace(s, plane); } catch { continue; }
                if (part == null || part.Faces.Size == 0) continue;
                foreach (Face f in part.Faces)
                {
                    if (!(f is PlanarFace pf) || pf.FaceNormal.DotProduct(normal) > -0.999) continue;
                    if (Math.Abs((pf.Origin - origin).DotProduct(normal)) > 1e-4) continue;
                    foreach (var loop in pf.GetEdgesAsCurveLoops())
                    {
                        var pts = new List<double[]>();
                        foreach (var c in loop)
                            foreach (var p in c.Tessellate().Take(c.Tessellate().Count - 1))
                            {
                                var d = p - origin;
                                pts.Add(new[] { R(d.DotProduct(ex) * Mm), R(vertical ? p.Z * Mm : d.DotProduct(ey) * Mm) });
                            }
                        var clean = Collinear(pts);
                        if (clean.Count > maxPoints) clean = clean.Where((_, i) => i % (clean.Count / maxPoints + 1) == 0).ToList();
                        loops.Add(new { points = clean.Count, loop = clean });
                    }
                }
            }
            return new { axes = vertical ? "u = along the plane (horizontal, mm from origin), v = height (model mm)" : "u = x, v = y (mm from origin)", loops };
        }

        private static List<double[]> Collinear(List<double[]> pts)
        {
            if (pts.Count < 4) return pts;
            var outp = new List<double[]>();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[(i - 1 + pts.Count) % pts.Count]; var b = pts[i]; var c = pts[(i + 1) % pts.Count];
                double cross = (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0]);
                double len = Math.Sqrt(Math.Pow(c[0] - a[0], 2) + Math.Pow(c[1] - a[1], 2));
                if (len < 1e-6 || Math.Abs(cross) / len > 0.5) outp.Add(b);
            }
            return outp;
        }

        private static FormData FormInfo(Document fdoc, GenericForm g)
        {
            var bb = g.get_BoundingBox(null);
            var f = new FormData
            {
                kind = g.GetType().Name, solid = g.IsSolid, sub = g.Subcategory?.Name, visible = g.Visible,
                size = bb == null ? null : new[] { R((bb.Max.X - bb.Min.X) * Mm), R((bb.Max.Y - bb.Min.Y) * Mm), R((bb.Max.Z - bb.Min.Z) * Mm) },
                at = bb == null ? null : P((bb.Min + bb.Max) / 2),
            };
            try
            {
                switch (g)
                {
                    case Extrusion x:
                        f.detail = $"from {R(x.StartOffset * Mm)} to {R(x.EndOffset * Mm)} mm, sketch of {x.Sketch.Profile.Size} loop(s) on {x.Sketch.SketchPlane?.Name}";
                        break;
                    case Sweep sw:
                        f.detail = (sw.ProfileSymbol?.Profile != null ? $"profile family {sw.ProfileSymbol.Profile.Name}" : "sketched profile") +
                                   (sw.PathSketch != null ? $", path {R(sw.PathSketch.Profile.Cast<CurveArray>().Sum(ca => ca.Cast<Curve>().Sum(c => c.Length)) * Mm)} mm" : "");
                        break;
                    case Blend bl:
                        f.detail = $"top {R(bl.TopOffset * Mm)} bottom {R(bl.BottomOffset * Mm)} mm";
                        break;
                    case Revolution rv:
                        f.detail = $"{R(rv.StartAngle * 180 / Math.PI)} to {R(rv.EndAngle * 180 / Math.PI)} deg";
                        break;
                }
            }
            catch { }
            return f;
        }

        private sealed class FormData
        {
            public string kind, sub, detail;
            public bool solid, visible;
            public double[] size, at;
        }

        // ================================================================ dialogs and warnings while opening

        private static void Answer(DialogBoxShowingEventArgs e, List<string> notes)
        {
            string what = e.DialogId;
            if (e is TaskDialogShowingEventArgs td) what += ": " + td.Message;
            else if (e is MessageBoxShowingEventArgs mb) what += ": " + mb.Message;
            notes.Add(what.Length > 200 ? what.Substring(0, 200) : what);
            if (e.DialogId != null && e.DialogId.IndexOf("Unresolved", StringComparison.OrdinalIgnoreCase) >= 0 && e.OverrideResult(1002)) return;
            foreach (int pick in new[] { (int)TaskDialogResult.Ok, (int)TaskDialogResult.Close, (int)TaskDialogResult.Yes, 1002, 1001 })
                try { if (e.OverrideResult(pick)) return; } catch { }
        }

        private static void Quiet(FailuresProcessingEventArgs e, List<string> notes)
        {
            var fa = e.GetFailuresAccessor();
            bool any = false;
            foreach (var m in fa.GetFailureMessages())
            {
                any = true;
                if (m.GetSeverity() == FailureSeverity.Warning) { fa.DeleteWarning(m); continue; }
                notes.Add("Error resolved: " + m.GetDescriptionText());
                if (m.HasResolutions()) fa.ResolveFailure(m);
            }
            if (any) e.SetProcessingResult(FailureProcessingResult.Continue);
        }

        private sealed class KeepOurs : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
        }

        // ================================================================ helpers

        private static Document Doc(UIApplication app) =>
            Reference.Current ?? app?.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No Revit project is open.");

        private static object Location(Document doc)
        {
            var pp = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
            return new
            {
                eastMm = R(pp.EastWest * Mm), northMm = R(pp.NorthSouth * Mm), elevationMm = R(pp.Elevation * Mm),
                angleToTrueNorthDeg = R(pp.Angle * 180 / Math.PI, 3), site = doc.ActiveProjectLocation.Name,
            };
        }

        private static List<Level> Levels(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();

        internal static Category ResolveCategory(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("category is needed.");
            if (Enum.TryParse(name, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic) && Category.GetCategory(doc, bic) is Category c0) return c0;
            foreach (Category c in doc.Settings.Categories) if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return c;
            foreach (Category c in doc.Settings.Categories) if (c.Name.Equals(name + "s", StringComparison.OrdinalIgnoreCase)) return c;
            throw new InvalidOperationException($"There is no category called \"{name}\".");
        }

        internal static string Label(Document d, Element e)
        {
            if (e is FamilyInstance fi && fi.Symbol != null) return (fi.Symbol.Family.IsInPlace ? "in-place " : "") + fi.Symbol.FamilyName + " : " + fi.Symbol.Name;
            if (e is DirectShape ds) return "DirectShape " + (ds.Name ?? "");
            string tn = d.GetElement(e.GetTypeId())?.Name;
            return tn ?? e.Name ?? e.GetType().Name;
        }

        private static string LevelName(Document d, Element e)
        {
            if (d.GetElement(e.LevelId) is Level l) return l.Name;
            foreach (var bip in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.STAIRS_BASE_LEVEL_PARAM, BuiltInParameter.ROOF_BASE_LEVEL_PARAM })
                if (e.get_Parameter(bip) is Parameter p && p.StorageType == StorageType.ElementId && d.GetElement(p.AsElementId()) is Level pl) return pl.Name;
            return null;
        }

        private static (double[] min, double[] max) Box(BoundingBoxXYZ bb, Transform tf)
        {
            double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue }, mx = { double.MinValue, double.MinValue, double.MinValue };
            foreach (double x in new[] { bb.Min.X, bb.Max.X })
                foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                    {
                        var p = tf.OfPoint(bb.Transform.OfPoint(new XYZ(x, y, z)));
                        double[] v = { p.X * Mm, p.Y * Mm, p.Z * Mm };
                        for (int i = 0; i < 3; i++) { mn[i] = Math.Min(mn[i], v[i]); mx[i] = Math.Max(mx[i], v[i]); }
                    }
            return (mn.Select(v => R(v)).ToArray(), mx.Select(v => R(v)).ToArray());
        }

        private static Pt ToPt(XYZ p) => new Pt(p.X * Mm, p.Y * Mm, p.Z * Mm);
        private static double[] P(XYZ p) => new[] { R(p.X * Mm), R(p.Y * Mm), R(p.Z * Mm) };
        private static double R(double v, int digits = 1) => Math.Round(v, digits);
    }
}
