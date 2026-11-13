// 1.18.0: facade checks that find faults without anyone looking at pictures (docs/FACADE-CHECKS.md).
// facade_correspondence: the model's edges against the survey elevation's lines on the facade plane (coverage, precision,
// shifted and spurious elements, gaps). opening_clear_check: rays through every window and door: what stands in front of
// the glass; and drawn openings the model lacks or has the wrong size. Both read only; any survey, any building.
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
        /// <summary>Layers of an elevation that are never building lines (annotation, frames, sections).</summary>
        private static readonly string[] CheckSkip = { "Zz_*", "*Text*", "*Level*", "*Sect*", "Defpoints", "*Frame*", "*Border*", "*Dim*", "*Hatch*", "*Grid*", "*Title*" };

        public static IEnumerable<RevitTool> FacadeCheckTools()
        {
            yield return new RevitTool
            {
                Name = "facade_correspondence",
                Description = "Does the model look like the survey elevation, measured not eyeballed: the model's edges near a facade (walls, windows, " +
                              "doors, detail families, wall sweeps, columns, roofs) projected onto the facade plane and matched line by line with the " +
                              "elevation's lines (within tol_mm, same direction). Gives coverage (share of the drawing the model has: the rest are gaps), " +
                              "precision (share of the model's detail the drawing explains: the rest is spurious or misplaced), and per element a verdict: " +
                              "ok, shifted (all of it matches after one move along or up: dU/dZ given; a storey-sized dZ is a wrong level or height fit), " +
                              "partial, or spurious (nothing drawn near it). Gaps = drawn lines nothing in the model matches, grouped, with their boxes. " +
                              "image true: grey = drawing matched, blue = drawing not matched, green = model matched, red = model not matched. " +
                              "Writes facade-correspondence-<facade>.json. Changes nothing. Needs the facade in elevation-index.json.",
                InputSchema = Schema(
                    ("facade", S("string", "Facade id (facade_list / elevation-index.json)"), true),
                    ("tol_mm", S("number", "Match distance (default 20)"), false),
                    ("depth_mm", Arr("number", "Model edges from this far behind to this far in front of the facade line (default [3000, 1500])"), false),
                    ("image", S("boolean", "Return the overlay picture (default false)"), false),
                    ("list", S("integer", "How many elements and gaps to list per kind (default 20)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    string fid = Args.Str(a, "facade") ?? throw new ArgumentException("facade is needed.");
                    var spec = ElevationSpecs(doc, null).FirstOrDefault(s => string.Equals(Args.Str(s, "facade"), fid, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"No elevation for {fid} in elevation-index.json: elevation_index first.");
                    Func<IEnumerable<string>, Dictionary<string, string>> roles = null;
                    try { roles = ReadingFor(app, doc, a)?.Roles; } catch { }
                    var fr = ElevationFrame(doc, spec, roles);
                    var (nx, ny) = Outward(doc, fr);
                    var dr = Args.Numbers(a, "depth_mm");
                    double behind = dr.Count == 2 ? Math.Abs(dr[0]) : 3000, front = dr.Count == 2 ? Math.Abs(dr[1]) : 1500;
                    var o = new FacadeMatchOptions { TolMm = Args.Dbl(a, "tol_mm") ?? 20 };
                    int list = Args.Int(a, "list", 20);

                    var drawn = fr.Lines.Where(l => !DwgFile.LayerMatches(l.Layer, CheckSkip))
                        .Select(l => new FacadeEdge(fr.U(l.A.X), fr.Z(l.A.Y), fr.U(l.B.X), fr.Z(l.B.Y), 0, l.Layer))
                        .Where(e => e.Len >= 5 && Math.Max(e.U0, e.U1) >= -500 && Math.Min(e.U0, e.U1) <= fr.Length + 500).ToList();
                    if (drawn.Count == 0) return new { facade = fid, note = "The elevation has no building lines in its sheet range: nothing to check against." };
                    double zLo = drawn.Min(e => Math.Min(e.Z0, e.Z1)) - 500, zHi = drawn.Max(e => Math.Max(e.Z0, e.Z1)) + 500;
                    var (model, labels) = ModelEdges(doc, fr, nx, ny, behind, front, zLo, zHi);
                    var m = FacadeMatch.Match(drawn, model, o);

                    // per item of the facade's detail map: is the drawing there covered?
                    var map = FacadeMap.Read(drawn.Select(e => new DwgLine { Layer = e.Layer, A = new Pt(e.U0, e.Z0), B = new Pt(e.U1, e.Z1) }), fr.Length);
                    var itemCover = map.Items.Select(it =>
                    {
                        double tot = 0, hit = 0;
                        for (int i = 0; i < drawn.Count; i++)
                        {
                            var e = drawn[i];
                            double cu = (e.U0 + e.U1) / 2, cz = (e.Z0 + e.Z1) / 2;
                            if (cu < it.U0 - 20 || cu > it.U1 + 20 || cz < it.Z0 - 20 || cz > it.Z1 + 20) continue;
                            tot += e.Len; if (m.DrawnMatched[i]) hit += e.Len;
                        }
                        return (it, share: tot > 0 ? hit / tot : 1.0);
                    }).ToList();

                    object El(ElementMatch e) => new
                    {
                        id = e.Element, label = labels.TryGetValue(e.Element, out var lb) ? lb : null, kind = e.Kind, matched = Math.Round(e.MatchedShare, 2),
                        shiftMm = e.Verdict == "shifted" ? new[] { Math.Round(e.ShiftU), Math.Round(e.ShiftZ) } : null,
                        afterShift = e.Verdict == "shifted" ? Math.Round(e.ShiftedShare, 2) : (double?)null,
                        u = new[] { Math.Round(e.U0), Math.Round(e.U1) }, zModelMm = new[] { Math.Round(e.Z0), Math.Round(e.Z1) },
                        storeyOffset = e.Verdict == "shifted" && Math.Abs(e.ShiftZ) >= 2400 && Math.Abs(e.ShiftZ) <= 4800 ? true : (bool?)null,
                    };
                    var byVerdict = m.Elements.GroupBy(e => e.Verdict).ToDictionary(g => g.Key, g => g.Count());
                    var answer = new
                    {
                        facade = fid, elevation = Args.Str(spec, "name"), tolMm = o.TolMm,
                        coverage = Math.Round(m.Coverage, 3), precision = Math.Round(m.Precision, 3),
                        drawnLengthM = Math.Round(m.DrawnMm / 1000, 1), modelDetailLengthM = Math.Round(m.ModelMm / 1000, 1),
                        elements = byVerdict,
                        shifted = m.Elements.Where(e => e.Verdict == "shifted").OrderByDescending(e => e.LengthMm).Take(list).Select(El).ToList(),
                        spurious = m.Elements.Where(e => e.Verdict == "spurious").OrderByDescending(e => e.LengthMm).Take(list).Select(El).ToList(),
                        partial = m.Elements.Where(e => e.Verdict == "partial").OrderBy(e => e.MatchedShare).Take(list).Select(El).ToList(),
                        gaps = m.Gaps.Take(list).Select(g => new { u = new[] { g.U0, g.U1 }, zModelMm = new[] { g.Z0, g.Z1 }, lengthMm = g.LengthMm, layers = g.Layers }).ToList(),
                        gapsTotal = m.Gaps.Count,
                        mapItemsUncovered = itemCover.Where(x => x.share < 0.5).Take(list).Select(x => new { x.it.Id, x.it.Kind, x.it.Type, covered = Math.Round(x.share, 2), u = new[] { Math.Round(x.it.U0), Math.Round(x.it.U1) }, zModelMm = new[] { Math.Round(x.it.Z0), Math.Round(x.it.Z1) } }).ToList(),
                        mapItems = map.Items.Count, mapItemsCovered = itemCover.Count(x => x.share >= 0.5),
                        target = "coverage and precision 0.90 or more per facade",
                        next = "shifted: move the element by -shiftMm (storeyOffset true: it is on the wrong level or the elevation's height fit is wrong there; check elevation-index.json for that part). " +
                               "spurious: delete it or rebuild it from the drawing. gaps and mapItemsUncovered: build them (facade_detail_map says how). Re-run after each fix.",
                    };
                    string folder = ReportsFolder(doc);
                    string file = Path.Combine(folder, $"facade-correspondence-{fid}.json");
                    File.WriteAllText(file, JsonSerializer.Serialize(new
                    {
                        answer,
                        allElements = m.Elements.Select(El).ToList(),
                        allGaps = m.Gaps.Select(g => new { u = new[] { g.U0, g.U1 }, zModelMm = new[] { g.Z0, g.Z1 }, lengthMm = g.LengthMm, layers = g.Layers }).ToList(),
                    }, JsonOpts));
                    string text = JsonSerializer.Serialize(new { answer, file }, JsonOpts);
                    if (Args.Bool(a, "image") != true) return text;
                    var img = new LineImage(2000);
                    for (int i = 0; i < drawn.Count; i++) img.Add(drawn[i].U0, drawn[i].Z0, drawn[i].U1, drawn[i].Z1, m.DrawnMatched[i] ? LineImage.Grey : LineImage.Blue, m.DrawnMatched[i] ? 1 : 2);
                    for (int i = 0; i < model.Count; i++) img.Add(model[i].U0, model[i].Z0, model[i].U1, model[i].Z1, m.ModelMatched[i] ? ((byte)0, (byte)150, (byte)60) : LineImage.Red, 1);
                    var png = img.Png();
                    File.WriteAllBytes(Path.Combine(folder, $"facade-correspondence-{fid}.png"), png);
                    var c = new ToolContent { Text = text + "\nImage: grey = drawn and matched, blue = drawn, nothing in the model; green = model matched, red = model with nothing drawn." };
                    c.Images.Add(("image/png", png));
                    return c;
                }
            };

            yield return new RevitTool
            {
                Name = "opening_clear_check",
                Description = "Is every window and door on a facade clear: rays (a grid over each opening) from outside straight in; the first thing " +
                              "hit must be the window or door itself. Anything hit before it (a detail family, a wall sweep, a DirectShape, a wall not " +
                              "cut) is blocking, listed with its id and name and the share of the opening it covers. Also compares the openings the " +
                              "elevation draws with the model's: drawn with no window in the model, or off by more than tol_mm on a side. Needs a 3D " +
                              "view where the facade is visible (the default {3D} view; no section box hiding it). Changes nothing.",
                InputSchema = Schema(
                    ("facade", S("string", "Facade id (facade_list / elevation-index.json)"), true),
                    ("grid", S("integer", "Rays across and up each opening (default 6)"), false),
                    ("tol_mm", S("number", "Drawn vs model opening tolerance per side (default 30)"), false),
                    ("view", S("string", "3D view name (default: {3D}, else the first 3D view)"), false),
                    ("list", S("integer", "How many to list (default 30)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    string fid = Args.Str(a, "facade") ?? throw new ArgumentException("facade is needed.");
                    var spec = ElevationSpecs(doc, null).FirstOrDefault(s => string.Equals(Args.Str(s, "facade"), fid, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"No elevation for {fid} in elevation-index.json: elevation_index first.");
                    Func<IEnumerable<string>, Dictionary<string, string>> roles = null;
                    try { roles = ReadingFor(app, doc, a)?.Roles; } catch { }
                    var fr = ElevationFrame(doc, spec, roles);
                    var (nx, ny) = Outward(doc, fr);
                    int grid = Math.Max(2, Math.Min(20, Args.Int(a, "grid", 6))), list = Args.Int(a, "list", 30);
                    double tol = Args.Dbl(a, "tol_mm") ?? 30;
                    string vn = Args.Str(a, "view");
                    var views = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate).ToList();
                    var view = (vn != null ? views.FirstOrDefault(v => v.Name == vn) : views.FirstOrDefault(v => v.Name == "{3D}")) ?? views.FirstOrDefault()
                        ?? throw new InvalidOperationException("No 3D view to cast rays in: open the default 3D view once, then run again.");
                    var ri = new ReferenceIntersector(view) { TargetType = FindReferenceTarget.Element, FindReferencesInRevitLinks = false };
                    var inward = new XYZ(-nx, -ny, 0);

                    var results = new List<(FamilyInstance fi, double u0, double u1, double z0, double z1, double blocked, Dictionary<long, int> by)>();
                    foreach (var cat in new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors })
                        foreach (var fi in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
                        {
                            if (!(fi.Location is LocationPoint lp)) continue;
                            var p = CadLink.ToMm(lp.Point);
                            double u = (p.X - fr.A.X) * fr.Ux + (p.Y - fr.A.Y) * fr.Uy, off = (p.X - fr.A.X) * nx + (p.Y - fr.A.Y) * ny;
                            if (Math.Abs(off) > 800 || u < -500 || u > fr.Length + 500) continue;
                            double w = WidthOf(fi), lz = (doc.GetElement(fi.LevelId) as Level)?.Elevation * Mm ?? 0;
                            double sill = LenOf(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) ?? 0, head = LenOf(fi, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM) ?? sill;
                            if (w <= 0 || head - sill <= 0) continue;
                            double z0 = lz + sill, z1 = lz + head;
                            int blocked = 0, rays = 0; var by = new Dictionary<long, int>();
                            for (int i = 0; i < grid; i++)
                                for (int j = 0; j < grid; j++)
                                {
                                    double ru = u - w / 2 + w * (0.08 + 0.84 * (i + 0.5) / grid), rz = z0 + (z1 - z0) * (0.08 + 0.84 * (j + 0.5) / grid);
                                    var origin = new XYZ((fr.A.X + fr.Ux * ru + nx * 2500) / Mm, (fr.A.Y + fr.Uy * ru + ny * 2500) / Mm, rz / Mm);
                                    rays++;
                                    IList<ReferenceWithContext> hits;
                                    try { hits = ri.Find(origin, inward); } catch { continue; }
                                    var ordered = hits.OrderBy(h => h.Proximity).ToList();
                                    int k = ordered.FindIndex(h => IsSelf(doc, h.GetReference().ElementId, fi));
                                    double limit = k >= 0 ? ordered[k].Proximity : (2500 + 800) / Mm;
                                    var before = ordered.Where(h => h.Proximity < limit - 1e-6 && !IsSelf(doc, h.GetReference().ElementId, fi)).ToList();
                                    if (before.Count == 0) continue;
                                    blocked++;
                                    long first = before[0].GetReference().ElementId.Value;
                                    by[first] = by.TryGetValue(first, out var n) ? n + 1 : 1;
                                }
                            results.Add((fi, u - w / 2, u + w / 2, z0, z1, rays == 0 ? 0 : (double)blocked / rays, by));
                        }

                    // the openings the elevation draws, against the model's
                    var drawnBoxes = FacadeMap.Openings(fr.WindowLines.Select(l => new DwgLine { Layer = l.Layer, A = new Pt(fr.U(l.A.X), fr.Z(l.A.Y)), B = new Pt(fr.U(l.B.X), fr.Z(l.B.Y)) }));
                    var missing = new List<object>(); var offSize = new List<object>();
                    foreach (var d in drawnBoxes)
                    {
                        var hit = results.Select(r => (r, ov: Math.Max(0, Math.Min(r.u1, d.U1) - Math.Max(r.u0, d.U0)) * Math.Max(0, Math.Min(r.z1, d.Z1) - Math.Max(r.z0, d.Z0))))
                            .Where(x => x.ov > 0.3 * d.W * d.H).OrderByDescending(x => x.ov).Select(x => x.r).FirstOrDefault();
                        if (hit.fi == null) { missing.Add(new { u = new[] { Math.Round(d.U0), Math.Round(d.U1) }, zModelMm = new[] { Math.Round(d.Z0), Math.Round(d.Z1) } }); continue; }
                        double dl = hit.u0 - d.U0, drt = hit.u1 - d.U1, db = hit.z0 - d.Z0, dt = hit.z1 - d.Z1;
                        if (new[] { dl, drt, db, dt }.Any(x => Math.Abs(x) > tol))
                            offSize.Add(new { id = hit.fi.Id.Value, label = ReferenceTools.Label(doc, hit.fi), modelMinusDrawnMm = new { left = Math.Round(dl), right = Math.Round(drt), sill = Math.Round(db), head = Math.Round(dt) } });
                    }

                    var blockedList = results.Where(r => r.blocked > 0).OrderByDescending(r => r.blocked).ToList();
                    var answer = new
                    {
                        facade = fid, view = view.Name, openings = results.Count,
                        clear = results.Count(r => r.blocked == 0), blocked = blockedList.Count,
                        score = results.Count == 0 ? 0 : Math.Round((double)results.Count(r => r.blocked == 0) / results.Count, 3),
                        blockedOpenings = blockedList.Take(list).Select(r => new
                        {
                            id = r.fi.Id.Value, label = ReferenceTools.Label(doc, r.fi), share = Math.Round(r.blocked, 2),
                            u = new[] { Math.Round(r.u0), Math.Round(r.u1) }, zModelMm = new[] { Math.Round(r.z0), Math.Round(r.z1) },
                            by = r.by.OrderByDescending(kv => kv.Value).Take(4).Select(kv => new { id = kv.Key, label = doc.GetElement(new ElementId(kv.Key)) is Element be ? ReferenceTools.Label(doc, be) : null, comments = doc.GetElement(new ElementId(kv.Key))?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() }).ToList(),
                        }).ToList(),
                        drawnOpenings = drawnBoxes.Count, drawnWithNoModelOpening = missing.Count, missing = missing.Take(list).ToList(),
                        offSizeCount = offSize.Count, offSize = offSize.Take(list).ToList(),
                        next = "Blocked by a facade_relief family: rebuild that facade's relief (1.18.0 cuts it back from the drawn openings). By a wall: the window doesn't cut it (check its host). " +
                               "missing: place_inserts or survey_autofix; offSize: survey_autofix / window_look.",
                    };
                    File.WriteAllText(Path.Combine(ReportsFolder(doc), $"opening-clear-{fid}.json"), JsonSerializer.Serialize(answer, JsonOpts));
                    return answer;
                }
            };
        }

        private static bool IsSelf(Document doc, ElementId id, FamilyInstance fi)
        {
            if (id == fi.Id) return true;
            return doc.GetElement(id) is FamilyInstance sub && sub.SuperComponent?.Id == fi.Id;
        }

        /// <summary>The facade's outward normal: away from the middle of the model's walls.</summary>
        private static (double nx, double ny) Outward(Document doc, ElevFrame fr)
        {
            var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.Location is LocationCurve).ToList();
            double nx = -fr.Uy, ny = fr.Ux;
            if (walls.Count == 0) return (nx, ny);
            double cx = walls.Average(w => ((LocationCurve)w.Location).Curve.Evaluate(0.5, true).X) * Mm, cy = walls.Average(w => ((LocationCurve)w.Location).Curve.Evaluate(0.5, true).Y) * Mm;
            var mid = new Pt(fr.A.X + fr.Ux * fr.Length / 2, fr.A.Y + fr.Uy * fr.Length / 2);
            if ((cx - mid.X) * nx + (cy - mid.Y) * ny > 0) { nx = -nx; ny = -ny; }
            return (nx, ny);
        }

        /// <summary>
        /// The model's solid edges near a facade projected onto its plane (u along, z model mm), per element, with a kind
        /// (wall, floor, roof, window, door, detail) and labels.
        /// </summary>
        private static (List<FacadeEdge> edges, Dictionary<long, string> labels) ModelEdges(Document doc, ElevFrame fr, double nx, double ny, double behind, double front, double zLo, double zHi)
        {
            var cats = new (BuiltInCategory cat, string kind)[]
            {
                (BuiltInCategory.OST_Walls, "wall"), (BuiltInCategory.OST_Floors, "floor"), (BuiltInCategory.OST_Roofs, "roof"),
                (BuiltInCategory.OST_Windows, "window"), (BuiltInCategory.OST_Doors, "door"), (BuiltInCategory.OST_GenericModel, "detail"),
                (BuiltInCategory.OST_Cornices, "detail"), (BuiltInCategory.OST_Reveals, "detail"), (BuiltInCategory.OST_Columns, "detail"),
                (BuiltInCategory.OST_StructuralColumns, "detail"), (BuiltInCategory.OST_StairsRailing, "detail"),
            };
            var edges = new List<FacadeEdge>(); var labels = new Dictionary<long, string>();
            var opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };
            (double u, double v, double z) Map(XYZ p)
            {
                double x = p.X * Mm - fr.A.X, y = p.Y * Mm - fr.A.Y;
                return (x * fr.Ux + y * fr.Uy, x * nx + y * ny, p.Z * Mm);
            }
            foreach (var (cat, kind) in cats)
                foreach (var e in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType())
                {
                    if (e is FamilyInstance fi && fi.SuperComponent != null) continue;
                    var bb = e.get_BoundingBox(null); if (bb == null) continue;
                    var corners = new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z) }.Select(c => Map(bb.Transform.OfPoint(c))).ToList();
                    if (corners.Max(c => c.u) < -500 || corners.Min(c => c.u) > fr.Length + 500 || corners.Max(c => c.v) < -behind || corners.Min(c => c.v) > front
                        || corners.Max(c => c.z) < zLo || corners.Min(c => c.z) > zHi) continue;
                    int before = edges.Count;
                    foreach (var solid in SolidsOf(e.get_Geometry(opt)))
                        foreach (Edge ed in solid.Edges)
                        {
                            IList<XYZ> pts;
                            try { pts = ed.Tessellate(); } catch { continue; }
                            for (int i = 0; i + 1 < pts.Count; i++)
                            {
                                var p0 = Map(pts[i]); var p1 = Map(pts[i + 1]);
                                if (Math.Max(p0.v, p1.v) < -behind || Math.Min(p0.v, p1.v) > front) continue;
                                var fe = new FacadeEdge(p0.u, p0.z, p1.u, p1.z, e.Id.Value, null, kind);
                                if (fe.Len >= 5) edges.Add(fe);   // edges running straight out of the facade project to nothing
                            }
                        }
                    if (edges.Count > before) labels[e.Id.Value] = ReferenceTools.Label(doc, e);
                }
            return (edges, labels);
        }

        private static IEnumerable<Solid> SolidsOf(GeometryElement ge)
        {
            if (ge == null) yield break;
            foreach (var o in ge)
            {
                if (o is Solid s && s.Volume > 1e-9) yield return s;
                else if (o is GeometryInstance gi) foreach (var x in SolidsOf(gi.GetInstanceGeometry())) yield return x;
            }
        }
    }
}
