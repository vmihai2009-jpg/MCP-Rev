// 1.33.0: editability_audit: what in the model a person would struggle to correct (solids with no family, one-off in-place
// families, families with no sizes to change, curves built in straight pieces, one type per window), with the tool that
// turns each into something editable.
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
        public static IEnumerable<RevitTool> EditabilityTools()
        {
            yield return new RevitTool
            {
                Name = "editability_audit",
                Description = "How easy the model is for a person to correct: DirectShape solids (no family, no parameters), in-place families " +
                              "(one-offs), loadable families with no length parameters (fixed sizes), walls and floor/roof edges that are " +
                              "curves built as straight pieces, and window/door families with one type per instance. Each finding with its " +
                              "elements and the tool that makes them editable; a score 0-1 (share of elements a person can correct directly). " +
                              "Changes nothing.",
                InputSchema = Schema(
                    ("max_ids", S("integer", "Element ids listed per finding (default 30)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    int maxIds = (int)(Args.Dbl(a, "max_ids") ?? 30);
                    var cats = new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Stairs, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_Cornices, BuiltInCategory.OST_Reveals, BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns };
                    var all = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats.ToList())).WhereElementIsNotElementType().ToList();
                    var found = new Dictionary<string, EditFinding>();
                    void Add(string kind, long id, string why, string fix)
                    {
                        if (!found.TryGetValue(kind, out var f)) found[kind] = f = new EditFinding { Kind = kind, Why = why, Fix = fix };
                        if (!f.Elements.Contains(id)) f.Elements.Add(id);
                    }
                    bool HasSizes(Element e)
                    {
                        foreach (Parameter p in e.Parameters)
                            if (p.Definition is InternalDefinition d && d.BuiltInParameter == BuiltInParameter.INVALID && !p.IsReadOnly)
                                try { if (d.GetDataType() == SpecTypeId.Length) return true; } catch { }
                        return false;
                    }
                    var sizes = new Dictionary<long, bool>();
                    foreach (var e in all)
                    {
                        if (e is DirectShape)
                            Add("direct shape", e.Id.Value, "a solid with no family and no parameters: it can only be deleted and remade", "facade_types (one family per repeated detail) or model it as a family");
                        else if (e is FamilyInstance fi && fi.Symbol?.Family is Family fam)
                        {
                            if (fam.IsInPlace) Add("in-place family", e.Id.Value, "a one-off family: a change to it does not carry to others like it", "facade_types, or rebuild as a loadable family");
                            else if (e.Category?.Id.Value == (long)BuiltInCategory.OST_GenericModel || e.Category?.Id.Value == (long)BuiltInCategory.OST_Columns)
                            {
                                if (!sizes.TryGetValue(fi.Symbol.Id.Value, out var ok)) sizes[fi.Symbol.Id.Value] = ok = HasSizes(fi.Symbol) || HasSizes(fi);
                                if (!ok && !HasSizes(fi)) Add("family without parameters", e.Id.Value, "a loadable family with no length parameters: its sizes cannot be changed without editing the family", "add width/height/depth parameters to the family (or facade_types with your own family)");
                            }
                        }
                    }
                    // one type per instance (window_look's per-window types): families where most types hold one window
                    foreach (var g in all.OfType<FamilyInstance>().Where(fi => fi.Category?.Id.Value == (long)BuiltInCategory.OST_Windows || fi.Category?.Id.Value == (long)BuiltInCategory.OST_Doors)
                                         .GroupBy(fi => fi.Symbol.Family.Id.Value))
                    {
                        var byType = g.GroupBy(fi => fi.Symbol.Id.Value).ToList();
                        int single = byType.Count(t => t.Count() == 1);
                        if (byType.Count >= 4 && single >= 0.7 * byType.Count)
                            foreach (var t in byType.Where(t => t.Count() == 1)) Add("one type per instance", t.First().Id.Value, $"family {g.First().Symbol.Family.Name}: {single} of {byType.Count} types hold one instance each, so alike windows change one at a time", "opening_clusters (merge alike types)");
                    }
                    // curves built as straight pieces: walls by level, and each floor/roof's sketch edges
                    foreach (var lv in all.OfType<Wall>().Where(w => w.Location is LocationCurve lc && lc.Curve is Line).GroupBy(w => w.LevelId.Value))
                    {
                        var walls = lv.ToList();
                        var pieces = walls.Select(w => { var c = ((LocationCurve)w.Location).Curve; var p0 = CadLink.ToMm(c.GetEndPoint(0)); var p1 = CadLink.ToMm(c.GetEndPoint(1)); return (new Pt(p0.X, p0.Y), new Pt(p1.X, p1.Y)); }).ToList();
                        foreach (var ch in CurveChains.Find(pieces, new CurveChainOptions { JoinMm = 20 }))
                            foreach (var m in ch.Members) Add("curve in straight pieces", walls[m].Id.Value, "walls that follow a curve as straight pieces: changing the radius means moving each piece", "curve_walls (one curved wall per run, keeps its windows and sweeps)");
                    }
                    foreach (var h in all.Where(e => e is Floor || e is FootPrintRoof || e is Ceiling))
                    {
                        try
                        {
                            var sk = (h is Floor fl ? doc.GetElement(fl.SketchId) : h is Ceiling ce ? doc.GetElement(ce.SketchId) : null) as Sketch;
                            var lines = new List<(Pt, Pt)>();
                            if (sk != null)
                                foreach (CurveArray arr in sk.Profile) foreach (Curve c in arr) if (c is Line) { var p0 = CadLink.ToMm(c.GetEndPoint(0)); var p1 = CadLink.ToMm(c.GetEndPoint(1)); lines.Add((new Pt(p0.X, p0.Y), new Pt(p1.X, p1.Y))); }
                            if (h is FootPrintRoof rf)
                                foreach (ModelCurveArray arr in rf.GetProfiles()) foreach (ModelCurve mc in arr) if (mc.GeometryCurve is Line) { var c = mc.GeometryCurve; var p0 = CadLink.ToMm(c.GetEndPoint(0)); var p1 = CadLink.ToMm(c.GetEndPoint(1)); lines.Add((new Pt(p0.X, p0.Y), new Pt(p1.X, p1.Y))); }
                            if (lines.Count >= 3 && CurveChains.Find(lines, new CurveChainOptions { JoinMm = 5 }).Count > 0)
                                Add("curve in straight pieces", h.Id.Value, "a floor, ceiling or roof edge that follows a curve as straight pieces", "edit its boundary: replace the pieces with one arc (the curve's centre and radius: curve_check)");
                        }
                        catch { }
                    }
                    var list = found.Values.OrderByDescending(f => Editability.Cost.TryGetValue(f.Kind, out var c) ? c * f.Elements.Count : f.Elements.Count).ToList();
                    double score = Editability.Score(list, all.Count);
                    var answer = new
                    {
                        score, elements = all.Count,
                        findings = list.Select(f => new { kind = f.Kind, count = f.Elements.Count, cost = Editability.Cost.TryGetValue(f.Kind, out var c) ? c : 0.5, why = f.Why, fix = f.Fix, ids = f.Elements.Take(maxIds).ToList() }).ToList(),
                        next = list.Count == 0 ? "Everything here can be corrected directly." : "Work the findings top down (most elements x hardest to edit first); re-run to see the score rise.",
                    };
                    File.WriteAllText(Path.Combine(ReportsFolder(doc), "editability-audit.json"), JsonSerializer.Serialize(answer, JsonOpts));
                    return answer;
                }
            };
        }
    }
}
