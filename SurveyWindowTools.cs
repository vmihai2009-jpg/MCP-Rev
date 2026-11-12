// 1.9.17: windows that look like the survey's elevations. Survey to Revit places one plain-pane family everywhere;
// the elevation draws each window's glass panes (sliding sashes, pairs of them, arched heads). window_look reads
// those panes (Survey/WindowPattern.cs, Revit-free) and builds one window family per look from the Metric Window
// template: an opening cut to the drawn outline, a frame with the panes cut out of it, and glass in them, set back
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
                Name = "window_look",
                Write = true,
                Description = "Windows that look like the survey's elevations: for every window an elevation draws (elevation-index.json), its glass " +
                              "panes and outline (sashes, glazing bars, arched heads) are read from the elevation's window layer, and one window " +
                              "family per look is built (opening cut to the outline, frame with the panes cut out, glass, set back by reveal_mm from " +
                              "the wall face) and swapped onto those windows. Windows whose size differs from the elevation by more than " +
                              "size_tolerance_mm are left (survey_autofix openings first). dry_run (default true) lists the looks. One undo step.",
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
                    foreach (var m in matches.GroupBy(m => m.Insert.Id).Select(g => g.First()))
                    {
                        if (only.Count > 0 && !only.Contains(m.Insert.Id)) continue;
                        if (!(doc.GetElement(new ElementId(m.Insert.Id)) is FamilyInstance fi) || fi.Category?.Id != winCat || m.Pattern == null) continue;
                        double w = WidthOf(fi);
                        double sill = LenOf(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) ?? 0, head = LenOf(fi, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM) ?? 0;
                        double dw = w - m.Pattern.Width, dh = head - sill - m.Pattern.Height;
                        if (Math.Abs(dw) > tol || Math.Abs(dh) > tol)
                        {
                            skipped.Add(new { window = fi.Id.Value, why = $"model {w:0}x{head - sill:0}, elevation {m.Pattern.Width:0}x{m.Pattern.Height:0}" });
                            continue;
                        }
                        var p = fi.Mirrored ? m.Pattern.Mirrored() : m.Pattern;
                        double thick = fi.Host is Wall hw ? Math.Round(hw.Width * Mm / 25) * 25 : 300;
                        plan.Add((fi, p, thick, p.Key + "|t" + thick, m));
                    }
                    var groups = plan.GroupBy(x => x.key).ToList();
                    var looks = groups.Select(g => new
                    {
                        family = FamilyNameFor(g.First().p, g.Key), look = g.First().p.Describe(), widthMm = R(g.First().p.Width), heightMm = R(g.First().p.Height),
                        panes = g.First().p.Panes.Count, windows = g.Count(), ids = g.Take(8).Select(x => x.fi.Id.Value).ToList(),
                        sheet = new[] { R(g.First().m.Sheet.OpeningLeft), R(g.First().m.Sheet.OpeningRight), R(g.First().m.Sheet.Bottom), R(g.First().m.Sheet.Top) },
                    }).OrderByDescending(x => x.windows).ToList();

                    var made = new List<object>(); var problems = new List<string>(); List<string> revit = null; int swapped = 0;
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
                        revit = BuildTools.Tx.Run(doc, "MCP: windows from elevations", () =>
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
                    }
                    return new
                    {
                        windowsOnElevations = matches.Count, toSwap = plan.Count, looks = looks.Count, sizeDiffers = skipped.Count,
                        list = looks.Take(40).ToList(), skipped = skipped.Take(20).ToList(),
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
                        var ca = new CurveArray();
                        for (int i = 0; i < ring.Count; i++)
                        {
                            var a0 = At(ring[i], off); var b0 = At(ring[(i + 1) % ring.Count], off);
                            if (a0.DistanceTo(b0) * Mm >= 0.5) ca.Append(Line.CreateBound(a0, b0));
                        }
                        return ca;
                    }

                    // the opening: cut to the outline (the template's rectangle kept, sized, if Revit will not take the profile)
                    bool cut = false;
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
                    var frame = fdoc.FamilyCreate.NewExtrusion(true, frameProfile, sp, depth / Mm);
                    frame.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(frameMat);
                    // glass: in the middle of the frame
                    double gOff = face - reveal - depth / 2 + 4;
                    var gsp = SketchPlane.Create(fdoc, Plane.CreateByNormalAndOrigin(ext.Negate(), At(new Pt(0, 0), gOff)));
                    var glassProfile = new CurveArrArray();
                    foreach (var pane in p.Panes) glassProfile.Append(Loop(pane, gOff));
                    var glass = fdoc.FamilyCreate.NewExtrusion(true, glassProfile, gsp, 8 / Mm);
                    glass.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(glassMat);
                    try
                    {
                        var subs = fdoc.OwnerFamily.FamilyCategory.SubCategories;
                        if (subs.Contains("Glass")) glass.Subcategory = subs.get_Item("Glass");
                        if (subs.Contains("Frame/Mullion")) frame.Subcategory = subs.get_Item("Frame/Mullion");
                    }
                    catch { }
                    t.Commit();
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
