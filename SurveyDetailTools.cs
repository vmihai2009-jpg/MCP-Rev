// 1.9.0: the detail tools, after studying how the colleague's Swallow Place model was built by hand (cornices as
// sweeps along the facades, pilasters/surrounds as placed pieces, parametric sash windows with an inset, dormers,
// chimneys, flat and pitched roof faces, stairs, ceilings). Each measures against the survey first and changes the
// model only when asked (apply / build). The measuring is in Survey/SurveyDetail.cs (Revit-free, tested).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> Phase4()
        {
            // ------------------------------------------------------------------ floors
            yield return new RevitTool
            {
                Name = "survey_floor_levels",
                Write = true,
                Description = "Floors against the survey: each floor slab's top at every spot height of its plan DWG (Zz_60_45-Level, metres AOD). " +
                              "Per floor: how many spots, median and worst difference, whether it is even; spots with no slab under them (voids, " +
                              "missing floors). apply: true moves each even floor by its median (Height Offset From Level). One undo step.",
                InputSchema = Schema(
                    ("levels", Arr("string", "Floors to check (default every floor with a plan DWG)"), false),
                    ("tolerance_mm", S("number", "Difference that counts (default 10)"), false),
                    ("apply", S("boolean", "Move floors off by more than the tolerance to their median (default false)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    double tol = Args.Dbl(a, "tolerance_mm") ?? 10, datum = Datum(doc);
                    bool apply = Args.Bool(a, "apply") == true;
                    var report = new List<object>(); var fixes = new List<(Floor f, double dev)>();
                    foreach (var (level, imp) in PlanFloors(doc, Args.Strings(a, "levels")))
                    {
                        var spots = PlanSpots(doc, imp);
                        var floors = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => f.LevelId == level.Id).ToList();
                        var tops = floors.ToDictionary(f => f.Id.Value, f => Faces(f, top: true));
                        var per = new Dictionary<long, List<double>>(); var orphans = new List<object>();
                        foreach (var (p, aod) in spots)
                        {
                            double want = aod - datum;
                            // spots far from this floor are another level's (a mezzanine, the street): skip
                            if (Math.Abs(want - level.Elevation * Mm) > 1500) continue;
                            long hit = 0; double z = double.NaN;
                            foreach (var f in floors) if (ZOn(tops[f.Id.Value], p) is double zz) { hit = f.Id.Value; z = zz; break; }
                            if (hit == 0) { orphans.Add(new { at = new[] { R(p.X), R(p.Y) }, aodM = aod / 1000 }); continue; }
                            if (!per.TryGetValue(hit, out var l)) per[hit] = l = new List<double>();
                            l.Add(z - want);
                        }
                        var rows = per.Select(kv =>
                        {
                            // the slab is where most spots agree (within 60 mm of each other); the rest stand on steps,
                            // landings, raised floors or pits, which the slab alone cannot model
                            var d = kv.Value.OrderBy(v => v).ToList();
                            int bi = 0, bn = 0;
                            for (int i = 0, j = 0; i < d.Count; i++) { while (d[i] - d[j] > 60) j++; if (i - j + 1 > bn) { bn = i - j + 1; bi = j; } }
                            var main = d.Skip(bi).Take(bn).ToList();
                            double med = main[main.Count / 2], spread = main[^1] - main[0];
                            bool even = bn >= Math.Max(2, d.Count * 0.5);
                            // more than 300 mm off is not a slab set a little wrong but the wrong slab: reported, never moved
                            if (Math.Abs(med) > tol && Math.Abs(med) <= 300 && even) fixes.Add((floors.First(f => f.Id.Value == kv.Key), med));
                            return new
                            {
                                floor = kv.Key, spots = d.Count, onSlab = bn, medianMm = R(med), spreadMm = R(spread),
                                elsewhere = d.Count - bn, elsewhereMm = d.Where((_, i) => i < bi || i >= bi + bn).Select(R).Take(12).ToList(),
                                even, ok = Math.Abs(med) <= tol && even,
                            };
                        }).OrderByDescending(r => Math.Abs(r.medianMm)).ToList();
                        report.Add(new
                        {
                            level = level.Name, spots = spots.Count, floors = floors.Count, checkedFloors = rows.Count, passing = rows.Count(r => r.ok),
                            rows = rows.Take(15).ToList(), spotsWithNoFloor = orphans.Count, noFloorAt = orphans.Take(8).ToList(),
                        });
                    }
                    List<string> notes = null;
                    if (apply && fixes.Count > 0)
                        notes = BuildTools.Tx.Run(doc, "MCP: floors to survey levels", () =>
                        {
                            foreach (var (f, dev) in fixes)
                                if (f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM) is Parameter p && !p.IsReadOnly) p.Set(p.AsDouble() - dev / Mm);
                        });
                    return new { floors = report, wouldMove = fixes.Count, moved = apply ? fixes.Count : 0, revit = notes, next = apply ? "survey_floor_levels again to confirm." : (fixes.Count > 0 ? "apply: true to move them." : null) };
                }
            };

            // ------------------------------------------------------------------ sections on the plan
            yield return new RevitTool
            {
                Name = "section_index",
                Description = "Finds where each survey section (Section-A, Section-B...) cuts the building: its cut line on a plan DWG (the Zz_60_50_80-Sect " +
                              "line and its letters), and which way and where the drawing runs along it (matched on the walls the line crosses). Saves " +
                              "section-index.json in the reports folder for survey_ceilings and the stair and roof checks. Changes nothing.",
                InputSchema = Schema(
                    ("path", S("string", "The sections DWG (default: the DWG beside the plans whose text says Section-)"), false),
                    ("level", S("string", "The floor whose plan has the cut lines (default the lowest with a plan)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var floors = PlanFloors(doc, Args.Str(a, "level") is string ln ? new List<string> { ln } : new List<string>());
                    if (floors.Count == 0) throw new ArgumentException("No floor has a plan DWG linked into its plan view.");
                    var (level, imp) = floors[0];
                    string planPath = CadLink.FilePath(doc, imp) ?? throw new InvalidOperationException("The plan link's file can't be found.");
                    string path = Args.Str(a, "path") ?? Directory.GetFiles(Path.GetDirectoryName(planPath), "*.dwg")
                        .FirstOrDefault(f => { try { return DwgFile.Read(f).Texts.Any(t => t.Value != null && t.Value.TrimStart().StartsWith("Section-", StringComparison.OrdinalIgnoreCase)); } catch { return false; } })
                        ?? throw new InvalidOperationException("No sections DWG beside the plans; give path.");
                    var plan = DwgFile.Read(planPath);
                    var map = CadLink.FileToModel(doc, imp, plan, out string mapNote) ?? throw new InvalidOperationException("The plan can't be placed in the model: " + mapNote);
                    var walls = CadLink.Segments(CadLink.Read(doc, imp, new ReadStats()), new[] { "EF_25*", "*Wall*" });
                    var file = DwgFile.Read(path);
                    var prof = file.Lines.Where(l => DwgFile.LayerMatches(l.Layer, new[] { "*Profile*" })).ToList();
                    var outSections = new JsonArray(); var summary = new List<object>();
                    foreach (var (x0, x1, y0, y1) in ElevationFit.SheetParts(prof, 5000))
                    {
                        var part = prof.Where(l => Math.Min(l.A.X, l.B.X) >= x0 - 1 && Math.Max(l.A.X, l.B.X) <= x1 + 1).ToList();
                        if (part.Count < 20) continue;
                        string title = file.Texts.Where(t => t.Value != null && t.Value.TrimStart().StartsWith("Section", StringComparison.OrdinalIgnoreCase) && t.At.X >= x0 - 5000 && t.At.X <= x1 + 5000)
                            .OrderBy(t => Math.Abs(t.At.X - (x0 + x1) / 2)).Select(t => t.Value.Trim()).FirstOrDefault();
                        if (title == null) continue;
                        string letter = title.Split('-', ' ').Last().Trim();
                        // the cut line on the plan: section-layer lines near that letter, end to end
                        var marks = plan.Texts.Where(t => DwgFile.LayerMatches(t.Layer, new[] { "*Sect*" }) && string.Equals(t.Value?.Trim(), letter, StringComparison.OrdinalIgnoreCase)).Select(t => t.At).ToList();
                        var near = plan.Lines.Where(l => DwgFile.LayerMatches(l.Layer, new[] { "*Sect*" }) && marks.Any(m => SurveyDetail.PtSeg(m, new Seg(l.A, l.B, 0)) < 3000)).ToList();
                        var ends = near.SelectMany(l => new[] { l.A, l.B }).ToList();
                        if (marks.Count == 0 || ends.Count < 2) { summary.Add(new { section = title, problem = "no cut line found on the plan" }); continue; }
                        var (ca, cb) = (ends[0], ends[0]); double far = -1;
                        foreach (var p in ends) foreach (var q in ends) { double d = p.Dist2D(q); if (d > far) { far = d; ca = p; cb = q; } }
                        Pt A = map(ca), B = map(cb);
                        double len = A.Dist2D(B), ux = (B.X - A.X) / len, uy = (B.Y - A.Y) / len;
                        var cutSeg = new Seg(A, B, 0);
                        var crossings = new List<double>();
                        foreach (var w in walls)
                            if (Intersect(cutSeg, w) is double t) crossings.Add(t * len);
                        var verticals = part.Where(l => Math.Abs(l.A.X - l.B.X) < 2 && Math.Abs(l.A.Y - l.B.Y) > 1500).Select(l => (l.A.X + l.B.X) / 2).Distinct().ToList();
                        var place = SurveyDetail.PlaceSection(crossings, verticals);
                        var heights = ElevationFit.Heights(file.Texts.Where(t => ElevationFit.IsLevelText(t, DwgFile.LayerMatches(t.Layer, new[] { "*Level*" }))),
                                                           file.Lines.Where(l => ElevationFit.IsLevelLine(l, DwgFile.LayerMatches(l.Layer, new[] { "*Level*" }), file.Texts)), x0, x1);
                        var o = new JsonObject
                        {
                            ["name"] = title, ["letter"] = letter, ["path"] = path,
                            ["sheet_x_range"] = new JsonArray(R(x0), R(x1)), ["sheet_y_range"] = new JsonArray(R(y0), R(y1)),
                            ["cut"] = new JsonArray(R(A.X), R(A.Y), R(B.X), R(B.Y)),
                            ["dir"] = place?.Dir ?? 0, ["offset"] = place == null ? 0 : R(place.Offset),
                            ["matched"] = place?.Matched ?? 0, ["of"] = place?.Of ?? verticals.Count,
                            ["heights"] = heights.Note,
                        };
                        outSections.Add(o);
                        summary.Add(new { section = title, cutLengthM = Math.Round(len / 1000, 2), crossings = crossings.Count, verticals = verticals.Count, matched = place?.Matched, dir = place?.Dir, heights = heights.Note,
                                          trusted = place != null && place.Matched >= 4 });
                    }
                    string folder = ReportsFolder(doc), saved = Path.Combine(folder, "section-index.json");
                    var root = new JsonObject { ["when"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), ["plan"] = planPath, ["sections"] = outSections };
                    File.WriteAllText(saved, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
                    return new { sections = summary, saved, next = "survey_ceilings; check a section with matched under 4 by hand." };
                }
            };

            // ------------------------------------------------------------------ ceilings
            yield return new RevitTool
            {
                Name = "survey_ceilings",
                Write = true,
                Description = "Ceilings against the survey sections (section-index.json): along each cut line, every 500 mm, the section's ceiling (the " +
                              "lowest outline line above head height on each floor) against the model's ceiling there. Per ceiling: median and worst " +
                              "difference; places the section shows a ceiling and the model has none, grouped by room. apply moves ceilings to their " +
                              "median; create_missing makes a ceiling in each such room at the survey height. One undo step.",
                InputSchema = Schema(
                    ("tolerance_mm", S("number", "Difference that counts (default 20)"), false),
                    ("step_mm", S("number", "Sampling step along the cut lines (default 500)"), false),
                    ("apply", S("boolean", "Move ceilings to the survey (default false)"), false),
                    ("create_missing", S("boolean", "Make ceilings where the section has one and the model none (default false)"), false),
                    ("ceiling_type", S("string", "Ceiling type for new ceilings (default the first)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    double tol = Args.Dbl(a, "tolerance_mm") ?? 20, step = Math.Max(100, Args.Dbl(a, "step_mm") ?? 500), datum = Datum(doc);
                    var sections = SectionSpecs(doc);
                    var levels = StoreyLevels(doc);
                    var ceilings = new FilteredElementCollector(doc).OfClass(typeof(Ceiling)).Cast<Ceiling>().ToList();
                    var bottoms = ceilings.ToDictionary(c => c.Id.Value, c => Faces(c, top: false));
                    var per = new Dictionary<long, List<double>>();
                    var missing = new List<(Level lv, Pt p, double z, string room, long roomId)>();
                    var notes = new List<string>();
                    foreach (var s in sections)
                    {
                        var (A, B, place, horizontals, name) = s;
                        if (place.Matched < 4) { notes.Add($"{name}: not used, it does not sit on the plan well enough ({place.Matched} matched)"); continue; }
                        double len = A.Dist2D(B), ux = (B.X - A.X) / len, uy = (B.Y - A.Y) / len;
                        int samples = 0;
                        for (double t = 0; t <= len; t += step)
                        {
                            var p = new Pt(A.X + ux * t, A.Y + uy * t);
                            double x = place.Dir * t + place.Offset;
                            for (int i = 0; i + 1 < levels.Count; i++)
                            {
                                double floorAod = levels[i].Elevation * Mm + datum, nextAod = levels[i + 1].Elevation * Mm + datum;
                                if (!(SurveyDetail.Soffit(horizontals, x, floorAod, nextAod) is double soffitAod)) continue;
                                double want = soffitAod - datum;
                                samples++;
                                long hit = 0; double z = double.NaN;
                                foreach (var c in ceilings.Where(c => c.LevelId == levels[i].Id))
                                    if (ZOn(bottoms[c.Id.Value], p) is double zz) { hit = c.Id.Value; z = zz; break; }
                                if (hit != 0)
                                {
                                    if (!per.TryGetValue(hit, out var l)) per[hit] = l = new List<double>();
                                    l.Add(z - want);
                                }
                                else
                                {
                                    var room = RoomAt(doc, new XYZ(p.X / Mm, p.Y / Mm, (levels[i].Elevation * Mm + 1000) / Mm));
                                    missing.Add((levels[i], p, want, room == null ? null : $"{room.Number} {room.Name}", room?.Id.Value ?? 0));
                                }
                            }
                        }
                        notes.Add($"{name}: {samples} ceiling heights read along {len / 1000:0.0} m");
                    }
                    var rows = per.Select(kv =>
                    {
                        var d = kv.Value.OrderBy(v => v).ToList();
                        return (id: kv.Key, n: d.Count, med: d[d.Count / 2], worst: d.OrderByDescending(Math.Abs).First(), spread: d[^1] - d[0]);
                    }).OrderByDescending(r => Math.Abs(r.med)).ToList();
                    var groups = missing.GroupBy(m => (m.lv.Id.Value, m.roomId)).Select(g => new
                    {
                        level = g.First().lv, roomId = g.Key.roomId, room = g.First().room, samples = g.Count(),
                        heightMm = g.Select(m => m.z).OrderBy(v => v).ElementAt(g.Count() / 2),
                        from = new[] { R(g.First().p.X), R(g.First().p.Y) }, to = new[] { R(g.Last().p.X), R(g.Last().p.Y) },
                    }).OrderByDescending(g => g.samples).ToList();

                    bool apply = Args.Bool(a, "apply") == true, create = Args.Bool(a, "create_missing") == true;
                    var made = new List<long>(); List<string> revit = null;
                    var moves = rows.Where(r => Math.Abs(r.med) > tol && r.spread <= 50).ToList();
                    if ((apply && moves.Count > 0) || create)
                        revit = BuildTools.Tx.Run(doc, "MCP: ceilings to survey", () =>
                        {
                            if (apply)
                                foreach (var r in moves)
                                    if (doc.GetElement(new ElementId(r.id)) is Ceiling c && c.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM) is Parameter p && !p.IsReadOnly)
                                        p.Set(p.AsDouble() - r.med / Mm);
                            if (create)
                            {
                                var type = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<CeilingType>()
                                    .FirstOrDefault(t => Args.Str(a, "ceiling_type") == null || t.Name.Equals(Args.Str(a, "ceiling_type"), StringComparison.OrdinalIgnoreCase))
                                    ?? throw new InvalidOperationException("No ceiling type " + Args.Str(a, "ceiling_type"));
                                foreach (var g in groups.Where(g => g.roomId != 0 && g.samples >= 2))
                                {
                                    if (!(doc.GetElement(new ElementId(g.roomId)) is Room room)) continue;
                                    var loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions()).Select(segs =>
                                    {
                                        var cl = new CurveLoop();
                                        foreach (var bs in segs) cl.Append(bs.GetCurve());
                                        return cl;
                                    }).ToList();
                                    if (loops.Count == 0) continue;
                                    var ceil = Ceiling.Create(doc, loops, type.Id, g.level.Id);
                                    ceil.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.Set((g.heightMm - g.level.Elevation * Mm) / Mm);
                                    ceil.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("MCP|survey_ceilings|from the sections");
                                    made.Add(ceil.Id.Value);
                                }
                            }
                        });
                    return new
                    {
                        checkedCeilings = rows.Count, passing = rows.Count(r => Math.Abs(r.med) <= tol),
                        ceilings = rows.Take(20).Select(r => new { ceiling = r.id, samples = r.n, medianMm = R(r.med), worstMm = R(r.worst), spreadMm = R(r.spread) }).ToList(),
                        missing = groups.Take(20).Select(g => new { level = g.level.Name, g.room, g.samples, surveyHeightAboveLevelMm = R(g.heightMm - g.level.Elevation * Mm), g.from, g.to }).ToList(),
                        missingPlaces = missing.Count,
                        moved = apply ? moves.Count : 0, created = made, revit, sections = notes,
                    };
                }
            };

            // ------------------------------------------------------------------ stairs
            yield return new RevitTool
            {
                Name = "survey_stairs",
                Description = "Stairs against the plan DWGs: each flight on the stair layer (EF_35_10-Stair) read as treads (count, going, width) and matched to " +
                              "the model's stair runs on that floor. Reports differences, flights the model lacks, and the riser height the storey and " +
                              "tread count give. Changes nothing (stairs are fixed by hand or rebuilt from these numbers).",
                InputSchema = Schema(("levels", Arr("string", "Floors (default all with a plan)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var levels = StoreyLevels(doc);
                    var stairs = new FilteredElementCollector(doc).OfClass(typeof(Stairs)).Cast<Stairs>().ToList();
                    var report = new List<object>();
                    foreach (var (level, imp) in PlanFloors(doc, Args.Strings(a, "levels")))
                    {
                        var segs = CadLink.Segments(CadLink.Read(doc, imp, new ReadStats()), new[] { "*Stair*" });
                        var flights = SurveyDetail.Stairs(segs);
                        int li = levels.FindIndex(l => l.Id == level.Id);
                        double rise = li >= 0 && li + 1 < levels.Count ? (levels[li + 1].Elevation - level.Elevation) * Mm : double.NaN;
                        var rows = new List<object>(); int missingHere = 0;
                        foreach (var g in flights)
                        {
                            var hits = new List<object>();
                            foreach (var st in stairs)
                            {
                                // 1.14.0: a plan shows the flight arriving at its floor too (the top floor's plan shows the flight up from the
                                // floor below), so a stair that ends at this level counts; before, its top had to be above the level
                                // and the top floor's flights all read as missing
                                double baseMm = st.BaseElevation * Mm, topMm = st.TopElevation * Mm, lvMm = level.Elevation * Mm;
                                if (baseMm > lvMm + 1500 || topMm < lvMm - 50 || (baseMm < lvMm - 1500 && topMm < lvMm - 50)) continue;
                                foreach (var rid in st.GetStairsRuns())
                                {
                                    if (!(doc.GetElement(rid) is StairsRun run) || !(run.get_BoundingBox(null) is BoundingBoxXYZ bb)) continue;
                                    if (bb.Max.X * Mm < g.Min.X - 300 || bb.Min.X * Mm > g.Max.X + 300 || bb.Max.Y * Mm < g.Min.Y - 300 || bb.Min.Y * Mm > g.Max.Y + 300) continue;
                                    hits.Add(new
                                    {
                                        stairs = st.Id.Value, run = run.Id.Value, treads = run.ActualTreadsNumber, risers = run.ActualRisersNumber,
                                        goingMm = R(st.ActualTreadDepth * Mm), riserMm = R(st.ActualRiserHeight * Mm), widthMm = R(run.ActualRunWidth * Mm),
                                        treadsOff = run.ActualTreadsNumber - g.Treads, goingOffMm = R(st.ActualTreadDepth * Mm - g.GoingMm), widthOffMm = R(run.ActualRunWidth * Mm - g.WidthMm),
                                    });
                                }
                            }
                            if (hits.Count == 0) missingHere++;
                            rows.Add(new
                            {
                                survey = new { treads = g.Treads, goingMm = g.GoingMm, widthMm = g.WidthMm, box = new[] { R(g.Min.X), R(g.Min.Y), R(g.Max.X), R(g.Max.Y) } },
                                model = hits.Count > 0 ? hits : null,
                                missing = hits.Count == 0,
                            });
                        }
                        report.Add(new { level = level.Name, storeyRiseMm = double.IsNaN(rise) ? (double?)null : R(rise), flights = rows.Count, missing = missingHere, rows });
                    }
                    return new { floors = report, note = "Treads are what the plan shows on that floor; a flight cut by the plan's cut plane shows only its lower treads." };
                }
            };

            // ------------------------------------------------------------------ columns and piers
            yield return new RevitTool
            {
                Name = "plan_columns",
                Write = true,
                Description = "Free-standing columns and piers on each plan DWG: small closed outlines on the wall layer that touch nothing else (shopfront " +
                              "columns, piers, stacks). Says which the model already has (a column, or a wall through it) and which it lacks; build: true " +
                              "makes the missing ones as solids (DirectShape, Columns by default) from the floor to the next level. One undo step.",
                InputSchema = Schema(
                    ("levels", Arr("string", "Floors (default all with a plan)"), false),
                    ("layers", Arr("string", "Layers (default EF_25*)"), false),
                    ("max_area_m2", S("number", "Largest outline (default 2.5)"), false),
                    ("max_side_mm", S("number", "Longest side (default 1600)"), false),
                    ("category", S("string", "Columns (default), Structural Columns or Generic Models"), false),
                    ("build", S("boolean", "Make the missing ones (default false)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var layers = Args.Strings(a, "layers"); if (layers.Count == 0) layers = new List<string> { "EF_25*" };
                    var levels = StoreyLevels(doc);
                    var cat = (Args.Str(a, "category") ?? "Columns").ToLowerInvariant() switch
                    {
                        "structural columns" => BuiltInCategory.OST_StructuralColumns,
                        "generic models" => BuiltInCategory.OST_GenericModel,
                        _ => BuiltInCategory.OST_Columns,
                    };
                    var found = new List<(Level lv, List<Pt> ring, bool modelled, string by)>();
                    foreach (var (level, imp) in PlanFloors(doc, Args.Strings(a, "levels")))
                    {
                        var segs = CadLink.Segments(CadLink.Read(doc, imp, new ReadStats()), layers);
                        var rings = SurveyDetail.Columns(segs, 0.02, Args.Dbl(a, "max_area_m2") ?? 2.5, Args.Dbl(a, "max_side_mm") ?? 1600);
                        var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.LevelId == level.Id && w.Location is LocationCurve).ToList();
                        var cols = new FilteredElementCollector(doc).WherePasses(new LogicalOrFilter(new List<ElementFilter>
                            { new ElementCategoryFilter(BuiltInCategory.OST_Columns), new ElementCategoryFilter(BuiltInCategory.OST_StructuralColumns) }))
                            .WhereElementIsNotElementType().ToList();
                        double z = (level.Elevation * Mm) + 1000;
                        foreach (var r in rings)
                        {
                            var c = new Pt(r.Average(p => p.X), r.Average(p => p.Y));
                            string by = null;
                            foreach (var col in cols)
                                if (col.get_BoundingBox(null) is BoundingBoxXYZ bb && bb.Min.X * Mm <= c.X && bb.Max.X * Mm >= c.X && bb.Min.Y * Mm <= c.Y && bb.Max.Y * Mm >= c.Y && bb.Min.Z * Mm <= z && bb.Max.Z * Mm >= z)
                                { by = "column " + col.Id.Value; break; }
                            if (by == null)
                                foreach (var w in walls)
                                {
                                    var lc = ((LocationCurve)w.Location).Curve;
                                    var pr = lc.Project(new XYZ(c.X / Mm, c.Y / Mm, lc.GetEndPoint(0).Z));
                                    if (pr != null && pr.Distance * Mm <= w.Width * Mm / 2 + 30) { by = "wall " + w.Id.Value; break; }
                                }
                            found.Add((level, r, by != null, by));
                        }
                    }
                    var made = new List<long>(); List<string> revit = null;
                    if (Args.Bool(a, "build") == true && found.Any(f => !f.modelled))
                        revit = BuildTools.Tx.Run(doc, "MCP: columns from plans", () =>
                        {
                            int n = 0;
                            foreach (var f in found.Where(f => !f.modelled))
                            {
                                int li = levels.FindIndex(l => l.Id == f.lv.Id);
                                double z0 = f.lv.Elevation * Mm, z1 = li >= 0 && li + 1 < levels.Count ? levels[li + 1].Elevation * Mm : z0 + 3000;
                                var solid = Prism(f.ring, z0, z1 - z0);
                                if (solid == null) continue;
                                made.Add(Shape(doc, cat, solid, $"Column {f.lv.Name} {++n}", $"MCP|plan_columns|{f.lv.Name}"));
                            }
                        });
                    return new
                    {
                        found = found.Count, modelled = found.Count(f => f.modelled), missing = found.Count(f => !f.modelled),
                        list = found.Take(40).Select(f =>
                        {
                            GeoMath.BoundingBox(f.ring, out var mn, out var mx);
                            return new { level = f.lv.Name, at = new[] { R((mn.X + mx.X) / 2), R((mn.Y + mx.Y) / 2) }, sizeMm = new[] { R(mx.X - mn.X), R(mx.Y - mn.Y) }, areaM2 = Math.Round(Math.Abs(GeoMath.SignedArea(f.ring)) / 1e6, 3), f.by };
                        }).ToList(),
                        built = made, revit,
                    };
                }
            };

            // ------------------------------------------------------------------ roof
            yield return new RevitTool
            {
                Name = "roof_from_survey",
                Write = true,
                Description = "The roof from the survey roof plan: its line work (roof, parapet walls, chimneys, rooflights) split into faces, each face's " +
                              "spot heights (metres AOD) fitted with a plane (flat with one or two spots, sloped with three or more), chimneys as stacks " +
                              "up to their top spot. Reports every face (area, slope, spots, fit) and what has no height. build: true makes each face as " +
                              "a solid of the given thickness under the surveyed surface (DirectShape, Roofs) and the chimneys. The roof plan need not be " +
                              "linked: it is placed by the survey coordinates and checked against the model's walls. One undo step.",
                InputSchema = Schema(
                    ("path", S("string", "Roof plan DWG (default: the one beside the plans with the most roof lines)"), false),
                    ("thickness_mm", S("number", "Roof build-up under the surveyed surface (default 150)"), false),
                    ("min_area_m2", S("number", "Smallest face to make (default 0.3)"), false),
                    ("roof_layers", Arr("string", "Roof line layers (default: by Survey to Revit's layer roles, Roof/Wall on the roof plan)"), false),
                    ("merge_spotless", S("boolean", "Merge faces with no spot height into their neighbour across the seam (default true)"), false),
                    ("edge_spot_mm", S("number", "A face with no spot inside takes the spots on its edges within this (hips, ridge, eaves; default 400)"), false),
                    ("build", S("boolean", "Make the faces and chimneys (default false)"), false),
                    ("list", S("integer", "Faces to list (default 30)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    double datum = Datum(doc), thick = Args.Dbl(a, "thickness_mm") ?? 150, minArea = (Args.Dbl(a, "min_area_m2") ?? 0.3) * 1e6;
                    string path = Args.Str(a, "path") ?? FindRoofPlan(doc) ?? throw new InvalidOperationException("No roof plan found beside the plans; give path.");
                    var file = DwgFile.Read(path);
                    var rl = RoofLayers(app, file, path, a);
                    var map = SurveyMap(doc, file, out string mapNote, rl.Wall);
                    Seg M(DwgLine l) => new Seg(map(l.A), map(l.B), 0);
                    var segs = file.Lines.Where(l => l.Layer != null && rl.Roof(l.Layer)).Select(M).ToList();
                    var chimneys = LoopBuilder.Build(file.Lines.Where(l => l.Layer != null && rl.Chimney(l.Layer)).Select(M).ToList(), 10).Loops;
                    var lights = LoopBuilder.Build(file.Lines.Where(l => l.Layer != null && rl.Light(l.Layer)).Select(M).ToList(), 10).Loops;
                    var spots = file.Texts.Where(t => t.Layer != null && rl.Spot(t.Layer) && ElevationFit.LevelValueMm(t.Value) is double)
                        .Select(t => { var p = map(t.At); return new Pt(p.X, p.Y, ElevationFit.LevelValueMm(t.Value).Value); }).ToList();
                    var faces = RoofFaces(segs, spots, minArea / 3, Args.Bool(a, "merge_spotless") != false, out int seams);
                    mapNote += $"; {rl.Note}; {seams} seam lines between spotless strips dropped";
                    var bySpot = SpotsByFace(faces, spots, Args.Dbl(a, "edge_spot_mm") ?? 400);
                    // each face: a plane through its spots; flat roofs at several levels inside one outline (the plane fits
                    // badly) as terraces; a face with no spot takes the nearest spot within 2 m (an upstand, a light)
                    var rows = new List<(int i, List<Pt> ring, PlaneZ plane, string kind, string how, List<(double z, List<(double x0, double y0, double x1, double y1)> rects)> terraces)>();
                    for (int i = 0; i < faces.Count; i++)
                    {
                        var c = new Pt(faces[i].Average(p => p.X), faces[i].Average(p => p.Y));
                        string kind = chimneys.Any(r => GeoMath.PointInPolygon(c, r)) ? "chimney" : lights.Any(r => GeoMath.PointInPolygon(c, r)) ? "rooflight" : "roof";
                        bySpot.TryGetValue(i, out var ss);
                        var plane = ss != null ? SurveyDetail.FitPlane(ss) : null;
                        string how = plane == null ? null : plane.Flat ? "flat" : "sloped";
                        List<(double, List<(double, double, double, double)>)> ter = null;
                        if (plane != null && plane.RmsMm > 40 && kind != "chimney") { ter = SurveyDetail.Terraces(faces[i], ss); how = $"terraces ({ter.Count} levels)"; }
                        if (plane == null && kind != "chimney")
                        {
                            var near = spots.Select(s => (s, d: faces[i].Min(v => v.Dist2D(s)))).Where(x => x.d <= 2000).OrderBy(x => x.d).Select(x => (Pt?)x.s).FirstOrDefault();
                            if (near is Pt n) { plane = new PlaneZ { C = n.Z, Spots = 0 }; how = "from a spot nearby"; }
                        }
                        rows.Add((i, faces[i], plane, kind, how, ter));
                    }
                    var made = new List<long>(); var problems = new List<string>(); List<string> revit = null;
                    if (Args.Bool(a, "build") == true)
                        revit = BuildTools.Tx.Run(doc, "MCP: roof from survey", () =>
                        {
                            foreach (var r in rows.Where(r => r.plane != null && Math.Abs(GeoMath.SignedArea(r.ring)) >= minArea))
                            {
                                try
                                {
                                    var g = new List<GeometryObject>();
                                    if (r.kind == "chimney")
                                    {
                                        // from the roof around it up to its top spot
                                        var c = new Pt(r.ring.Average(p => p.X), r.ring.Average(p => p.Y));
                                        var around = spots.Where(s => s.Dist2D(c) < 3000 && !GeoMath.PointInPolygon(s, r.ring)).Select(s => s.Z).DefaultIfEmpty(r.plane.C - 1500).Min();
                                        double top = spots.Where(s => GeoMath.PointInPolygon(s, r.ring)).Max(s => s.Z);
                                        g.Add(Prism(r.ring, around - datum, top - around));
                                    }
                                    else if (r.terraces != null)
                                        foreach (var (z, rects) in r.terraces)
                                            foreach (var q in rects)
                                                g.Add(Prism(new List<Pt> { new Pt(q.x0, q.y0), new Pt(q.x1, q.y0), new Pt(q.x1, q.y1), new Pt(q.x0, q.y1) }, z - datum - thick, thick));
                                    else g.Add(Slab(r.ring, r.plane, datum, thick));
                                    g.RemoveAll(x => x == null);
                                    if (g.Count > 0) made.Add(Shape(doc, BuiltInCategory.OST_Roofs, g, $"Roof {r.kind} R{r.i}", $"MCP|roof_from_survey|{r.kind} R{r.i}: {r.how}, {r.plane.Spots} spots, rms {r.plane.RmsMm:0} mm"));
                                }
                                catch (Exception ex) { problems.Add($"R{r.i}: {McpServer.Unwrap(ex).Message}"); }
                            }
                        });
                    int list = Args.Int(a, "list", 30);
                    return new
                    {
                        path, placed = mapNote, faces = rows.Count, withHeights = rows.Count(r => r.plane != null), spots = spots.Count,
                        flat = rows.Count(r => r.plane != null && r.plane.Flat), sloped = rows.Count(r => r.plane != null && !r.plane.Flat),
                        chimneys = rows.Count(r => r.kind == "chimney"), rooflights = rows.Count(r => r.kind == "rooflight"),
                        terraced = rows.Count(r => r.terraces != null), inferred = rows.Count(r => r.how == "from a spot nearby"),
                        noHeight = rows.Count(r => r.plane == null), noHeightAreaM2 = Math.Round(rows.Where(r => r.plane == null).Sum(r => Math.Abs(GeoMath.SignedArea(r.ring))) / 1e6, 1),
                        list = rows.OrderByDescending(r => Math.Abs(GeoMath.SignedArea(r.ring))).Take(list).Select(r => new
                        {
                            face = "R" + r.i, r.kind, r.how, levels = r.terraces?.Select(t => Math.Round(t.z / 1000, 2)).ToList(), areaM2 = Math.Round(Math.Abs(GeoMath.SignedArea(r.ring)) / 1e6, 2),
                            spots = r.plane?.Spots ?? 0, slopeDeg = r.plane == null ? (double?)null : Math.Round(r.plane.SlopeDeg, 1),
                            aodM = r.plane == null ? (double?)null : Math.Round(r.plane.At(r.ring.Average(p => p.X), r.ring.Average(p => p.Y)) / 1000, 3),
                            rmsMm = r.plane == null ? (double?)null : R(r.plane.RmsMm),
                        }).ToList(),
                        built = made.Count, ids = made.Take(100).ToList(), problems = problems.Take(20).ToList(), revit,
                    };
                }
            };

            // ------------------------------------------------------------------ windows: inset
            yield return new RevitTool
            {
                Name = "window_detail",
                Write = true,
                Description = "Window depth in the wall against the plan DWGs: for each window, the frame's outer line on the plan's window layer, measured " +
                              "in from the wall's outer face (the inset; the colleague's sash families carry it as Inset). Reports the survey inset " +
                              "against the window's own parameter; apply: true sets it. One undo step.",
                InputSchema = Schema(
                    ("levels", Arr("string", "Floors (default all with a plan)"), false),
                    ("inset_param", S("string", "The window parameter that holds the inset (default Inset)"), false),
                    ("tolerance_mm", S("number", "Difference that counts (default 15)"), false),
                    ("apply", S("boolean", "Set the parameter (default false)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    string pname = Args.Str(a, "inset_param") ?? "Inset";
                    double tol = Args.Dbl(a, "tolerance_mm") ?? 15;
                    var rows = new List<(FamilyInstance fi, double inset, double? now, bool settable)>();
                    int noLine = 0;
                    foreach (var (level, imp) in PlanFloors(doc, Args.Strings(a, "levels")))
                    {
                        var segs = CadLink.Segments(CadLink.Read(doc, imp, new ReadStats()), new[] { "*Window*" });
                        foreach (var fi in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType().OfType<FamilyInstance>())
                        {
                            if (fi.LevelId != level.Id || !(fi.Host is Wall w) || !(w.Location is LocationCurve lc) || !(fi.Location is LocationPoint lp)) continue;
                            var dir = (lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0)).Normalize();
                            var outN = w.Orientation; // towards the outside
                            if (!(OuterFace(w) is PlanarFace face)) continue;
                            var p = lp.Point; double width = WidthOf(fi);
                            double best = double.NaN;
                            foreach (var s in segs)
                            {
                                var sa = new XYZ(s.A.X / Mm, s.A.Y / Mm, p.Z); var sb = new XYZ(s.B.X / Mm, s.B.Y / Mm, p.Z);
                                var sd = (sb - sa); if (sd.GetLength() * Mm < 200) continue;
                                if (Math.Abs(sd.Normalize().DotProduct(dir)) < 0.996) continue; // parallel to the wall
                                var mid = (sa + sb) / 2;
                                if (Math.Abs((mid - p).DotProduct(dir)) * Mm > width / 2) continue; // within the opening
                                double inFromFace = -(mid - face.Origin).DotProduct(outN) * Mm; // positive = inside the outer face
                                if (inFromFace < 20 || inFromFace > w.Width * Mm + 50) continue;
                                if (double.IsNaN(best) || inFromFace < best) best = inFromFace;
                            }
                            if (double.IsNaN(best)) { noLine++; continue; }
                            var prm = fi.LookupParameter(pname) ?? doc.GetElement(fi.GetTypeId())?.LookupParameter(pname);
                            rows.Add((fi, best, prm != null && prm.StorageType == StorageType.Double ? prm.AsDouble() * Mm : (double?)null, prm != null && !prm.IsReadOnly && prm.Element is FamilyInstance));
                        }
                    }
                    var off = rows.Where(r => r.now == null || Math.Abs(r.now.Value - r.inset) > tol).ToList();
                    List<string> revit = null; int set = 0;
                    if (Args.Bool(a, "apply") == true)
                        revit = BuildTools.Tx.Run(doc, "MCP: window insets from survey", () =>
                        {
                            foreach (var r in off.Where(r => r.settable)) { r.fi.LookupParameter(pname).Set(Math.Round(r.inset) / Mm); set++; }
                        });
                    return new
                    {
                        measured = rows.Count, noFrameLineFound = noLine, differ = off.Count, withoutParameter = rows.Count(r => r.now == null),
                        medianInsetMm = rows.Count == 0 ? (double?)null : R(rows.Select(r => r.inset).OrderBy(v => v).ElementAt(rows.Count / 2)),
                        worst = off.OrderByDescending(r => r.now == null ? 0 : Math.Abs(r.now.Value - r.inset)).Take(20)
                            .Select(r => new { window = r.fi.Id.Value, type = r.fi.Symbol?.Name, surveyMm = R(r.inset), modelMm = r.now == null ? (double?)null : R(r.now.Value) }).ToList(),
                        set, revit,
                        note = rows.Count(r => r.now == null) > 0 ? $"Windows without a {pname} parameter need a sash family that has one (reference_copy types_only from the colleague's model, then change_type)." : null,
                    };
                }
            };

            // ------------------------------------------------------------------ merged openings
            yield return new RevitTool
            {
                Name = "split_opening",
                Write = true,
                Description = "Wide windows that are really several: Survey to Revit can merge a row of sashes and the piers between them into one " +
                              "opening. For each window wider than min_width_mm, the plan DWG's reveal lines across the wall inside it are paired into " +
                              "piers and the opening is split at them. Reports what it would make; apply: true reshapes the window to the first part " +
                              "and places the others (same family, height and sill). One undo step.",
                InputSchema = Schema(
                    ("levels", Arr("string", "Floors (default all with a plan)"), false),
                    ("window_ids", Arr("integer", "Only these windows"), false),
                    ("min_width_mm", S("number", "Only windows wider than this (default 1800)"), false),
                    ("apply", S("boolean", "Split them (default false)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    double minW = Args.Dbl(a, "min_width_mm") ?? 1800;
                    var only = Args.Numbers(a, "window_ids").Select(d => (long)d).ToHashSet();
                    var plans = new List<(FamilyInstance fi, Wall w, XYZ dir, List<(double a, double b)> parts, double width)>();
                    foreach (var (level, imp) in PlanFloors(doc, Args.Strings(a, "levels")))
                    {
                        var segs = CadLink.Segments(CadLink.Read(doc, imp, new ReadStats()), new[] { "EF_25*", "*Wall*" });
                        foreach (var fi in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType().OfType<FamilyInstance>())
                        {
                            if (fi.LevelId != level.Id || (only.Count > 0 && !only.Contains(fi.Id.Value))) continue;
                            if (!(fi.Host is Wall w) || !(w.Location is LocationCurve lc) || !(fi.Location is LocationPoint lp)) continue;
                            double width = WidthOf(fi);
                            if (width <= minW) continue;
                            var dir = (lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0)).Normalize();
                            var nrm = new XYZ(-dir.Y, dir.X, 0);
                            var p = lp.Point; double half = w.Width * Mm / 2;
                            var reveals = new List<double>();
                            foreach (var s in segs)
                            {
                                var sa = new XYZ(s.A.X / Mm, s.A.Y / Mm, p.Z); var sb = new XYZ(s.B.X / Mm, s.B.Y / Mm, p.Z);
                                var d = sb - sa; double len = d.GetLength() * Mm;
                                if (len < 40 || len > w.Width * Mm + 400 || Math.Abs(d.Normalize().DotProduct(dir)) > 0.1) continue; // across the wall
                                var mid = (sa + sb) / 2;
                                if (Math.Abs((mid - p).DotProduct(nrm)) * Mm > half + 200) continue;
                                reveals.Add((mid - p).DotProduct(dir) * Mm);
                            }
                            var parts = SurveyDetail.SplitSpan(-width / 2, width / 2, reveals);
                            if (parts.Count > 1) plans.Add((fi, w, dir, parts, width));
                        }
                    }
                    var made = new List<object>(); var problems = new List<string>(); List<string> revit = null;
                    if (Args.Bool(a, "apply") == true && plans.Count > 0)
                        revit = BuildTools.Tx.Run(doc, "MCP: split merged windows", () =>
                        {
                            foreach (var (fi, w, dir, parts, width) in plans)
                            {
                                try
                                {
                                    var p = ((LocationPoint)fi.Location).Point;
                                    var (_, hh) = BuildTools.InsertSizeOf(doc, fi);
                                    double sill = LenOf(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) ?? 900, head = LenOf(fi, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM) ?? sill + 1500;
                                    double h = hh is double hv && hv > 0 ? hv : head - sill;
                                    XYZ At((double a, double b) s) => p + dir * ((s.a + s.b) / 2 / Mm);
                                    var ids = new List<long> { fi.Id.Value };
                                    BuildTools.ReshapeInsert(doc, fi, parts[0].b - parts[0].a, h, null, At(parts[0]));
                                    foreach (var s in parts.Skip(1))
                                    {
                                        var at = At(s);
                                        var it = new JsonObject
                                        {
                                            ["category"] = "Windows", ["x"] = at.X * Mm, ["y"] = at.Y * Mm, ["wall_id"] = w.Id.Value,
                                            ["width_mm"] = Math.Round(s.b - s.a), ["height_mm"] = Math.Round(h), ["sill_mm"] = Math.Round(sill),
                                            ["family"] = fi.Symbol.FamilyName, ["comments"] = $"MCP|split_opening|from {fi.Id.Value}",
                                        };
                                        var r = BuildTools.PlaceInsertHere(doc, it);
                                        ids.Add((long)r.GetType().GetProperty("id").GetValue(r));
                                    }
                                    made.Add(new { from = fi.Id.Value, windows = ids });
                                }
                                catch (Exception ex) { problems.Add($"{fi.Id.Value}: {McpServer.Unwrap(ex).Message}"); }
                            }
                        });
                    return new
                    {
                        wouldSplit = plans.Count,
                        list = plans.Take(30).Select(x => new { window = x.fi.Id.Value, widthMm = R(x.width), into = x.parts.Select(s => R(s.b - s.a)).ToList() }).ToList(),
                        made, problems = problems.Take(20).ToList(), revit,
                        next = Args.Bool(a, "apply") == true ? "survey_fidelity to see the openings score." : "apply: true to split them (with testmcp active).",
                    };
                }
            };

            // ------------------------------------------------------------------ placing loaded families
            yield return new RevitTool
            {
                Name = "place_family",
                Write = true,
                Description = "Place instances of a loaded family that needs no host (pilasters, consoles, surrounds, chimney pots: e.g. the colleague's " +
                              "EF_25_10-Wall_Ext_Detail families brought in with reference_copy types_only) at given points, turned to face a direction, " +
                              "with instance parameters set (lengths in mm). dry_run (default true) checks everything first. One undo step.",
                InputSchema = Schema(
                    ("family", S("string", "Family name"), true),
                    ("type", S("string", "Type name (default the first)"), false),
                    ("items", Arr("object", "{x, y, z (mm, model), level?, rotation_deg? | facing?: [dx, dy], params?: {name: value}}"), true),
                    ("dry_run", S("boolean", "Only check (default true)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    string fam = Args.Str(a, "family"), tn = Args.Str(a, "type");
                    var symbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                        .Where(s => s.FamilyName.Equals(fam, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Name)
                        .FirstOrDefault(s => tn == null || s.Name.Equals(tn, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"No loaded family \"{fam}\"" + (tn != null ? $" with type \"{tn}\"" : "") + ". reference_copy types_only brings one from a reference model.");
                    var items = (a["items"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                    if (items.Count == 0) throw new ArgumentException("items are needed.");
                    var levels = StoreyLevels(doc);
                    var plan = new List<(XYZ p, Level lv, double rot, JsonObject prm)>();
                    foreach (var it in items)
                    {
                        double x = Args.Dbl(it, "x") ?? throw new ArgumentException("each item needs x, y, z"), y = Args.Dbl(it, "y") ?? 0, z = Args.Dbl(it, "z") ?? 0;
                        var lv = Args.Str(it, "level") is string ln ? levels.FirstOrDefault(l => l.Name.Equals(ln, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("No level " + ln)
                                 : levels.LastOrDefault(l => l.Elevation * Mm <= z + 1) ?? levels.First();
                        var f = Args.Numbers(it, "facing");
                        double rot = f.Count == 2 ? Math.Atan2(f[1], f[0]) - Math.PI / 2 : (Args.Dbl(it, "rotation_deg") ?? 0) * Math.PI / 180;
                        plan.Add((new XYZ(x / Mm, y / Mm, z / Mm), lv, rot, it["params"] as JsonObject));
                    }
                    if (Args.Bool(a, "dry_run") != false)
                        return new { dryRun = true, family = symbol.FamilyName, type = symbol.Name, placement = symbol.Family.FamilyPlacementType.ToString(), items = plan.Count };
                    var ids = new List<long>(); var problems = new List<string>();
                    var notes = BuildTools.Tx.Run(doc, $"MCP: place {symbol.FamilyName}", () =>
                    {
                        if (!symbol.IsActive) symbol.Activate();
                        foreach (var (p, lv, rot, prm) in plan)
                        {
                            var fi = doc.Create.NewFamilyInstance(new XYZ(p.X, p.Y, lv.Elevation), symbol, lv, StructuralType.NonStructural);
                            var off = fi.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM) ?? fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                            if (off != null && !off.IsReadOnly) off.Set(p.Z - lv.Elevation);
                            if (Math.Abs(rot) > 1e-9) ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(new XYZ(p.X, p.Y, 0), new XYZ(p.X, p.Y, 1)), rot);
                            if (prm != null)
                                foreach (var kv in prm)
                                {
                                    var par = fi.LookupParameter(kv.Key);
                                    if (par == null || par.IsReadOnly) { problems.Add($"{fi.Id.Value}: no settable parameter {kv.Key}"); continue; }
                                    string v = kv.Value?.ToString() ?? "";
                                    bool ok = par.StorageType == StorageType.Double && double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d)
                                        ? par.Set(d / Mm) : par.StorageType == StorageType.String ? par.Set(v) : par.SetValueString(v);
                                    if (!ok) problems.Add($"{fi.Id.Value}: {kv.Key} not set to {v}");
                                }
                            fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("MCP|place_family");
                            ids.Add(fi.Id.Value);
                        }
                    });
                    return new { placed = ids.Count, ids, problems = problems.Take(20).ToList(), revit = notes, undo = "One undo step in Revit." };
                }
            };
        }

        // ================================================================ helpers

        /// <summary>The room at a point in any phase, latest first (a survey model's rooms are often all Existing).</summary>
        private static Room RoomAt(Document doc, XYZ p)
        {
            for (int i = doc.Phases.Size - 1; i >= 0; i--)
                if (doc.GetRoomAtPoint(p, doc.Phases.get_Item(i) as Phase) is Room r) return r;
            return null;
        }

        private static double Datum(Document doc) => doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;
        private static double R(double v) => Math.Round(v, 1);

        private static List<Level> StoreyLevels(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();

        /// <summary>Floors with a plan DWG linked into one of their plan views, lowest first.</summary>
        private static List<(Level level, ImportInstance imp)> PlanFloors(Document doc, List<string> wanted)
        {
            var plans = CadLink.All(doc).Where(i => i.ViewSpecific && doc.GetElement(i.OwnerViewId) is ViewPlan vp && vp.GenLevel != null)
                .GroupBy(i => ((ViewPlan)doc.GetElement(i.OwnerViewId)).GenLevel.Id.Value).ToDictionary(g => g.Key, g => g.First());
            return StoreyLevels(doc).Where(l => plans.ContainsKey(l.Id.Value) && (wanted.Count == 0 || wanted.Any(w => w.Equals(l.Name, StringComparison.OrdinalIgnoreCase))))
                .Select(l => (l, plans[l.Id.Value])).ToList();
        }

        /// <summary>A plan's spot heights placed in the model (mm), with their value (mm above datum).</summary>
        private static List<(Pt p, double aod)> PlanSpots(Document doc, ImportInstance imp)
        {
            var result = new List<(Pt, double)>();
            string path = CadLink.FilePath(doc, imp);
            if (path == null || !File.Exists(path)) return result;
            var file = DwgFile.Read(path);
            var map = CadLink.FileToModel(doc, imp, file, out _);
            if (map == null) return result;
            foreach (var t in file.Texts.Where(t => ElevationFit.IsLevelText(t, DwgFile.LayerMatches(t.Layer, new[] { "*Level*" }))))
                if (ElevationFit.LevelValueMm(t.Value) is double v) result.Add((map(t.At), v));
            return result;
        }

        /// <summary>The planar top (or bottom) faces of a floor, ceiling or roof.</summary>
        private static List<PlanarFace> Faces(HostObject h, bool top)
        {
            var list = new List<PlanarFace>();
            try
            {
                foreach (var r in top ? HostObjectUtils.GetTopFaces(h) : HostObjectUtils.GetBottomFaces(h))
                    if (h.GetGeometryObjectFromReference(r) is PlanarFace pf && Math.Abs(pf.FaceNormal.Z) > 0.2) list.Add(pf);
            }
            catch { }
            return list;
        }

        /// <summary>The height (mm) of the first face that lies over or under a plan point, or null.</summary>
        private static double? ZOn(List<PlanarFace> faces, Pt p)
        {
            foreach (var f in faces)
            {
                var n = f.FaceNormal; var o = f.Origin;
                double x = p.X / Mm, y = p.Y / Mm;
                double z = o.Z - (n.X * (x - o.X) + n.Y * (y - o.Y)) / n.Z;
                var ir = f.Project(new XYZ(x, y, z));
                if (ir != null && ir.Distance < 0.005) return z * Mm;
            }
            return null;
        }

        private static PlanarFace OuterFace(Wall w)
        {
            try
            {
                foreach (var r in HostObjectUtils.GetSideFaces(w, ShellLayerType.Exterior))
                    if (w.GetGeometryObjectFromReference(r) is PlanarFace pf) return pf;
            }
            catch { }
            return null;
        }

        /// <summary>Where a segment crosses the cut line, as a fraction along the cut line.</summary>
        private static double? Intersect(Seg cut, Seg s)
        {
            double rx = cut.B.X - cut.A.X, ry = cut.B.Y - cut.A.Y, sx = s.B.X - s.A.X, sy = s.B.Y - s.A.Y;
            double den = rx * sy - ry * sx;
            if (Math.Abs(den) < 1e-9) return null;
            double qx = s.A.X - cut.A.X, qy = s.A.Y - cut.A.Y;
            double t = (qx * sy - qy * sx) / den, u = (qx * ry - qy * rx) / den;
            return t >= 0 && t <= 1 && u >= 0 && u <= 1 ? t : (double?)null;
        }

        /// <summary>The indexed sections: cut line ends in the model, placement, and outline horizontals (x0, x1, height AOD mm).</summary>
        private static List<(Pt A, Pt B, SectionPlacement place, List<(double x0, double x1, double z)> horizontals, string name)> SectionSpecs(Document doc)
        {
            string file = Path.Combine(ReportsFolder(doc), "section-index.json");
            if (!File.Exists(file)) throw new InvalidOperationException("No section-index.json yet: run section_index first.");
            var list = new List<(Pt, Pt, SectionPlacement, List<(double, double, double)>, string)>();
            foreach (var s in (JsonNode.Parse(File.ReadAllText(file))["sections"] as JsonArray).OfType<JsonObject>())
            {
                var cut = Args.Numbers(s, "cut"); var xr = Args.Numbers(s, "sheet_x_range");
                if (cut.Count != 4 || xr.Count != 2) continue;
                var place = new SectionPlacement { Dir = (int)(Args.Dbl(s, "dir") ?? 0), Offset = Args.Dbl(s, "offset") ?? 0, Matched = (int)(Args.Dbl(s, "matched") ?? 0) };
                if (place.Dir == 0) continue;
                var dwg = DwgFile.Read(Args.Str(s, "path"));
                double x0 = xr[0], x1 = xr[1];
                var heights = ElevationFit.Heights(dwg.Texts.Where(t => ElevationFit.IsLevelText(t, DwgFile.LayerMatches(t.Layer, new[] { "*Level*" }))),
                                                   dwg.Lines.Where(l => ElevationFit.IsLevelLine(l, DwgFile.LayerMatches(l.Layer, new[] { "*Level*" }), dwg.Texts)), x0, x1);
                var hz = dwg.Lines.Where(l => DwgFile.LayerMatches(l.Layer, new[] { "*Profile*" }) && Math.Abs(l.A.Y - l.B.Y) < 2 && Math.Abs(l.A.X - l.B.X) >= 300
                                              && Math.Min(l.A.X, l.B.X) >= x0 - 1 && Math.Max(l.A.X, l.B.X) <= x1 + 1)
                    .Select(l => (l.A.X, l.B.X, heights.Height(l.A.Y))).ToList();
                list.Add((new Pt(cut[0], cut[1]), new Pt(cut[2], cut[3]), place, hz, Args.Str(s, "name")));
            }
            return list;
        }

        /// <summary>
        /// A survey DWG not linked in the model (drawn in grid coordinates) placed in it: the project's survey position, both
        /// ways round, the one that puts its wall lines on the model's walls wins.
        /// </summary>
        /// <summary>1.36.12: layer roles from Survey to Revit (its rules + the job's layers.txt), cached per layer; null when it
        /// isn't loaded. Rule 6: tools pick layers by role, with the old names only as a fallback.</summary>
        internal static Func<IEnumerable<string>, Dictionary<string, string>> SurveyRoles(UIApplication app, string folder)
        {
            var hub = App.Instance?.Hub; if (hub == null) return null;
            var cache = new Dictionary<string, string>(StringComparer.Ordinal);
            return layers =>
            {
                var ask = layers.Where(l => l != null && !cache.ContainsKey(l)).Distinct().ToList();
                if (ask.Count > 0)
                {
                    try
                    {
                        var q = new JsonObject { ["layers"] = new JsonArray(ask.Select(l => (JsonNode)l).ToArray()) };
                        if (folder != null) q["folder"] = folder;
                        var roles = JsonNode.Parse(hub.Providers.CallHere(app, "survey_layer_roles", q) ?? "{}")?["roles"] as JsonObject;
                        foreach (var kv in roles ?? new JsonObject()) cache[kv.Key] = kv.Value?.GetValue<string>();
                    }
                    catch { }
                    foreach (var l in ask) if (!cache.ContainsKey(l)) cache[l] = null;
                }
                return cache;
            };
        }

        /// <summary>The roof plan's layers by role: roof lines (Roof, and Wall: on a roof plan the wall/building lines are the
        /// roof's edges, as Survey to Revit reads them), chimneys, rooflights, spot heights. `roof_layers` overrides; without
        /// Survey to Revit the old Plowman Craven names apply.</summary>
        internal sealed class RoofLayerSet
        {
            public Func<string, bool> Roof, Chimney, Light, Spot, Wall; public string Note;
        }
        internal static RoofLayerSet RoofLayers(UIApplication app, DwgContent file, string path, JsonObject a)
        {
            var given = Args.Strings(a, "roof_layers");
            var roleOf = SurveyRoles(app, Path.GetDirectoryName(path))?.Invoke(file.Lines.Select(l => l.Layer).Concat(file.Texts.Select(t => t.Layer)));
            string R(string layer) => layer != null && roleOf != null && roleOf.TryGetValue(layer, out var r) ? r : null;
            bool Has(string role) => roleOf != null && roleOf.Values.Any(v => v == role);
            var set = new RoofLayerSet
            {
                Chimney = l => R(l) == "Chimney" || DwgFile.LayerMatches(l, new[] { "*Chimney*" }),
                Light = l => R(l) == "Rooflight" || DwgFile.LayerMatches(l, new[] { "*Skylight*", "*Rooflight*", "*Roof*light*" }),
                Spot = l => R(l) == "HeightText" || R(l) == "LevelText" || DwgFile.LayerMatches(l, new[] { "*Level*", "*Spot*", "*Height*" }),
                Wall = l => R(l) == "Wall" || DwgFile.LayerMatches(l, new[] { "EF_25*", "*Wall*" }),
            };
            if (given.Count > 0) { set.Roof = l => DwgFile.LayerMatches(l, given.ToArray()) || set.Chimney(l) || set.Light(l); set.Note = "roof lines from roof_layers"; }
            else if (Has("Roof") || Has("Wall"))
            {
                set.Roof = l => { var r = R(l); return r == "Roof" || r == "Wall" || r == "Chimney" || r == "Rooflight"; };
                var used = file.Lines.Select(l => l.Layer).Where(l => l != null && set.Roof(l)).Distinct().ToList();
                set.Note = $"roof lines by Survey to Revit's layer roles (Roof/Wall/Chimney/Rooflight): {string.Join(", ", used.Take(8))}";
            }
            else { set.Roof = l => DwgFile.LayerMatches(l, new[] { "*Roof*", "EF_25*", "*Chimney*", "*Skylight*" }); set.Note = "roof lines by the hub's default layer names (Survey to Revit gave no roles: pass roof_layers for another surveyor's names)"; }
            return set;
        }

        /// <summary>The roof's faces, with spotless faces merged into a neighbour first: a lead roof's roll seams (or any line
        /// across a roof slope) chop one face into strips that carry no spot height; the seam between a spotless strip and the
        /// face beside it is dropped, and the faces found again (up to 4 rounds). Faces that still have no spot are counted.</summary>
        internal static List<List<Pt>> RoofFaces(List<Seg> segs, List<Pt> spots, double minArea, bool mergeSpotless, out int seamsDropped)
        {
            seamsDropped = 0;
            var rings = SurveyDetail.Faces(segs, 10, minArea);
            if (!mergeSpotless || spots.Count == 0) return rings;
            for (int round = 0; round < 4; round++)
            {
                // a face has heights with a spot inside it, or 3+ on its edges (a hip triangle: two eave corners and its apex);
                // a roll-seam strip with only the two at its ends is merged
                var spotted = new HashSet<int>(spots.Select(s => SurveyDetail.FaceAt(rings, s)).Where(i => i >= 0));
                foreach (var kv in SpotsByFace(rings, spots, 400)) if (kv.Value.Count >= 3) spotted.Add(kv.Key);
                var keep = new List<Seg>(); int dropped = 0;
                foreach (var s in segs)
                {
                    double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y, len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1) { keep.Add(s); continue; }
                    var mid = new Pt((s.A.X + s.B.X) / 2, (s.A.Y + s.B.Y) / 2); double nx = -dy / len * 60, ny = dx / len * 60;
                    int f1 = SurveyDetail.FaceAt(rings, new Pt(mid.X + nx, mid.Y + ny)), f2 = SurveyDetail.FaceAt(rings, new Pt(mid.X - nx, mid.Y - ny));
                    // a seam: faces on both sides, at least one of them without a spot height
                    if (f1 >= 0 && f2 >= 0 && f1 != f2 && (!spotted.Contains(f1) || !spotted.Contains(f2))) { dropped++; continue; }
                    keep.Add(s);
                }
                if (dropped == 0) break;
                seamsDropped += dropped; segs = keep;
                rings = SurveyDetail.Faces(segs, 10, minArea);
            }
            return rings;
        }

        /// <summary>1.36.13: each face's spot heights. Spots inside it; and when that gives fewer than 3, the spots on or near its
        /// edges too (within edgeMm): on a hipped or pitched roof the surveyor levels the eaves, hips and ridge, not the
        /// middle of a slope, so a hip triangle's points are its two eave corners and its apex. A spot on a shared edge
        /// counts for both faces.</summary>
        internal static Dictionary<int, List<Pt>> SpotsByFace(List<List<Pt>> rings, List<Pt> spots, double edgeMm)
        {
            var byFace = spots.GroupBy(s => SurveyDetail.FaceAt(rings, s)).Where(g => g.Key >= 0).ToDictionary(g => g.Key, g => g.ToList());
            for (int i = 0; i < rings.Count; i++)
            {
                byFace.TryGetValue(i, out var own); own ??= new List<Pt>();
                if (own.Count >= 3) continue;
                var r = rings[i];
                var near = spots.Where(s => !own.Contains(s) && Enumerable.Range(0, r.Count).Any(k => SurveyDetail.PtSeg(s, new Seg(r[k], r[(k + 1) % r.Count], 0)) <= edgeMm)).ToList();
                var all = own.Concat(near).ToList();
                if (all.Count > 0) byFace[i] = all;
            }
            return byFace;
        }

        private static Func<Pt, Pt> SurveyMap(Document doc, DwgContent file, out string note, Func<string, bool> isWall = null)
        {
            var pp = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
            var total = doc.ActiveProjectLocation.GetTotalTransform(); // shared -> internal
            var ways = new List<(string name, Func<Pt, Pt> f)>
            {
                ("shared coordinates", p => { var q = total.OfPoint(new XYZ(p.X / Mm, p.Y / Mm, 0)); return new Pt(q.X * Mm, q.Y * Mm); }),
            };
            foreach (int sign in new[] { -1, 1 })
            {
                double c = Math.Cos(sign * pp.Angle), s = Math.Sin(sign * pp.Angle);
                ways.Add(($"survey position, angle {(sign < 0 ? "-" : "+")}", p =>
                {
                    double x = p.X - pp.EastWest * Mm, y = p.Y - pp.NorthSouth * Mm;
                    return new Pt(c * x - s * y, s * x + c * y);
                }));
            }
            var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.Location is LocationCurve lc && lc.Curve is Line)
                .Select(w => { var c = ((LocationCurve)w.Location).Curve; return (s: new Seg(CadLink.ToMm(c.GetEndPoint(0)), CadLink.ToMm(c.GetEndPoint(1)), 0), half: w.Width * Mm / 2); }).ToList();
            // wall lines by role (Survey to Revit), else the old names; with neither, every line (better than scoring 0%)
            var wallish = file.Lines.Where(l => isWall != null ? isWall(l.Layer) : DwgFile.LayerMatches(l.Layer, new[] { "EF_25*", "*Wall*" })).ToList();
            if (wallish.Count < 20) wallish = file.Lines.Where(l => Math.Abs(l.A.X - l.B.X) + Math.Abs(l.A.Y - l.B.Y) > 500).ToList();
            var sample = wallish.Take(400).SelectMany(l => new[] { l.A, l.B }).ToList();
            (string name, Func<Pt, Pt> f, double score) best = (ways[0].name, ways[0].f, -1);
            foreach (var (name, f) in ways)
            {
                double score = sample.Count == 0 ? 0 : sample.Count(p => { var q = f(p); return walls.Any(w => SurveyDetail.PtSeg(q, w.s) <= w.half + 300); }) / (double)sample.Count;
                if (score > best.score) best = (name, f, score);
            }
            note = $"placed by {best.name}: {best.score:P0} of its wall line ends on the model's walls";
            return best.f;
        }

        private static string FindRoofPlan(Document doc)
        {
            var dirs = CadLink.All(doc).Select(i => CadLink.FilePath(doc, i)).Where(p => p != null).Select(Path.GetDirectoryName).Distinct();
            string best = null; int most = 100;
            foreach (var d in dirs)
                foreach (var f in Directory.GetFiles(d, "*.dwg"))
                {
                    try
                    {
                        // a roof plan by its roof-layer lines, or by its name ("ROOF PLAN", "-RF"): another surveyor's roof plan
                        // may have no layer called *Roof* (On Centre: all on BUILDINGS)
                        var name = Path.GetFileNameWithoutExtension(f).ToUpperInvariant();
                        bool named = name.Contains("ROOF") || System.Text.RegularExpressions.Regex.IsMatch(name, @"(^|[^A-Z])RF([^A-Z]|$)");
                        int n = DwgFile.Read(f).Lines.Count(l => DwgFile.LayerMatches(l.Layer, new[] { "*Roof*" })) + (named ? 100000 : 0);
                        if (n > most) { most = n; best = f; }
                    }
                    catch { }
                }
            return best;
        }

        /// <summary>An upright prism over a plan ring (mm), from z0 up by height.</summary>
        private static Solid Prism(List<Pt> ring, double z0, double height)
        {
            if (height < 1) return null;
            var loop = Loop(ring.Select(p => new XYZ(p.X / Mm, p.Y / Mm, z0 / Mm)).ToList());
            return loop == null ? null : GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, height / Mm);
        }

        /// <summary>A roof face: the surveyed surface (plane over the ring) with the build-up under it.</summary>
        private static Solid Slab(List<Pt> ring, PlaneZ plane, double datum, double thick)
        {
            var bottom = ring.Select(p => new XYZ(p.X / Mm, p.Y / Mm, (plane.At(p.X, p.Y) - datum - thick) / Mm)).ToList();
            var loop = Loop(bottom);
            return loop == null ? null : GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, thick / Mm);
        }

        private static CurveLoop Loop(List<XYZ> pts)
        {
            var clean = new List<XYZ>();
            foreach (var p in pts) if (clean.Count == 0 || clean[^1].DistanceTo(p) * Mm > 2) clean.Add(p);
            if (clean.Count > 1 && clean[0].DistanceTo(clean[^1]) * Mm <= 2) clean.RemoveAt(clean.Count - 1);
            if (clean.Count < 3) return null;
            var loop = new CurveLoop();
            for (int i = 0; i < clean.Count; i++) loop.Append(Line.CreateBound(clean[i], clean[(i + 1) % clean.Count]));
            return loop;
        }

        private static long Shape(Document doc, BuiltInCategory cat, GeometryObject g, string name, string comments) =>
            Shape(doc, cat, new List<GeometryObject> { g }, name, comments);

        private static long Shape(Document doc, BuiltInCategory cat, IList<GeometryObject> g, string name, string comments)
        {
            var ds = DirectShape.CreateElement(doc, new ElementId(cat));
            ds.ApplicationId = "BGMCP"; ds.ApplicationDataId = name;
            ds.SetShape(g);
            ds.Name = name;
            ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(comments);
            return ds.Id.Value;
        }
    }
}
