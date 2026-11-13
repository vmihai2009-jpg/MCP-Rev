// 1.26.0: facade_types: one editable family per repeated detail type (facade_detail_map's T1...), placed at every item of
// the type on the wall face behind it, turned to the wall there (so it also sits right on a curved wall). A person edits
// the one family and every console / keystone / pediment of that type follows. facade_relief leaves out what these cover.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> FacadeTypeTools()
        {
            yield return new RevitTool
            {
                Name = "facade_types",
                Write = true,
                Description = "One editable family per repeated piece of facade detail, placed at every repeat: facade_detail_map groups the " +
                              "pieces (consoles, keystones, pediments, hoods, sills, balusters, modillions...) into types; for each type (or the " +
                              "ones named) this builds a Generic Model family from the type's first item (its drawn shapes, the openings left " +
                              "out, depth_mm deep, a step out for each shape inside another) or uses your family (family: 'Name' or " +
                              "'Name : Type'), and places one instance per item on the wall face behind it, turned to the wall there. " +
                              "Edit the family once and every repeat follows. facade_relief then leaves these boxes out. Re-running replaces " +
                              "this facade's placements of those types. apply: false (default) lists the types and items.",
                InputSchema = Schema(
                    ("facade", S("string", "Facade id (facade_list / elevation-index.json)"), true),
                    ("types", Arr("string", "Type ids from facade_detail_map (default: every piece type with min_count or more items)"), false),
                    ("min_count", S("integer", "Smallest repeat count built by default (default 2)"), false),
                    ("family", S("string", "Place this loaded family ('Name' or 'Name : Type') for the named types instead of building one"), false),
                    ("depth_mm", S("number", "How far a built piece stands out from the wall (default 80)"), false),
                    ("apply", S("boolean", "Build and place (default false: list)"), false)),
                Run = (app, a) =>
                {
                    bool apply = Args.Bool(a, "apply") == true;
                    var doc = apply ? Writable(app) : Doc(app);
                    var c = Correspond(app, doc, a);
                    if (c.drawn == null) return new { facade = c.fid, note = "The elevation has no lines in its sheet range." };
                    var map = FacadeMap.Read(c.drawn.Select(e => new DwgLine { Layer = e.Layer, A = new Pt(e.U0, e.Z0), B = new Pt(e.U1, e.Z1) }), c.fr.Length);
                    var named = Args.Strings(a, "types"); int minCount = Args.Int(a, "min_count", 2);
                    string famArg = Args.Str(a, "family"); double depth = Args.Dbl(a, "depth_mm") ?? 80;
                    var types = map.Types.Where(t => named.Count > 0 ? named.Any(n => n.Equals(t.Id, StringComparison.OrdinalIgnoreCase)) : FacadeTypes.IsPieceType(t) && t.Count >= minCount).ToList();
                    var items = map.Items.ToDictionary(i => i.Id);
                    var openings = map.Windows.Select(w => (w.U0, w.U1, w.Z0, w.Z1)).ToList();
                    var lines = c.drawn.Select(e => (new Pt(e.U0, e.Z0), new Pt(e.U1, e.Z1))).ToList();
                    var listing = types.Select(t => new { type = t.Id, t.Kind, t.Count, items = t.Items.Take(30).ToList() }).ToList();
                    if (!apply) return new { facade = c.fid, applied = false, types = listing, next = types.Count == 0 ? "No repeated piece types: facade_detail_map shows what the map found." : "apply: true to build and place" };

                    int outSign = Math.Abs(c.nx + c.fr.Uy) + Math.Abs(c.ny - c.fr.Ux) < 1e-6 ? 1 : -1;
                    var view = Find3DView(doc, null) ?? throw new InvalidOperationException("No 3D view to find the wall face in: open the default 3D view once, then run again.");
                    var walls = new ReferenceIntersector(new ElementCategoryFilter(BuiltInCategory.OST_Walls), FindReferenceTarget.Face, view);
                    var inward = new XYZ(-c.nx, -c.ny, 0);
                    XYZ At(double u, double v, double z) => new XYZ((c.fr.A.X + c.fr.Ux * u + c.nx * v) / Mm, (c.fr.A.Y + c.fr.Uy * u + c.ny * v) / Mm, z / Mm);
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                    string folder = Path.Combine(ReportsFolder(doc), "detail-families"); Directory.CreateDirectory(folder);
                    var log = new List<object>(); int made = 0, failed = 0;

                    // families first (outside the transaction: each is its own family document)
                    var famFor = new Dictionary<string, (string name, string path)>();
                    foreach (var t in types)
                    {
                        if (famArg != null) continue;
                        var rep = t.Items.Select(id => items.TryGetValue(id, out var it) ? it : null).FirstOrDefault(it => it != null);
                        if (rep == null) continue;
                        var faces = FacadeTypes.Faces(rep, lines, openings);
                        if (faces.Count == 0) { log.Add(new { type = t.Id, skipped = "its lines close no shape" }); continue; }
                        var pieces = faces.Select(f =>
                        {
                            double fu0 = f.Min(p => p.X), fu1 = f.Max(p => p.X), fz0 = f.Min(p => p.Y), fz1 = f.Max(p => p.Y);
                            int inside = faces.Count(g => g != f && g.Min(p => p.X) <= fu0 + 1 && g.Max(p => p.X) >= fu1 - 1 && g.Min(p => p.Y) <= fz0 + 1 && g.Max(p => p.Y) >= fz1 - 1);
                            return new DetailFamilyBuilder.Piece { Ring = f, V0 = 0, V1 = depth * (1 + 0.5 * inside) };
                        }).ToList();
                        string name = $"BG {c.fid} {t.Id} {t.Kind}";
                        try { famFor[t.Id] = (name, DetailFamilyBuilder.Build(app, pieces, outSign, Path.Combine(folder, name + ".rfa"))); }
                        catch (Exception ex) { failed++; log.Add(new { type = t.Id, error = McpServer.Unwrap(ex).Message }); }
                    }

                    var notes = BuildTools.Tx.Run(doc, $"MCP: facade types {c.fid}", () =>
                    {
                        foreach (var t in types)
                        {
                            string tag = $"MCP|facade_types|{c.fid}|{t.Id}|";
                            var old = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Where(e => (e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith(tag)).Select(e => e.Id).ToList();
                            if (old.Count > 0) doc.Delete(old);
                            FamilySymbol sym = null;
                            if (famArg != null)
                            {
                                var parts = famArg.Split(new[] { " : " }, StringSplitOptions.None);
                                sym = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                                    .FirstOrDefault(s => s.FamilyName == parts[0].Trim() && (parts.Length < 2 || s.Name == parts[1].Trim()));
                                if (sym == null) { failed++; log.Add(new { type = t.Id, error = $"no loaded family '{famArg}'" }); continue; }
                            }
                            else if (famFor.TryGetValue(t.Id, out var ff))
                            {
                                if (!doc.LoadFamily(ff.path, new Overwrite(), out Family fam)) fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(x => x.Name == ff.name);
                                sym = fam?.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>().FirstOrDefault();
                            }
                            if (sym == null) continue;
                            if (!sym.IsActive) sym.Activate();
                            int here = 0; var miss = new List<string>();
                            foreach (var id in t.Items)
                            {
                                if (!items.TryGetValue(id, out var it)) continue;
                                var (au, az) = FacadeTypes.Anchor(it);
                                try
                                {
                                    var hit = walls.FindNearest(At(au, 3000, (it.Z0 + it.Z1) / 2), inward);
                                    if (hit == null) { miss.Add($"{id}: no wall behind it"); continue; }
                                    var p = hit.GetReference().GlobalPoint;
                                    // the wall's own direction there (round a curve too), running the facade's way
                                    var n = new XYZ(c.nx, c.ny, 0);
                                    if (doc.GetElement(hit.GetReference()) is Wall hw && hw.GetGeometryObjectFromReference(hit.GetReference()) is Face face && face.Project(p) is IntersectionResult ir)
                                    { var fn = face.ComputeNormal(ir.UVPoint); if (Math.Abs(fn.Z) < 0.5) n = new XYZ(fn.X, fn.Y, 0).Normalize(); }
                                    var tl = new XYZ(n.Y, -n.X, 0); if (tl.X * c.fr.Ux + tl.Y * c.fr.Uy < 0) tl = tl.Negate();
                                    var lv = levels.LastOrDefault(l => l.Elevation * Mm <= az + 1) ?? levels.First();
                                    var at = new XYZ(p.X, p.Y, lv.Elevation);
                                    var fi = doc.Create.NewFamilyInstance(at, sym, lv, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                                    double off = (az - lv.Elevation * Mm) / Mm;
                                    foreach (var bip in new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM })
                                    { var prm = fi.get_Parameter(bip); if (prm != null && !prm.IsReadOnly) { prm.Set(off); break; } }
                                    double ang = Math.Atan2(tl.Y, tl.X);
                                    if (Math.Abs(ang) > 1e-9) ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(at, at + XYZ.BasisZ), ang);
                                    fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"{tag}box:{Math.Round(it.U0)},{Math.Round(it.U1)},{Math.Round(it.Z0)},{Math.Round(it.Z1)}");
                                    here++; made++;
                                }
                                catch (Exception ex) { failed++; miss.Add($"{id}: {McpServer.Unwrap(ex).Message}"); }
                            }
                            log.Add(new { type = t.Id, t.Kind, family = sym.FamilyName, placed = here, of = t.Items.Count, problems = miss.Count > 0 ? miss.Take(5).ToList() : null });
                        }
                    });
                    return new
                    {
                        facade = c.fid, applied = true, placed = made, failed, types = log, notes = notes.Count > 0 ? notes.Take(5).ToList() : null,
                        next = "facade_relief on this facade again (it leaves these boxes out), then facade_check. To change a type: edit its family 'BG <F> <T> <kind>' and reload.",
                    };
                }
            };
        }
    }
}
