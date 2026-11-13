// 1.25.0: facade_rustication: the rusticated base as channelled joints that stay editable: one Profile family (a square
// groove), one Reveal type per facade, and a native reveal on each of the facade's walls at each joint height facade_detail_map
// reads (PLAN.md F7). Reveals follow their wall (also round a curve) and are changed in one place, the type.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> RusticationTools()
        {
            yield return new RevitTool
            {
                Name = "facade_rustication",
                Write = true,
                Description = "Build a facade's rustication (channelled stone joints) as native reveals: facade_detail_map finds the joint heights " +
                              "(4 or more horizontal joints at a steady pitch); this makes a groove Profile family (joint_mm wide, depth_mm deep), " +
                              "a Reveal type 'BG <F> rustication' carrying it, and a reveal at every joint height on each of the facade's walls " +
                              "that the rustication runs along (and on curved walls joined to them). Editable: change the type's profile to change " +
                              "every joint. Re-running replaces this facade's rustication reveals. apply: false (default) lists the joints and walls.",
                InputSchema = Schema(
                    ("facade", S("string", "Facade id (facade_list / elevation-index.json)"), true),
                    ("apply", S("boolean", "Make the reveals (default false: list them)"), false),
                    ("joint_mm", S("number", "Groove width (default 20)"), false),
                    ("depth_mm", S("number", "Groove depth into the wall (default 20; a section through the base or the reference model says)"), false)),
                Run = (app, a) =>
                {
                    bool apply = Args.Bool(a, "apply") == true;
                    var doc = apply ? Writable(app) : Doc(app);
                    var c = Correspond(app, doc, a);
                    if (c.drawn == null) return new { facade = c.fid, note = "The elevation has no lines in its sheet range." };
                    var map = FacadeMap.Read(c.drawn.Select(e => new DwgLine { Layer = e.Layer, A = new Pt(e.U0, e.Z0), B = new Pt(e.U1, e.Z1) }), c.fr.Length);
                    var rust = map.Items.Where(i => i.Kind == "rustication" && i.LineHeights != null).ToList();
                    if (rust.Count == 0) return new { facade = c.fid, rustication = 0, note = "facade_detail_map finds no rustication on this elevation (4+ joints at a steady pitch)." };
                    double jw = Args.Dbl(a, "joint_mm") ?? 20, jd = Args.Dbl(a, "depth_mm") ?? 20;

                    var root = JsonNode.Parse(File.ReadAllText(Path.Combine(ReportsFolder(doc), "facades.json")));
                    var wallIds = root["facades"].AsArray().OfType<JsonObject>().FirstOrDefault(f => string.Equals(f["id"]?.GetValue<string>(), c.fid, StringComparison.OrdinalIgnoreCase))?["wallIds"]?.AsArray()
                        .Select(v => v.GetValue<long>()).ToList() ?? new List<long>();
                    var walls = wallIds.Select(id => doc.GetElement(new ElementId(id)) as Wall).Where(w => w?.Location is LocationCurve).ToList();
                    double U(XYZ p) => (p.X * Mm - c.fr.A.X) * c.fr.Ux + (p.Y * Mm - c.fr.A.Y) * c.fr.Uy;
                    double Base(Wall w) => ((doc.GetElement(w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? ElementId.InvalidElementId) as Level)?.Elevation ?? 0) * Mm
                                           + (w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0) * Mm;
                    // curved walls joined to the facade's walls carry the joints on round the curve
                    var ends = walls.SelectMany(w => new[] { 0, 1 }.Select(k => ((LocationCurve)w.Location).Curve.GetEndPoint(k))).ToList();
                    var curved = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                        .Where(w => w.Location is LocationCurve lc && lc.Curve is Arc && !wallIds.Contains(w.Id.Value)
                                    && new[] { 0, 1 }.Any(k => ends.Any(e => e.DistanceTo(lc.Curve.GetEndPoint(k)) * Mm < 100))).ToList();
                    var plan = new List<(Wall w, double z, string item)>();
                    foreach (var it in rust)
                        foreach (var z in it.LineHeights)
                        {
                            foreach (var w in walls)
                            {
                                double um = U(((LocationCurve)w.Location).Curve.Evaluate(0.5, true));
                                if (!(it.Runs ?? new List<(double, double)> { (it.U0, it.U1) }).Any(r => um >= r.u0 - 200 && um <= r.u1 + 200)) continue;
                                double wb = Base(w), wt = (w.get_BoundingBox(null)?.Max.Z ?? 0) * Mm;
                                if (z > wb + jw && z < wt - jw) plan.Add((w, z, it.Id));
                            }
                            foreach (var w in curved)
                            {
                                double wb = Base(w), wt = (w.get_BoundingBox(null)?.Max.Z ?? 0) * Mm;
                                if (z > wb + jw && z < wt - jw && plan.Any(p => Math.Abs(p.z - z) < 1)) plan.Add((w, z, it.Id));
                            }
                        }
                    var summary = rust.Select(it => new { item = it.Id, joints = it.LineHeights.Count, pitchMm = it.Size.TryGetValue("pitch", out var pt) ? pt : 0, zModelMm = new[] { Math.Round(it.Z0), Math.Round(it.Z1) }, u = new[] { Math.Round(it.U0), Math.Round(it.U1) } }).ToList();
                    if (!apply)
                        return new { facade = c.fid, applied = false, rustication = summary, reveals = plan.Count, walls = plan.Select(p => p.w.Id.Value).Distinct().ToList(), next = "apply: true to make them" };

                    string folder = Path.Combine(ReportsFolder(doc), "profiles"); Directory.CreateDirectory(folder);
                    string pname = $"BG rustication joint {jw:0}x{jd:0}";
                    string ppath = ProfileFamilyBuilder.Build(app, new List<Pt> { new Pt(0, -jw / 2), new Pt(jd, -jw / 2), new Pt(jd, jw / 2), new Pt(0, jw / 2) }, Path.Combine(folder, pname + ".rfa"));
                    int made = 0, failed = 0; var problems = new List<string>(); bool? flip = null;
                    var notes = BuildTools.Tx.Run(doc, $"MCP: facade rustication {c.fid}", () =>
                    {
                        if (!doc.LoadFamily(ppath, new Overwrite(), out Family fam))
                            fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(x => x.Name == pname);
                        var prof = fam?.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>().FirstOrDefault()
                                   ?? throw new InvalidOperationException("The joint profile did not load.");
                        if (!prof.IsActive) prof.Activate();
                        string tname = $"BG {c.fid} rustication";
                        var old = new FilteredElementCollector(doc).OfClass(typeof(WallSweep)).Where(e => doc.GetElement(e.GetTypeId())?.Name == tname).Select(e => e.Id).ToList();
                        if (old.Count > 0) doc.Delete(old);
                        var rtype = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Reveals).WhereElementIsElementType().Cast<ElementType>().FirstOrDefault(x => x.Name == tname)
                                    ?? (new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Reveals).WhereElementIsElementType().Cast<ElementType>().FirstOrDefault()
                                        ?? throw new InvalidOperationException("The model has no Reveal type to start from (load one from the Revit library once).")).Duplicate(tname);
                        rtype.get_Parameter(BuiltInParameter.WALL_SWEEP_PROFILE_PARAM)?.Set(prof.Id);
                        foreach (var (w, z, item) in plan)
                        {
                            try
                            {
                                WallSweep Make(bool fl) => WallSweep.Create(w, rtype.Id, new WallSweepInfo(WallSweepType.Reveal, false)
                                {
                                    ProfileId = prof.Id, Distance = (z - Base(w)) / Mm, DistanceMeasuredFrom = DistanceMeasuredFrom.Base,
                                    WallSide = WallSide.Exterior, WallOffset = 0, IsProfileFlipped = fl,
                                });
                                if (flip == null)
                                {
                                    // the groove must cut the wall: keep the way that takes volume out of it
                                    double V() { doc.Regenerate(); return w.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.AsDouble() ?? 0; }
                                    double v0 = V();
                                    var t1 = Make(false); double v1 = V(); doc.Delete(t1.Id);
                                    var t2 = Make(true); double v2 = V(); doc.Delete(t2.Id);
                                    flip = v2 < v1 && v2 < v0 - 1e-9;
                                    if (Math.Min(v1, v2) >= v0 - 1e-9) problems.Add($"wall {w.Id.Value}: the reveal took no volume out either way; check the joints in 3D");
                                }
                                Make(flip.Value); made++;
                            }
                            catch (Exception ex) { failed++; if (problems.Count < 10) problems.Add($"wall {w.Id.Value} at {z:0}: {McpServer.Unwrap(ex).Message}"); }
                        }
                    });
                    return new
                    {
                        facade = c.fid, applied = true, rustication = summary, revealsMade = made, failed, profile = pname, type = $"BG {c.fid} rustication",
                        problems = problems.Count > 0 ? problems : null, notes = notes.Count > 0 ? notes.Take(5).ToList() : null,
                        next = "facade_check on this facade; to change the joint, edit the profile family or the type's profile (all joints follow).",
                    };
                }
            };
        }
    }
}
