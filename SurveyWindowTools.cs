// 1.9.17: windows that look like the survey's elevations. Survey to Revit places one plain-pane family everywhere;
// the elevation draws each window's glass panes (sliding sashes, pairs of them, arched heads). window_look reads
// those panes (Survey/WindowPattern.cs, Revit-free) and builds one window family per look from the Metric Window
// template: the template's own opening (Width/Height; arched windows cut to the drawn outline), a frame with the panes cut out of it, and glass in them, set back
// from the wall face by the reveal. Works from any survey that draws its elevations' panes on a window layer.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> WindowLook()
        {
            yield return new RevitTool
            {
                Name = "window_family",
                Write = true,
                Description = "The parametric \"BG Window\" family every surveyed window is a type of. action probe: what the Metric Window " +
                              "template holds (planes, views, parameters). build: generate the family (saved to the reports folder) and, with " +
                              "load: true, load it into the model. flex: build it and try it at a list of type values, reporting any that " +
                              "Revit refuses. One undo step when loading.",
                InputSchema = Schema(
                    ("action", S("string", "probe | build | flex"), true),
                    ("load", S("boolean", "build: load it into the model (default false)"), false),
                    ("cases", Arr("object", "flex: type values to try, e.g. {\"Width\":900,\"Height\":2400,\"Panes Across\":2,\"Meeting Rail\":1}"), false)),
                Run = (app, a) =>
                {
                    string action = Args.Str(a, "action") ?? "probe";
                    if (action == "probe") return WindowFamilyBuilder.Probe(app);
                    if (action != "build" && action != "flex") throw new ArgumentException("action: probe, build or flex");
                    var doc = Doc(app);
                    var cases = (a["cases"] as JsonArray)?.OfType<JsonObject>()
                        .Select(o => o.ToDictionary(kv => kv.Key, kv => kv.Value.GetValue<double>())).ToList() ?? new List<Dictionary<string, double>>();
                    if (action == "flex" && cases.Count == 0)
                        cases = new List<Dictionary<string, double>>
                        {
                            new() { ["Width"] = 1230, ["Height"] = 1736, ["Panes Across"] = 2, ["Meeting Rail"] = 1, ["Meeting Rail Height"] = 1000 },
                            new() { ["Width"] = 845, ["Height"] = 2420, ["Panes Across"] = 1, ["Meeting Rail"] = 1, ["Meeting Rail Height"] = 1300 },
                            new() { ["Width"] = 460, ["Height"] = 1714, ["Panes Across"] = 1, ["Meeting Rail"] = 0 },
                            new() { ["Width"] = 1450, ["Height"] = 1683, ["Panes Across"] = 2, ["Meeting Rail"] = 0 },
                            new() { ["Width"] = 1600, ["Height"] = 3000, ["Panes Across"] = 4, ["Panes High Bottom"] = 3, ["Panes High Top"] = 2, ["Meeting Rail"] = 1, ["Meeting Rail Height"] = 1700 },
                            new() { ["Width"] = 5116, ["Height"] = 1685, ["Panes Across"] = 4, ["Panes High Bottom"] = 2, ["Meeting Rail"] = 0 },
                            new() { ["Width"] = 400, ["Height"] = 500, ["Panes Across"] = 1, ["Meeting Rail"] = 0 },
                        };
                    string path = Path.Combine(ReportsFolder(doc), "window-families", WindowFamilyBuilder.FamilyName + ".rfa");
                    var rep = WindowFamilyBuilder.Build(app, path, cases);
                    List<string> revit = null; string loaded = null;
                    if (action == "build" && Args.Bool(a, "load") == true)
                    {
                        var wdoc = Writable(app);
                        revit = BuildTools.Tx.Run(wdoc, "MCP: load BG Window", () =>
                        {
                            if (!wdoc.LoadFamily(path, new Overwrite(), out Family fam)) fam = new FilteredElementCollector(wdoc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == WindowFamilyBuilder.FamilyName);
                            loaded = fam == null ? "not loaded" : $"loaded, {fam.GetFamilySymbolIds().Count} type(s)";
                        });
                    }
                    return new { rep.Path, rep.Steps, rep.Planes, rep.Boxes, rep.Locks, flex = rep.Flex, loaded, revit };
                }
            };

            yield return new RevitTool
            {
                Name = "window_look",
                Write = true,
                Description = "Windows that look like the survey's elevations: for every window an elevation draws (elevation-index.json), its glass " +
                              "panes and outline (sashes, glazing bars, arched heads) are read from the elevation's window layer and each window becomes a " +
                              "type of the one parametric family \"BG Window\" (window_family: size, panes across, meeting rail, panes high, frame and bar widths, inset reveal_mm from " +
                              "the wall face); only arched windows get a family of their own. Windows whose size differs from the elevation by more than " +
                              "size_tolerance_mm are left (survey_autofix openings first). dry_run (default true) lists the types. One undo step.",
                InputSchema = Schema(
                    ("facades", Arr("string", "Only these facades (ids from elevation-index.json)"), false),
                    ("window_ids", Arr("integer", "Only these windows"), false),
                    ("reveal_mm", S("number", "Frame set back from the wall's outside face (default 100)"), false),
                    ("frame_depth_mm", S("number", "Frame depth (default 70)"), false),
                    ("size_tolerance_mm", S("number", "Largest size difference model/elevation to swap (default 150)"), false),
                    ("dry_run", S("boolean", "Only list (default true)"), false)),
                Run = (app, a) =>
                {
                    bool dry = Args.Bool(a, "dry_run") != false;
                    var doc = dry ? Doc(app) : Writable(app);
                    double reveal = Args.Dbl(a, "reveal_mm") ?? 100, depth = Args.Dbl(a, "frame_depth_mm") ?? 70, tol = Args.Dbl(a, "size_tolerance_mm") ?? 150;
                    var only = Args.Numbers(a, "window_ids").Select(d => (long)d).ToHashSet();
                    var facades = Args.Strings(a, "facades");
                    Func<IEnumerable<string>, Dictionary<string, string>> roles = null;
                    try { roles = ReadingFor(app, doc, a)?.Roles; } catch { /* layer names alone */ }

                    var notes = new List<string>();
                    var matches = new List<ElevMatch>();
                    foreach (var spec in ElevationSpecs(doc, a))
                    {
                        string fid = Args.Str(spec, "facade");
                        if (facades.Count > 0 && !facades.Any(f => f.Equals(fid, StringComparison.OrdinalIgnoreCase))) continue;
                        try { var r = ElevationOnFacade(doc, spec, roles); matches.AddRange(r.matched); notes.Add(r.note); }
                        catch (Exception ex) { notes.Add($"{fid}: {McpServer.Unwrap(ex).Message}"); }
                    }
                    if (matches.Count == 0) throw new InvalidOperationException("No window is matched to an elevation: run elevation_index first. " + string.Join(" ", notes));

                    var winCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Windows).Id;
                    var plan = new List<(FamilyInstance fi, WindowPattern p, double thick, string key, ElevMatch m)>();
                    var skipped = new List<object>();
                    var changed = new List<object>();
                    var resizedIds = new HashSet<long>();
                    var slides = new Dictionary<long, XYZ>();
                    foreach (var m in matches.GroupBy(m => m.Insert.Id).Select(g => g.First()))
                    {
                        if (only.Count > 0 && !only.Contains(m.Insert.Id)) continue;
                        if (!(doc.GetElement(new ElementId(m.Insert.Id)) is FamilyInstance fi) || fi.Category?.Id != winCat || m.Pattern == null) continue;
                        double w = WidthOf(fi);
                        double sill = LenOf(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) ?? 0, head = LenOf(fi, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM) ?? 0;
                        double dw = w - m.Pattern.Width, dh = head - sill - m.Pattern.Height;
                        var pat = m.Pattern; string how = null; XYZ slide = null;
                        double du = m.Insert.U - (m.U0 + m.U1) / 2;
                        // height: where the sill agrees with the elevation, the drawn height is the window's (Survey to
                        // Revit's family could not always take it, e.g. 765x2942); the type's Height sets it
                        if (Math.Abs(dh) > tol && Math.Abs(sill - m.Sill) <= 40 && pat.Height >= 300) { dh = 0; how = "height from the elevation"; }
                        if (Math.Abs(dw) > 5 && Math.Abs(dh) <= tol)
                        {
                            // width: the plan's (it cuts through the opening) where the drawn glass fits inside it, centred
                            // within 100 mm; where the glass is wider than the model's window (a width the old family
                            // capped, e.g. 4930), the elevation's, the window slid to the drawn centre (within 250 mm)
                            if (Math.Abs(du) <= 100 && pat.FitWidth(w) is WindowPattern fitted) { pat = fitted; dw = 0; }
                            else if (pat.FitWidth(w) == null && Math.Abs(du) <= 250 && Math.Abs(sill - m.Sill) <= 40 && !pat.Arched && (m.DirX != 0 || m.DirY != 0))
                            {
                                dw = 0; how = (how == null ? "" : how + "; ") + $"width from the elevation (its glass is wider than the model's {w:0}), slid {-du:+0;-0} mm";
                                if (Math.Abs(du) > 2) slide = new XYZ(-du * m.DirX / Mm, -du * m.DirY / Mm, 0);
                            }
                        }
                        if (Math.Abs(dw) > tol || Math.Abs(dh) > tol)
                        {
                            skipped.Add(new { window = fi.Id.Value, level = m.Insert.Level, why = $"model {w:0}x{head - sill:0}, elevation {m.Pattern.Width:0}x{m.Pattern.Height:0}",
                                modelW = R(w), modelH = R(head - sill), modelSill = R(sill), elevW = R(m.Pattern.Width), elevH = R(m.Pattern.Height), elevSill = R(m.Sill),
                                modelU = R(m.Insert.U), elevU0 = R(m.U0), elevU1 = R(m.U1), panes = m.Pattern.Panes.Count, reveal = m.Sheet.HasReveal,
                                paneX0 = R(m.Pattern.Panes.SelectMany(q => q).Min(q => q.X)), paneX1 = R(m.Pattern.Panes.SelectMany(q => q).Max(q => q.X)),
                                sheet = new[] { R(m.Sheet.OpeningLeft), R(m.Sheet.OpeningRight), R(m.Sheet.Bottom), R(m.Sheet.Top) } });
                            continue;
                        }
                        var p = fi.Mirrored ? pat.Mirrored() : pat;
                        double thick = fi.Host is Wall hw ? Math.Round(hw.Width * Mm / 25) * 25 : 300;
                        plan.Add((fi, p, thick, p.Key + "|t" + thick, m));
                        if (how != null) resizedIds.Add(fi.Id.Value);
                        if (how != null) changed.Add(new { window = fi.Id.Value, level = m.Insert.Level, how, from = $"{w:0}x{head - sill:0}", to = $"{pat.Width:0}x{pat.Height:0}" });
                        if (slide != null) slides[fi.Id.Value] = slide;
                    }
                    // rectangular windows: types of the one parametric family; arched ones (a different design): their own family
                    var typed = plan.Where(x => !x.p.Arched).Select(x =>
                    {
                        var v = x.p.TypeValues(out bool simp);
                        v["Inset"] = reveal; v["Frame Depth"] = depth;
                        return (x.fi, values: v, name: WindowPattern.TypeName(v), simp, x.m);
                    }).ToList();
                    var typeGroups = typed.GroupBy(x => x.name).ToList();
                    var types = typeGroups.Select(g => new
                    {
                        type = g.Key, windows = g.Count(), simplified = g.First().simp, ids = g.Take(8).Select(x => x.fi.Id.Value).ToList(),
                        sheet = new[] { R(g.First().m.Sheet.OpeningLeft), R(g.First().m.Sheet.OpeningRight), R(g.First().m.Sheet.Bottom), R(g.First().m.Sheet.Top) },
                    }).OrderByDescending(x => x.windows).ToList();
                    var groups = plan.Where(x => x.p.Arched).GroupBy(x => x.key).ToList();
                    var looks = groups.Select(g => new
                    {
                        family = FamilyNameFor(g.First().p, g.Key), look = g.First().p.Describe(), widthMm = R(g.First().p.Width), heightMm = R(g.First().p.Height),
                        panes = g.First().p.Panes.Count, windows = g.Count(), ids = g.Take(8).Select(x => x.fi.Id.Value).ToList(),
                        sheet = new[] { R(g.First().m.Sheet.OpeningLeft), R(g.First().m.Sheet.OpeningRight), R(g.First().m.Sheet.Bottom), R(g.First().m.Sheet.Top) },
                    }).OrderByDescending(x => x.windows).ToList();

                    var made = new List<object>(); var problems = new List<string>(); List<string> revit = null; int swapped = 0;
                    if (!dry && typeGroups.Count > 0)
                    {
                        // the family: loaded already, else generated (saved in the reports folder) and loaded
                        string famPath = Path.Combine(ReportsFolder(doc), "window-families", WindowFamilyBuilder.FamilyName + ".rfa");
                        bool have = new FilteredElementCollector(doc).OfClass(typeof(Family)).Any(f => f.Name == WindowFamilyBuilder.FamilyName);
                        if (!have) WindowFamilyBuilder.Build(app, famPath);
                        FamilySymbol TypeFor(string name, Dictionary<string, double> values)
                        {
                            Family fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == WindowFamilyBuilder.FamilyName);
                            if (fam == null && !doc.LoadFamily(famPath, new Overwrite(), out fam)) throw new InvalidOperationException("BG Window did not load from " + famPath);
                            var syms = fam.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>().ToList();
                            var sym = syms.FirstOrDefault(s => s.Name == name) ?? (FamilySymbol)syms[0].Duplicate(name);
                            foreach (var kv in values)
                            {
                                var prm = sym.LookupParameter(kv.Key);
                                if (prm == null || prm.IsReadOnly) continue;
                                if (prm.StorageType == StorageType.Integer) prm.Set((int)Math.Round(kv.Value)); else prm.Set(kv.Value / Mm);
                            }
                            if (!sym.IsActive) sym.Activate();
                            return sym;
                        }
                        // windows keeping their size and place: all in one go
                        revit = BuildTools.Tx.Run(doc, "MCP: windows as BG Window types", () =>
                        {
                            foreach (var g in typeGroups)
                            {
                                var keep = g.Where(x => !resizedIds.Contains(x.fi.Id.Value)).ToList();
                                if (keep.Count == 0) continue;
                                try
                                {
                                    var sym = TypeFor(g.Key, g.First().values);
                                    foreach (var x in keep)
                                        try { x.fi.Symbol = sym; swapped++; }
                                        catch (Exception ex) { problems.Add($"{x.fi.Id.Value}: {McpServer.Unwrap(ex).Message}"); }
                                    made.Add(new { type = g.Key, windows = keep.Count });
                                }
                                catch (Exception ex) { problems.Add($"{g.Key}: {McpServer.Unwrap(ex).Message}"); }
                            }
                        });
                        // windows taking a new size or place from the elevation: each on its own, so one the wall can't take
                        // (too short for it, a join) is left as it was and said, not the lot
                        foreach (var g in typeGroups)
                            foreach (var x in g.Where(x => resizedIds.Contains(x.fi.Id.Value)))
                                try
                                {
                                    revit.AddRange(BuildTools.Tx.Run(doc, $"MCP: window {x.fi.Id.Value} to its elevation", () =>
                                    {
                                        var sym = TypeFor(g.Key, g.First().values);
                                        if (slides.TryGetValue(x.fi.Id.Value, out var mv)) ElementTransformUtils.MoveElement(doc, x.fi.Id, mv);
                                        x.fi.Symbol = sym;
                                    }));
                                    swapped++;
                                    made.Add(new { type = g.Key, windows = 1, resized = x.fi.Id.Value });
                                }
                                catch (Exception ex) { problems.Add($"window {x.fi.Id.Value} left as it was: {McpServer.Unwrap(ex).Message}"); }
                    }
                    if (!dry && groups.Count > 0)
                    {
                        string folder = Path.Combine(ReportsFolder(doc), "window-families");
                        Directory.CreateDirectory(folder);
                        var files = new Dictionary<string, string>();
                        foreach (var g in groups)
                        {
                            string name = FamilyNameFor(g.First().p, g.Key);
                            if (new FilteredElementCollector(doc).OfClass(typeof(Family)).Any(f => f.Name == name)) { files[g.Key] = null; continue; }
                            try { files[g.Key] = BuildWindowFamily(app, g.First().p, g.First().thick, reveal, depth, Path.Combine(folder, name + ".rfa")); }
                            catch (Exception ex) { problems.Add($"{name}: {McpServer.Unwrap(ex).Message}"); }
                        }
                        var anotes = BuildTools.Tx.Run(doc, "MCP: arched windows from elevations", () =>
                        {
                            foreach (var g in groups)
                            {
                                string name = FamilyNameFor(g.First().p, g.Key);
                                if (!files.TryGetValue(g.Key, out var file)) continue;
                                Family fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == name);
                                if (fam == null && (file == null || !doc.LoadFamily(file, new Overwrite(), out fam))) { problems.Add($"{name}: did not load"); continue; }
                                var sym = fam.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>().FirstOrDefault();
                                if (sym == null) { problems.Add($"{name}: no type"); continue; }
                                if (!sym.IsActive) sym.Activate();
                                foreach (var x in g)
                                    try { x.fi.Symbol = sym; swapped++; }
                                    catch (Exception ex) { problems.Add($"{x.fi.Id.Value}: {McpServer.Unwrap(ex).Message}"); }
                                made.Add(new { family = name, windows = g.Count() });
                            }
                        });
                        revit = (revit ?? new List<string>()).Concat(anotes ?? new List<string>()).ToList();
                    }
                    return new
                    {
                        windowsOnElevations = matches.Count, toSwap = plan.Count, types = types.Count, arched = looks.Count, sizeDiffers = skipped.Count,
                        family = WindowFamilyBuilder.FamilyName, resized = changed, typeList = types.Take(40).ToList(), archedList = looks.ToList(), skipped = skipped.ToList(),
                        swapped, made = made.Take(40).ToList(), problems = problems.Take(20).ToList(), revit, elevations = notes,
                        next = dry ? "dry_run: false to build the families and swap them (with testmcp active)." : "Look at the elevations in Revit; survey_fidelity still scores sizes.",
                    };
                }
            };
        }

        private static string FamilyNameFor(WindowPattern p, string key)
        {
            uint h = 2166136261;
            foreach (char c in key) { h ^= c; h *= 16777619; }
            string look = p.FromDrawing ? p.Describe().Replace(" wide x ", "x").Replace(" high", "").Replace("arched ", "Arch") : "Plain";
            return $"BG_Window_{look}_{Math.Round(p.Width)}x{Math.Round(p.Height)}_{h % 0xFFFFFF:x6}";
        }

        private class Overwrite : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Family; overwriteParameterValues = true; return true; }
        }

        /// <summary>
        /// A window family from the Metric Window template, looking like the pattern: the template's opening replaced by one cut
        /// to the outline, a frame (outline less the panes) set back reveal mm from the host's outside face, glass in the panes.
        /// hostThick: the wall it goes in (mm), so the set-back is from that wall's face. Saved to path; returns path.
        /// </summary>
        private static string BuildWindowFamily(UIApplication app, WindowPattern p, double hostThick, double reveal, double depth, string path)
        {
            string template = Directory.GetFiles(app.Application.FamilyTemplatePath, "Metric Window.rft", SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.GetFiles(Path.GetDirectoryName(app.Application.FamilyTemplatePath.TrimEnd('\\')) ?? "", "Metric Window.rft", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new FileNotFoundException("No Metric Window.rft under " + app.Application.FamilyTemplatePath);
            var fdoc = app.Application.NewFamilyDocument(template);
            try
            {
                using (var t = new Transaction(fdoc, "window from elevation"))
                {
                    t.Start();
                    var wall = new FilteredElementCollector(fdoc).OfClass(typeof(Wall)).Cast<Wall>().First();
                    var ext = wall.Orientation.Normalize();
                    var right = ext.Negate().CrossProduct(XYZ.BasisZ).Normalize();
                    var lc = (LocationCurve)wall.Location;
                    var centre = lc.Curve.Evaluate(0.5, true);
                    var open = new FilteredElementCollector(fdoc).OfClass(typeof(Opening)).Cast<Opening>().FirstOrDefault();
                    double z0 = 0;
                    var mid = new XYZ(centre.X, centre.Y, 0);
                    if (open != null && open.IsRectBoundary && open.BoundaryRect?.Count == 2)
                    {
                        var r0 = open.BoundaryRect[0]; var r1 = open.BoundaryRect[1];
                        z0 = Math.Min(r0.Z, r1.Z);
                        var c = (r0 + r1) / 2;
                        mid = new XYZ(c.X, c.Y, 0);
                    }
                    // a point of the pattern (mm, from its bottom-left seen from outside) at off mm out from the wall's centre plane
                    XYZ At(Pt q, double off) => mid + right * ((q.X - p.Width / 2) / Mm) + XYZ.BasisZ * (z0 + q.Y / Mm) + ext * (off / Mm);
                    CurveArray Loop(IList<Pt> ring, double off)
                    {
                        // points closer than 2 mm merged first (Revit refuses shorter lines), so the loop stays closed
                        var pts = new List<Pt>();
                        foreach (var q in ring) if (pts.Count == 0 || q.Dist2D(pts[pts.Count - 1]) >= 2) pts.Add(q);
                        while (pts.Count > 3 && pts[pts.Count - 1].Dist2D(pts[0]) < 2) pts.RemoveAt(pts.Count - 1);
                        var ca = new CurveArray();
                        for (int i = 0; i < pts.Count; i++) ca.Append(Line.CreateBound(At(pts[i], off), At(pts[(i + 1) % pts.Count], off)));
                        return ca;
                    }

                    // the opening: the template's own, driven by Width/Height, for a rectangular window; only an arched
                    // window needs its own cut to the drawn outline (the template's rectangle kept if Revit refuses it)
                    bool cut = false;
                    if (p.Arched || open == null)
                        try
                        {
                            fdoc.FamilyCreate.NewOpening(wall, Loop(p.Outline, 0));
                            cut = true;
                            if (open != null) fdoc.Delete(open.Id);
                        }
                        catch { /* the template's opening stays */ }

                    var fm = fdoc.FamilyManager;
                    if (fm.CurrentType == null) fm.NewType($"{Math.Round(p.Width)}x{Math.Round(p.Height)}");
                    void SetLen(string n, double mm) { var fp = fm.get_Parameter(n); if (fp != null) try { fm.Set(fp, mm / Mm); } catch { } }
                    SetLen("Width", p.Width); SetLen("Height", p.Height);
                    if (!cut) { SetLen("Rough Width", p.Width); SetLen("Rough Height", p.Height); }

                    double face = hostThick / 2;
                    var frameMat = MaterialNamed(fdoc, "Window Frame", new Color(235, 235, 230), 0);
                    var glassMat = MaterialNamed(fdoc, "Glass", new Color(170, 200, 215), 60);
                    // frame: the outline with the panes cut out, from reveal to reveal + depth behind the face
                    var sp = SketchPlane.Create(fdoc, Plane.CreateByNormalAndOrigin(ext.Negate(), At(new Pt(0, 0), face - reveal)));
                    var frameProfile = new CurveArrArray();
                    frameProfile.Append(Loop(p.Outline, face - reveal));
                    foreach (var pane in p.Panes) frameProfile.Append(Loop(pane, face - reveal));
                    Extrusion frame;
                    try { frame = fdoc.FamilyCreate.NewExtrusion(true, frameProfile, sp, depth / Mm); }
                    catch (Exception ex) { throw new InvalidOperationException("frame: " + McpServer.Unwrap(ex).Message + $" (outline {p.Outline.Count} points, {p.Panes.Count} panes, opening {(cut ? "cut to the outline" : "the template's")})"); }
                    frame.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(frameMat);
                    // glass: in the middle of the frame
                    double gOff = face - reveal - depth / 2 + 4;
                    var gsp = SketchPlane.Create(fdoc, Plane.CreateByNormalAndOrigin(ext.Negate(), At(new Pt(0, 0), gOff)));
                    var glassProfile = new CurveArrArray();
                    foreach (var pane in p.Panes) glassProfile.Append(Loop(pane, gOff));
                    Extrusion glass;
                    try { glass = fdoc.FamilyCreate.NewExtrusion(true, glassProfile, gsp, 8 / Mm); }
                    catch (Exception ex) { throw new InvalidOperationException("glass: " + McpServer.Unwrap(ex).Message); }
                    glass.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(glassMat);
                    try
                    {
                        var subs = fdoc.OwnerFamily.FamilyCategory.SubCategories;
                        if (subs.Contains("Glass")) glass.Subcategory = subs.get_Item("Glass");
                        if (subs.Contains("Frame/Mullion")) frame.Subcategory = subs.get_Item("Frame/Mullion");
                    }
                    catch { }
                    if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit would not keep the family's geometry");
                }
                fdoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                return path;
            }
            finally { fdoc.Close(false); }
        }

        private static ElementId MaterialNamed(Document d, string name, Color c, int transparency)
        {
            var m = new FilteredElementCollector(d).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == name);
            if (m != null) return m.Id;
            var id = Autodesk.Revit.DB.Material.Create(d, name);
            m = (Material)d.GetElement(id);
            m.Color = c; m.Transparency = transparency;
            return id;
        }
    }
}
