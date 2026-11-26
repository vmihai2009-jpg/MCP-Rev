// 1.44.0: rotate_plan_views. A client wants the GA plans turned (180 degrees on Swallow Place: the way the building has always
// been drawn). The plans are turned through a scope box (a turned copy by default, so views on other sheets stay as they are),
// plans with no scope box are given it (so every floor shares one crop and stacks), and each viewport is moved so the same
// model point lands where it was on the sheet: exported PDFs stack.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        private sealed class DryRunDone : Exception { }

        public static IEnumerable<RevitTool> SheetViews()
        {
            yield return new RevitTool
            {
                Name = "rotate_plan_views",
                Write = true,
                Description = "Turn the plans on a set of sheets (e.g. GA series 10.2xx and 20.2xx) by an angle (default 180) and keep each plan at the same place on its sheet, so exported sheets stack. " +
                              "Turns a scope box about its centre: by default a copy of the scope box most of the plans use (named new_name), given only to the chosen plans, so views elsewhere keep the old one; in_place turns the scope box itself. " +
                              "Plans with no scope box are given the turned one (assign_unboxed, default true; else their own crop region is turned). " +
                              "Each viewport is then moved so the scope box centre sits where it sat before (stack: the same spot on every sheet). " +
                              "Plans whose view template controls the scope box, and plans on another scope box, are listed, not changed. Run again with the same new_name: the existing turned box is reused, never turned twice. " +
                              "dry_run (default true) does it all, reports, and undoes it. One undo step. Does not save.",
                InputSchema = Schema(
                    ("sheet_prefixes", Arr("string", "Sheets whose number starts with one of these, e.g. [\"10.2\", \"20.2\"]"), false),
                    ("sheet_numbers", Arr("string", "Or exact sheet numbers"), false),
                    ("exclude_sheets", Arr("string", "Leave out sheets with this number, or with this text in their name (e.g. \"Original Orientation\")"), false),
                    ("angle_deg", S("number", "Turn, anticlockwise in plan (default 180)"), false),
                    ("scope_box", S("string", "Scope box to turn (default: the one most of the plans use)"), false),
                    ("in_place", S("boolean", "Turn that scope box itself: every view using it turns, on any sheet (default false: a turned copy)"), false),
                    ("new_name", S("string", "Name of the turned copy (default \"<scope box> <angle>\")"), false),
                    ("assign_unboxed", S("boolean", "Give plans with no scope box the turned one (default true)"), false),
                    ("stack", S("boolean", "Put the building at the same spot on every sheet (default true: the middle of where the plans on the scope box sit now); false: each sheet keeps its own spot"), false),
                    ("title_like_sheet", S("string", "Put every viewport title where the plan's title sits on this sheet (e.g. a sheet already drawn the way the client wants). Default: each title stays where it was on its sheet (turning the view otherwise throws it off the sheet)"), false),
                    ("bubbles", S("string", "Grid bubbles in the turned plans: top_left (default: the top end of up-down grids, the left end of across grids, the other end hidden), or keep"), false),
                    ("dry_run", S("boolean", "Do it, report, undo (default true)"), false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    double deg = Args.Dbl(a, "angle_deg") ?? 180, ang = deg * Math.PI / 180;
                    bool dry = Args.Bool(a, "dry_run") ?? true, inPlace = Args.Bool(a, "in_place") ?? false;
                    bool assignUnboxed = Args.Bool(a, "assign_unboxed") ?? true, stack = Args.Bool(a, "stack") ?? true;
                    var prefixes = Args.Strings(a, "sheet_prefixes"); var exact = Args.Strings(a, "sheet_numbers"); var excl = Args.Strings(a, "exclude_sheets");
                    if (prefixes.Count == 0 && exact.Count == 0) throw new ArgumentException("Give sheet_prefixes or sheet_numbers.");
                    var cmp = StringComparison.OrdinalIgnoreCase;

                    var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder && !s.IsTemplate
                                    && (exact.Any(n => n.Equals(s.SheetNumber, cmp)) || prefixes.Any(p => s.SheetNumber.StartsWith(p, cmp)))
                                    && !excl.Any(x => s.SheetNumber.Equals(x, cmp) || s.Name.IndexOf(x, cmp) >= 0))
                        .OrderBy(s => s.SheetNumber).ToList();
                    var planTypes = new[] { ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.AreaPlan, ViewType.EngineeringPlan };
                    var targets = new List<(ViewSheet sheet, Viewport vp, ViewPlan view)>();
                    var noPlan = new List<string>();
                    foreach (var s in sheets)
                    {
                        int before = targets.Count;
                        foreach (var id in s.GetAllViewports())
                            if (doc.GetElement(id) is Viewport vp && doc.GetElement(vp.ViewId) is ViewPlan v && !v.IsTemplate && planTypes.Contains(v.ViewType))
                                targets.Add((s, vp, v));
                        if (targets.Count == before) noPlan.Add(s.SheetNumber);
                    }
                    if (targets.Count == 0) throw new ArgumentException("No plan views on those sheets.");

                    ElementId BoxOf(View v) => v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)?.AsElementId() ?? ElementId.InvalidElementId;
                    var boxes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().ToList();
                    Element src = Args.Str(a, "scope_box") is string sbn
                        ? boxes.FirstOrDefault(b => b.Name.Equals(sbn, cmp)) ?? throw new ArgumentException($"No scope box called \"{sbn}\". Scope boxes: {string.Join(", ", boxes.Select(b => b.Name))}.")
                        : targets.Select(t => BoxOf(t.view)).Where(i => i != ElementId.InvalidElementId).GroupBy(i => i.Value).OrderByDescending(g => g.Count())
                                 .Select(g => doc.GetElement(g.First())).FirstOrDefault()
                          ?? throw new ArgumentException("None of these plans has a scope box: give scope_box.");
                    string newName = Args.Str(a, "new_name") ?? $"{src.Name} {deg:0}";
                    var existing = inPlace ? null : boxes.FirstOrDefault(b => b.Name.Equals(newName, cmp));
                    var sbb = src.get_BoundingBox(null);
                    var pivot = new XYZ((sbb.Min.X + sbb.Max.X) / 2, (sbb.Min.Y + sbb.Max.Y) / 2, 0);
                    if (existing != null)
                    {
                        var ebb = existing.get_BoundingBox(null);
                        pivot = new XYZ((ebb.Min.X + ebb.Max.X) / 2, (ebb.Min.Y + ebb.Max.Y) / 2, 0);
                    }

                    XYZ OnSheet(Viewport vp, View v)
                    {
                        var m2p = v.GetModelToProjectionTransforms().First().GetModelToProjectionTransform();
                        var p = vp.GetProjectionToSheetTransform().OfPoint(m2p.OfPoint(new XYZ(pivot.X, pivot.Y, v.GenLevel?.Elevation ?? 0)));
                        return new XYZ(p.X, p.Y, 0);
                    }
                    var before0 = targets.ToDictionary(t => t.vp.Id.Value, t => OnSheet(t.vp, t.view));
                    // 1.44.1: the title keeps its sheet offset from the box outline, which turns with the view, so it lands off the sheet: put it back.
                    var title0 = targets.ToDictionary(t => t.vp.Id.Value, t => t.vp.GetLabelOutline().MinimumPoint);
                    XYZ titleAt = null;
                    if (Args.Str(a, "title_like_sheet") is string tls)
                    {
                        var rs = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().FirstOrDefault(s => s.SheetNumber.Equals(tls, cmp))
                                 ?? throw new ArgumentException($"No sheet numbered \"{tls}\".");
                        var rvp = rs.GetAllViewports().Select(i => doc.GetElement(i) as Viewport).FirstOrDefault(v => v != null && doc.GetElement(v.ViewId) is ViewPlan)
                                  ?? throw new ArgumentException($"Sheet {tls} has no plan viewport to copy the title position from.");
                        titleAt = rvp.GetLabelOutline().MinimumPoint;
                    }
                    string bubbles = (Args.Str(a, "bubbles") ?? "top_left").ToLowerInvariant();
                    int bubblesSet = 0; var bubbleFails = new List<string>();
                    var srcUsers = targets.Where(t => BoxOf(t.view) == src.Id).ToList();
                    var basis = (srcUsers.Count > 0 ? srcUsers : targets).Select(t => before0[t.vp.Id.Value]).ToList();
                    double Med(IEnumerable<double> xs) { var l = xs.OrderBy(x => x).ToList(); return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2; }
                    var stackAt = new XYZ(Med(basis.Select(p => p.X)), Med(basis.Select(p => p.Y)), 0);

                    var targetIds = new HashSet<long>(targets.Select(t => t.view.Id.Value));
                    var elsewhere = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .Where(v => !v.IsTemplate && !targetIds.Contains(v.Id.Value) && BoxOf(v) == src.Id).Select(v => v.Name).ToList();

                    var rows = new JsonArray(); var left = new JsonArray();
                    string boxName = null; long boxId = 0;
                    List<string> notes = null;
                    try
                    {
                        notes = BuildTools.Tx.Run(doc, $"MCP: turn plans {deg:0}", () =>
                        {
                            Element box;
                            if (inPlace) { box = src; ElementTransformUtils.RotateElement(doc, src.Id, Line.CreateBound(pivot, pivot + XYZ.BasisZ), ang); }
                            else if (existing != null) box = existing;
                            else
                            {
                                var copy = ElementTransformUtils.CopyElement(doc, src.Id, XYZ.Zero).First();
                                box = doc.GetElement(copy);
                                var np = box.get_Parameter(BuiltInParameter.VOLUME_OF_INTEREST_NAME);
                                if (np != null && !np.IsReadOnly) np.Set(newName); else box.Name = newName;
                                ElementTransformUtils.RotateElement(doc, box.Id, Line.CreateBound(pivot, pivot + XYZ.BasisZ), ang);
                            }
                            boxName = box.Name; boxId = box.Id.Value;

                            var changed = new List<(ViewSheet sheet, Viewport vp, ViewPlan view, string was, string how)>();
                            foreach (var t in targets)
                            {
                                var cur = BoxOf(t.view);
                                string was = cur == ElementId.InvalidElementId ? "none" : doc.GetElement(cur)?.Name;
                                if (cur == box.Id) { changed.Add((t.sheet, t.vp, t.view, was, inPlace ? "scope box turned" : "already on it")); continue; }
                                bool onSrc = cur == src.Id, unboxed = cur == ElementId.InvalidElementId;
                                if (!onSrc && !(unboxed && assignUnboxed))
                                {
                                    if (unboxed)
                                    {
                                        var crop = t.view.GetDependentElements(new ElementCategoryFilter(BuiltInCategory.OST_Viewers)).FirstOrDefault();
                                        if (crop == null) { left.Add(new JsonObject { ["sheet"] = t.sheet.SheetNumber, ["view"] = t.view.Name, ["why"] = "no scope box and its crop region was not found" }); continue; }
                                        if (!t.view.CropBoxActive) t.view.CropBoxActive = true;
                                        ElementTransformUtils.RotateElement(doc, crop, Line.CreateBound(pivot, pivot + XYZ.BasisZ), ang);
                                        changed.Add((t.sheet, t.vp, t.view, was, "crop region turned")); continue;
                                    }
                                    left.Add(new JsonObject { ["sheet"] = t.sheet.SheetNumber, ["view"] = t.view.Name, ["why"] = $"on another scope box ({was})" }); continue;
                                }
                                var p = t.view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                                if (p == null || p.IsReadOnly)
                                {
                                    left.Add(new JsonObject { ["sheet"] = t.sheet.SheetNumber, ["view"] = t.view.Name, ["why"] = "its view template controls Scope Box: untick it in the template, or give the template the turned box" });
                                    continue;
                                }
                                if (!t.view.CropBoxActive) t.view.CropBoxActive = true;
                                p.Set(box.Id);
                                changed.Add((t.sheet, t.vp, t.view, was, unboxed ? "given the turned scope box (had none)" : "moved to the turned scope box"));
                            }
                            doc.Regenerate();
                            foreach (var c in changed)
                            {
                                var now = OnSheet(c.vp, c.view);
                                var want = stack ? stackAt : before0[c.vp.Id.Value];
                                var d = new XYZ(want.X - now.X, want.Y - now.Y, 0);
                                if (d.GetLength() > 1e-6) c.vp.SetBoxCenter(c.vp.GetBoxCenter() + d);
                            }
                            doc.Regenerate();
                            if (bubbles == "top_left")
                                foreach (var c in changed)
                                    foreach (var g in new FilteredElementCollector(doc, c.view.Id).OfClass(typeof(Grid)).Cast<Grid>())
                                    {
                                        try
                                        {
                                            var cv = g.GetCurvesInView(DatumExtentType.ViewSpecific, c.view).FirstOrDefault();
                                            if (cv == null) continue;
                                            XYZ p0 = cv.GetEndPoint(0), p1 = cv.GetEndPoint(1), dd = p1 - p0, up = c.view.UpDirection, rt = c.view.RightDirection;
                                            bool upDown = Math.Abs(dd.DotProduct(up)) >= Math.Abs(dd.DotProduct(rt));
                                            bool at0 = upDown ? p0.DotProduct(up) > p1.DotProduct(up) : p0.DotProduct(rt) < p1.DotProduct(rt);
                                            DatumEnds show = at0 ? DatumEnds.End0 : DatumEnds.End1, hide = at0 ? DatumEnds.End1 : DatumEnds.End0;
                                            g.ShowBubbleInView(show, c.view);
                                            g.HideBubbleInView(hide, c.view);
                                            bubblesSet++;
                                        }
                                        catch (Exception ex) { bubbleFails.Add($"{c.view.Name} grid {g.Name}: {ex.Message}"); }
                                    }
                            foreach (var c in changed)
                            {
                                var want = titleAt ?? title0[c.vp.Id.Value];
                                var now = c.vp.GetLabelOutline().MinimumPoint;
                                var d = new XYZ(want.X - now.X, want.Y - now.Y, 0);
                                if (d.GetLength() > 1e-6) c.vp.LabelOffset = c.vp.LabelOffset + d;
                            }
                            doc.Regenerate();
                            foreach (var c in changed)
                            {
                                var after = OnSheet(c.vp, c.view); var b0 = before0[c.vp.Id.Value];
                                var want = stack ? stackAt : b0;
                                rows.Add(new JsonObject
                                {
                                    ["sheet"] = c.sheet.SheetNumber, ["view"] = c.view.Name, ["view_id"] = c.view.Id.Value,
                                    ["scope_box_was"] = c.was, ["done"] = c.how,
                                    ["centre_moved_on_sheet_mm"] = Math.Round(Math.Sqrt(Math.Pow(after.X - b0.X, 2) + Math.Pow(after.Y - b0.Y, 2)) * Mm, 1),
                                    ["off_target_mm"] = Math.Round(Math.Sqrt(Math.Pow(after.X - want.X, 2) + Math.Pow(after.Y - want.Y, 2)) * Mm, 2),
                                    ["title_at_mm"] = new JsonArray(Math.Round(c.vp.GetLabelOutline().MinimumPoint.X * Mm, 1), Math.Round(c.vp.GetLabelOutline().MinimumPoint.Y * Mm, 1)),
                                });
                            }
                            if (dry) throw new DryRunDone();
                        });
                    }
                    catch (DryRunDone) { }

                    return new
                    {
                        dry_run = dry,
                        angle_deg = deg,
                        scope_box = new { source = src.Name, turned = boxName, id = dry ? (long?)null : boxId, in_place = inPlace, reused_existing = existing != null },
                        stack_point_on_sheet_mm = stack ? new[] { Math.Round(stackAt.X * Mm, 1), Math.Round(stackAt.Y * Mm, 1) } : null,
                        plans = rows, left_alone = left,
                        grid_bubbles = bubbles == "top_left" ? new { set = bubblesSet, failed = bubbleFails.Take(20).ToList() } : null,
                        sheets_without_plans = noPlan,
                        other_views_on_source_box = inPlace ? elsewhere : null,
                        note = inPlace ? (elsewhere.Count > 0 ? "These views use the same scope box and turned too." : null)
                                       : (elsewhere.Count > 0 ? $"{elsewhere.Count} other views keep \"{src.Name}\" unturned." : null),
                        check = "Text, tags and the north arrow: plan annotation turns with the plan; check text reads the right way up, and turn the sheet's north point by hand.",
                        revit = notes,
                        undo = dry ? "Nothing changed (dry run)." : "One undo step in Revit. Not saved.",
                    };
                }
            };
        }
    }
}
