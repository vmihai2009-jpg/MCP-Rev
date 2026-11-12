using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// 1.10.0: the ground. A Toposolid from the survey site plan's spot heights and contours (Survey/Terrain.cs reads
    /// them), or from points given; and areas of it (paths, paving, lawn) as subdivisions. Heights are survey datum
    /// (AOD) unless said otherwise, and go into the model through the shared coordinates, so the ground sits at the
    /// same datum as the floors Survey to Revit built.
    /// </summary>
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> Terrain()
        {
            yield return new RevitTool
            {
                Name = "dwg_terrain",
                Write = true,
                Description = "The ground from the survey site plan: spot-height labels (\"12.345\", \"+12.35\", \"SL 12.34\"; each placed at the cross or point it labels), " +
                              "3D points, and contours drawn at their height, read straight from the DWG; typos (a level metres off the rest) dropped and listed; thinned to max_points. " +
                              "Then a Toposolid through them: inside boundary if given, with holes (e.g. the building's footprint, where the floors take over). " +
                              "DRY RUN by default: says what it read and makes the surface, then undoes it, so Revit's refusals show. dry_run false makes it (one undo step); " +
                              "replace_id puts it in place of an older one. Window labels (S/H), floor levels (FFL) and inverts (IL) are not ground unless prefixes says so.",
                InputSchema = Schema(Import.With(
                    ("path", S("string", "Or a DWG/DXF file that is not linked (its coordinates are then taken as the shared ones, as a site survey on the grid is)"), false),
                    ("spot_layers", Arr("string", "Layer globs of the spot heights and their crosses (default: every layer, the label pattern decides)"), false),
                    ("contour_layers", Arr("string", "Layer globs of the contours (default *contour*, *cont*)"), false),
                    ("exclude_layers", Arr("string", "Layer globs never used, e.g. the building's own level labels"), false),
                    ("prefixes", Arr("string", "Label prefixes that are ground (default \"\", +, SL, GL, EL, RL, LVL, L, CL, TK, BK)"), false),
                    ("labels_in_mm", S("boolean", "Labels are millimetres, not metres"), false),
                    ("pair_radius_mm", S("number", "How far a label may be from its cross (default 5 x text height, at least 1000)"), false),
                    ("use_contours", S("boolean", "Default true"), false),
                    ("use_3d_points", S("boolean", "POINTs and blocks drawn at a height (default true)"), false),
                    ("contour_spacing_mm", S("number", "A point every this along contours (default 2000)"), false),
                    ("drop_outliers", S("boolean", "Drop levels metres off the rest (default true)"), false),
                    ("max_points", S("integer", "At most this many points (default 5000)"), false),
                    ("boundary", Arr("array", "[[x,y], ...] model mm: only the ground inside, and the Toposolid's edge"), false),
                    ("holes", Arr("array", "[[[x,y], ...], ...] model mm: left out of the Toposolid (building footprint)"), false),
                    ("heights", S("string", "survey_datum (default: AOD, as surveys label them) or model (already the model's own Z, mm)"), false),
                    ("z_offset_mm", S("number", "Added to every height"), false),
                    ("level", S("string", "Level of the Toposolid (default: the replaced one's, else the lowest)"), false),
                    ("type_name", S("string", "Toposolid type (default: the replaced one's, else the first)"), false),
                    ("replace_id", S("integer", "A Toposolid to replace"), false),
                    ("dry_run", S("boolean", "Default true"), false),
                    ("comments", S("string", "Comments (default MCP terrain)"), false))),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    string path = Args.Str(a, "path"), coords, note = null;
                    Func<Pt, Pt> toModel;
                    var sharedToInternal = doc.ActiveProjectLocation.GetTotalTransform().Inverse;
                    DwgContent content;
                    if (path == null)
                    {
                        var imp = CadLink.Pick(doc, a);
                        path = CadLink.FilePath(doc, imp) ?? throw new InvalidOperationException("Revit does not know that link's file (an import, or the file has moved). Give its path instead.");
                        content = DwgFile.Read(path);
                        toModel = CadLink.FileToModel(doc, imp, content, out note)
                            ?? throw new InvalidOperationException("The file's lines could not be matched to the link, so its points cannot be placed: " + note + " Give path instead (then the file's coordinates are taken as shared coordinates).");
                        coords = "placed on the link";
                    }
                    else
                    {
                        content = DwgFile.Read(path);
                        toModel = p => { var q = sharedToInternal.OfPoint(new XYZ(p.X / Mm, p.Y / Mm, 0)); return new Pt(q.X * Mm, q.Y * Mm, 0); };
                        coords = "the file's coordinates taken as the project's shared coordinates";
                    }

                    var o = new TerrainOptions
                    {
                        LabelsInMetres = Args.Bool(a, "labels_in_mm") != true,
                        PairRadiusMm = Args.Dbl(a, "pair_radius_mm") ?? 0,
                        UseContours = Args.Bool(a, "use_contours") != false,
                        Use3DPoints = Args.Bool(a, "use_3d_points") != false,
                        ContourSpacingMm = Math.Max(100, Args.Dbl(a, "contour_spacing_mm") ?? 2000),
                        DropOutliers = Args.Bool(a, "drop_outliers") != false,
                        MaxPoints = Math.Clamp(Args.Int(a, "max_points", 5000), 3, 50000),
                    };
                    o.SpotLayers.AddRange(Args.Strings(a, "spot_layers"));
                    o.ContourLayers.AddRange(Args.Strings(a, "contour_layers"));
                    o.ExcludeLayers.AddRange(Args.Strings(a, "exclude_layers"));
                    if (a["prefixes"] is JsonArray pa) o.Prefixes = pa.OfType<JsonValue>().Select(v => v.ToString()).ToList();
                    var read = Survey.Terrain.Collect(content, o);

                    // into the model: plan position through the link (or shared coordinates), height from the datum
                    var boundary = MmLoop(a["boundary"] as JsonArray);
                    var mapped = read.Points.Select(p => { var q = toModel(p); return new Pt(q.X, q.Y, p.Z); }).ToList();
                    int outside = 0;
                    if (boundary.Count >= 3)
                    {
                        int before = mapped.Count;
                        mapped = mapped.Where(p => GeoMath.PointInPolygon(p, boundary)).ToList();
                        outside = before - mapped.Count;
                    }
                    bool modelZ = string.Equals(Args.Str(a, "heights"), "model", StringComparison.OrdinalIgnoreCase);
                    double offset = Args.Dbl(a, "z_offset_mm") ?? 0;
                    var pts = mapped.Select(p => new XYZ(p.X / Mm, p.Y / Mm, ModelZ(sharedToInternal, p.Z + offset, modelZ))).ToList();

                    bool dry = Args.Bool(a, "dry_run") != false;
                    object made = null;
                    if (pts.Count >= 3 && !read.Warnings.Any(w => w.Contains("on one line")))
                        made = MakeToposolid(doc, pts, boundary, a, dry, "MCP: terrain from " + System.IO.Path.GetFileName(path));

                    var p0 = mapped.Count > 0 ? mapped : null;
                    return new
                    {
                        file = path, units = content.UnitsNote, coordinates = coords, match = note,
                        heights = modelZ ? "model Z" : $"survey datum; the model's 0 is at {DatumMm(doc) / 1000:0.000} m",
                        read = new
                        {
                            spotLabels = read.SpotLabels, atTheirCross = read.SpotsAtCross, atTheirText = read.SpotsAtText,
                            points3d = read.Points3D, contourSegments = read.ContourSegments, flatContourSegments = read.FlatContourSegments,
                            duplicates = read.Duplicates, thinnedToCellMm = read.ThinnedToCellMm > 0 ? read.ThinnedToCellMm : (double?)null,
                            outsideBoundary = outside, spotLayers = read.SpotLayersUsed, contourLayers = read.ContourLayersUsed,
                        },
                        points = pts.Count,
                        lowestMm = p0 == null ? (double?)null : Math.Round(p0.Min(p => p.Z) + offset),
                        highestMm = p0 == null ? (double?)null : Math.Round(p0.Max(p => p.Z) + offset),
                        dropped = read.Outliers.Take(20).Select(t => { var q = toModel(t.P); return new { label = t.Label, layer = t.Layer, heightMm = Math.Round(t.P.Z), at = new[] { Math.Round(q.X), Math.Round(q.Y) } }; }).ToList(),
                        sample = read.Spots.Take(12).Select(t => { var q = toModel(t.P); return new { label = t.Label, from = t.Source, heightMm = Math.Round(t.P.Z), at = new[] { Math.Round(q.X), Math.Round(q.Y) } }; }).ToList(),
                        warnings = read.Warnings,
                        toposolid = made,
                        next = dry ? "Check the sample and the dropped levels (dwg_read near a place shows the labels), then dry_run false." : "toposolid_subdivide for paths and paving; survey_coordinates if the ground is off by a constant height.",
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "create_toposolid",
                Write = true,
                Description = "A Toposolid through points given: [[x, y, z], ...] model mm in plan, z survey datum (AOD) mm unless heights is model. " +
                              "Optional boundary and holes (model mm). replace_id puts it in place of an older one (its type and level kept). Use dwg_terrain to read the points from a DWG. One undo step.",
                InputSchema = Schema(
                    ("points", Arr("array", "[[x, y, z], ...]"), true),
                    ("boundary", Arr("array", "[[x,y], ...] model mm"), false),
                    ("holes", Arr("array", "[[[x,y], ...], ...] model mm"), false),
                    ("heights", S("string", "survey_datum (default) or model"), false),
                    ("level", S("string", "Level (default: the replaced one's, else the lowest)"), false),
                    ("type_name", S("string", "Toposolid type"), false),
                    ("replace_id", S("integer", "A Toposolid to replace"), false),
                    ("dry_run", S("boolean", "Make it, report, undo (default false)"), false),
                    ("comments", S("string", ""), false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var tf = doc.ActiveProjectLocation.GetTotalTransform().Inverse;
                    bool modelZ = string.Equals(Args.Str(a, "heights"), "model", StringComparison.OrdinalIgnoreCase);
                    var pts = new List<XYZ>();
                    foreach (var p in (a["points"] as JsonArray)?.OfType<JsonArray>() ?? Enumerable.Empty<JsonArray>())
                    {
                        var n = p.OfType<JsonValue>().Select(v => double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : double.NaN).ToList();
                        if (n.Count < 3 || n.Any(double.IsNaN)) throw new ArgumentException("Each point is [x, y, z] in mm.");
                        pts.Add(new XYZ(n[0] / Mm, n[1] / Mm, ModelZ(tf, n[2], modelZ)));
                    }
                    if (pts.Count < 3) throw new ArgumentException("At least 3 points are needed.");
                    return MakeToposolid(doc, pts, MmLoop(a["boundary"] as JsonArray), a, Args.Bool(a, "dry_run") == true, "MCP: make Toposolid");
                }
            };

            yield return new RevitTool
            {
                Name = "toposolid_subdivide",
                Write = true,
                Description = "An area of a Toposolid with its own material: paths, paving, lawn, gravel, from the site plan's outlines (model mm). " +
                              "The material is made if the model has none of that name. One undo step.",
                InputSchema = Schema(
                    ("toposolid_id", S("integer", "The Toposolid"), true),
                    ("loops", Arr("array", "[[[x,y], ...], [hole...]] model mm: the outline, then any holes"), true),
                    ("material", S("string", "Material name, e.g. Paving - Stone"), false),
                    ("comments", S("string", ""), false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var topo = doc.GetElement(new ElementId(Args.Long(a, "toposolid_id") ?? 0)) as Toposolid ?? throw new ArgumentException("toposolid_id is not a Toposolid.");
                    double z = (doc.GetElement(topo.LevelId) as Level)?.Elevation ?? 0;
                    var loops = ((a["loops"] as JsonArray)?.OfType<JsonArray>() ?? Enumerable.Empty<JsonArray>()).Select(l => CurveLoopAt(MmLoop(l), z)).Where(l => l != null).ToList();
                    if (loops.Count == 0) throw new ArgumentException("loops is needed: [[[x,y], [x,y], [x,y], ...]] in model mm.");
                    long id = 0; string material = Args.Str(a, "material");
                    var notes = BuildTools.Tx.Run(doc, "MCP: Toposolid area", () =>
                    {
                        var sub = topo.CreateSubDivision(doc, loops);
                        if (material != null)
                        {
                            var m = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name.Equals(material, StringComparison.OrdinalIgnoreCase))
                                ?? doc.GetElement(Material.Create(doc, material)) as Material;
                            sub.get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_MATERIAL)?.Set(m.Id);
                        }
                        sub.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Args.Str(a, "comments") ?? "MCP terrain");
                        id = sub.Id.Value;
                    });
                    return new { subdivision_id = id, revit = notes, undo = "One undo step in Revit." };
                }
            };
        }

        // ---------- the Toposolid ----------

        private static object MakeToposolid(Document doc, List<XYZ> pts, List<Pt> boundary, JsonObject a, bool dry, string name)
        {
            if (doc.IsReadOnly) throw new InvalidOperationException("This project is read-only.");
            if (doc.IsModifiable) throw new InvalidOperationException("Another command is still changing the model. Ask the user to finish it in Revit, then try again.");
            Toposolid old = Args.Long(a, "replace_id") is long rid ? doc.GetElement(new ElementId(rid)) as Toposolid ?? throw new ArgumentException($"{rid} is not a Toposolid.") : null;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            Level level = Args.Str(a, "level") is string ln
                ? levels.FirstOrDefault(l => l.Name.Equals(ln, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"There is no level \"{ln}\". Levels: {string.Join(", ", levels.Select(l => l.Name))}.")
                : old != null ? doc.GetElement(old.LevelId) as Level : levels.FirstOrDefault() ?? throw new InvalidOperationException("The model has no level.");
            ElementId typeId;
            if (Args.Str(a, "type_name") is string tn)
                typeId = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().FirstOrDefault(t => t.Name.Equals(tn, StringComparison.OrdinalIgnoreCase))?.Id
                    ?? throw new ArgumentException($"There is no Toposolid type \"{tn}\".");
            else typeId = old?.GetTypeId() ?? new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).FirstElementId();
            if (typeId == null || typeId == ElementId.InvalidElementId) throw new InvalidOperationException("The project has no Toposolid type.");

            var holes = ((a["holes"] as JsonArray)?.OfType<JsonArray>() ?? Enumerable.Empty<JsonArray>()).Select(MmLoop).Where(h => h.Count >= 3).ToList();
            // only points on the ground that is made: one outside the edge or in a hole would also throw the height check below
            int given = pts.Count;
            pts = pts.Where(p =>
            {
                var q = new Pt(p.X * Mm, p.Y * Mm);
                return (boundary.Count < 3 || GeoMath.PointInPolygon(q, boundary)) && !holes.Any(h => GeoMath.PointInPolygon(q, h));
            }).ToList();
            if (pts.Count < 3) throw new ArgumentException($"Only {pts.Count} of the {given} points are inside the boundary and outside the holes: at least 3 are needed.");
            long id = 0; double lifted = 0;
            string edge = boundary.Count >= 3 ? "the boundary given" : holes.Count > 0 ? "the points' extent" : "around the points";
            var pre = BuildTools.Tx.Preprocessor(out var notes, out var blocking);
            using (var t = new Transaction(doc, name))
            {
                var fo = t.GetFailureHandlingOptions();
                fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); fo.SetForcedModalHandling(false);
                t.SetFailureHandlingOptions(fo);
                t.Start();
                try
                {
                    Toposolid topo;
                    if (boundary.Count >= 3 || holes.Count > 0)
                    {
                        var outer = boundary.Count >= 3 ? boundary : new List<Pt>
                        {
                            new Pt(pts.Min(p => p.X) * Mm, pts.Min(p => p.Y) * Mm), new Pt(pts.Max(p => p.X) * Mm, pts.Min(p => p.Y) * Mm),
                            new Pt(pts.Max(p => p.X) * Mm, pts.Max(p => p.Y) * Mm), new Pt(pts.Min(p => p.X) * Mm, pts.Max(p => p.Y) * Mm),
                        };
                        var loops = new List<CurveLoop> { CurveLoopAt(outer, level.Elevation) ?? throw new ArgumentException("The boundary is not a usable outline.") };
                        loops.AddRange(holes.Select(h => CurveLoopAt(h, level.Elevation)).Where(l => l != null));
                        topo = Toposolid.Create(doc, loops, pts, typeId, level.Id);
                    }
                    else topo = Toposolid.Create(doc, pts, typeId, level.Id);
                    doc.Regenerate();
                    // the points are meant as model heights; if Revit took them as above the level, lift the surface back (as Survey to Revit does)
                    var box = topo.get_BoundingBox(null);
                    double top = pts.Max(p => p.Z);
                    if (box != null && Math.Abs(box.Max.Z - top) > 100 / Mm)
                    {
                        ElementTransformUtils.MoveElement(doc, topo.Id, new XYZ(0, 0, top - box.Max.Z));
                        lifted = Math.Round((top - box.Max.Z) * Mm);
                    }
                    topo.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Args.Str(a, "comments") ?? "MCP terrain");
                    foreach (Phase ph in doc.Phases)
                        if (ph.Name.Equals("Existing", StringComparison.OrdinalIgnoreCase) && topo.get_Parameter(BuiltInParameter.PHASE_CREATED) is Parameter pp && !pp.IsReadOnly) pp.Set(ph.Id);
                    if (old != null) doc.Delete(old.Id);
                    id = topo.Id.Value;
                }
                catch
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    throw;
                }
                if (dry) t.RollBack();
                else if (t.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit refused the Toposolid, so nothing was changed. " + string.Join(" ", blocking()));
            }
            return new
            {
                made = !dry, toposolid_id = dry ? (long?)null : id, replaced = old?.Id.Value, level = level.Name, edge, holes = holes.Count, points = pts.Count, leftOut = given - pts.Count,
                liftedMm = lifted != 0 ? lifted : (double?)null,
                revit = notes().Concat(blocking()).Distinct().Take(20).ToList(),
                undo = dry ? "Dry run: made and undone, nothing changed." : "One undo step in Revit.",
            };
        }

        /// <summary>Survey datum (AOD) mm to the model's internal Z, in feet: through the shared coordinates.</summary>
        private static double ModelZ(Transform sharedToInternal, double mm, bool alreadyModel) =>
            alreadyModel ? mm / Mm : sharedToInternal.OfPoint(new XYZ(0, 0, mm / Mm)).Z;

        private static double DatumMm(Document doc) => doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;

        private static List<Pt> MmLoop(JsonArray arr)
        {
            var list = new List<Pt>();
            foreach (var p in arr?.OfType<JsonArray>() ?? Enumerable.Empty<JsonArray>())
            {
                var n = p.OfType<JsonValue>().Select(v => double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : double.NaN).ToList();
                if (n.Count >= 2 && !double.IsNaN(n[0]) && !double.IsNaN(n[1])) list.Add(new Pt(n[0], n[1]));
            }
            return list;
        }

        private static CurveLoop CurveLoopAt(List<Pt> pts, double zFeet)
        {
            if (pts.Count >= 2 && pts[0].Dist2D(pts[pts.Count - 1]) < 1) pts = pts.Take(pts.Count - 1).ToList();
            if (pts.Count < 3) return null;
            var loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i]; var q = pts[(i + 1) % pts.Count];
                if (p.Dist2D(q) < 1) continue;
                loop.Append(Line.CreateBound(new XYZ(p.X / Mm, p.Y / Mm, zFeet), new XYZ(q.X / Mm, q.Y / Mm, zFeet)));
            }
            return loop;
        }
    }
}
