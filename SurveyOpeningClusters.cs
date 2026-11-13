// 1.34.0: opening_clusters: window_look's one type per window merged into the few designs a facade really has. Within each
// window/door family, types whose settings match and whose sizes are within tol_mm join the type with the most windows;
// apply moves their windows to it (each changes by at most tol_mm) and deletes the emptied types. One undo step.
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
        private static readonly BuiltInParameter[] OpeningSizes =
        {
            BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.WINDOW_HEIGHT,
            BuiltInParameter.DOOR_WIDTH, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM,
        };

        private static ClusterType ClusterOf(FamilySymbol s, int instances)
        {
            var ct = new ClusterType { Id = s.Id.Value, Instances = instances };
            var keys = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (Parameter p in s.Parameters)
            {
                if (p.IsReadOnly || !p.HasValue || !(p.Definition is InternalDefinition d)) continue;
                bool own = d.BuiltInParameter == BuiltInParameter.INVALID;
                if (!own && !OpeningSizes.Contains(d.BuiltInParameter)) continue; // identity data, cost, mark... don't make a design
                string name = d.Name;
                bool length = false;
                try { length = d.GetDataType() == SpecTypeId.Length; } catch { }
                if (length && p.StorageType == StorageType.Double) ct.Sizes[name] = Math.Round(p.AsDouble() * Mm, 1);
                else if (own) keys[name] = p.StorageType == StorageType.String ? p.AsString() : p.StorageType == StorageType.ElementId ? p.AsElementId().Value.ToString() : p.AsValueString() ?? p.AsInteger().ToString();
            }
            ct.Key = string.Join(";", keys.Select(kv => kv.Key + "=" + kv.Value));
            return ct;
        }

        public static IEnumerable<RevitTool> OpeningClusterTools()
        {
            yield return new RevitTool
            {
                Name = "opening_clusters",
                Write = true,
                Description = "Merge window and door types that are one design into one type (window_look makes one type per window). Within each " +
                              "family, types whose non-size settings match (materials, yes/no...) and whose every size is within tol_mm join the " +
                              "type with the most instances. Preview first (default); apply: true moves their instances to that type (each " +
                              "changes by at most tol_mm, listed) and deletes the emptied types, so a change to the design is one type edit. " +
                              "One undo step.",
                InputSchema = Schema(
                    ("apply", S("boolean", "Merge (default false: preview)"), false),
                    ("tol_mm", S("number", "Sizes within this of the cluster's type are the same design (default 25)"), false),
                    ("families", Arr("string", "Only these families (default every window and door family)"), false),
                    ("keep_empty_types", S("boolean", "Keep the emptied types (default false: delete them)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    bool apply = Args.Bool(a, "apply") == true, keep = Args.Bool(a, "keep_empty_types") == true;
                    double tol = Args.Dbl(a, "tol_mm") ?? 25;
                    var only = new HashSet<string>(Args.Strings(a, "families"), StringComparer.OrdinalIgnoreCase);
                    var inst = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors }))
                        .WhereElementIsNotElementType().OfType<FamilyInstance>().Where(fi => fi.Symbol?.Family != null && !fi.Symbol.Family.IsInPlace && (only.Count == 0 || only.Contains(fi.Symbol.Family.Name))).ToList();
                    var plans = new List<(string family, TypeCluster c)>();
                    foreach (var fam in inst.GroupBy(fi => fi.Symbol.Family.Id.Value))
                    {
                        var types = fam.GroupBy(fi => fi.Symbol.Id.Value).Select(g => ClusterOf(g.First().Symbol, g.Count())).ToList();
                        foreach (var c in OpeningClusters.Cluster(types, tol).Where(c => c.Members.Count > 1)) plans.Add((fam.First().Symbol.Family.Name, c));
                    }
                    string Name(long id) => doc.GetElement(new ElementId(id))?.Name;
                    int moved = 0, deleted = 0;
                    if (apply && plans.Count > 0)
                        BuildTools.Tx.Run(doc, "MCP: opening clusters", () =>
                        {
                            foreach (var (_, c) in plans)
                            {
                                var seed = new ElementId(c.Seed.Id);
                                foreach (var m in c.Members.Where(m => m.Id != c.Seed.Id))
                                {
                                    foreach (var fi in inst.Where(fi => fi.Symbol.Id.Value == m.Id)) { fi.ChangeTypeId(seed); moved++; }
                                    if (!keep) try { doc.Delete(new ElementId(m.Id)); deleted++; } catch { }
                                }
                            }
                        });
                    int before = inst.Select(fi => fi.Symbol.Id.Value).Distinct().Count(), merged = plans.Sum(p => p.c.Members.Count - 1);
                    var answer = new
                    {
                        applied = apply, typesBefore = before, typesAfter = before - merged, tolMm = tol,
                        clusters = plans.Select(p => new
                        {
                            family = p.family, type = Name(p.c.Seed.Id), typeId = p.c.Seed.Id, sizes = p.c.Seed.Sizes,
                            joins = p.c.Members.Where(m => m.Id != p.c.Seed.Id).Select(m => new { type = apply ? null : Name(m.Id), typeId = m.Id, instances = m.Instances, changeMm = m.Sizes.ToDictionary(kv => kv.Key, kv => Math.Round(p.c.Seed.Sizes[kv.Key] - kv.Value, 1)).Where(kv => kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value) }).ToList(),
                            maxChangeMm = p.c.MaxChangeMm,
                        }).ToList(),
                        moved = apply ? moved : (int?)null, typesDeleted = apply ? deleted : (int?)null,
                        next = apply ? "Rename each kept type for its design (e.g. 'W1 sash 1200x2400'); run facade_correspondence to confirm the windows still match the elevation." :
                               plans.Count == 0 ? "No types to merge at this tolerance." : "Check maxChangeMm per cluster, then apply: true (tol_mm smaller to keep close designs apart).",
                    };
                    File.WriteAllText(Path.Combine(ReportsFolder(doc), "opening-clusters.json"), JsonSerializer.Serialize(answer, JsonOpts));
                    return answer;
                }
            };
        }
    }
}
