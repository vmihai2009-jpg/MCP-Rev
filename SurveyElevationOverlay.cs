// 1.29.0: elevation_overlay (Mihai, 2026-11-13: "most of the time the dwgs of the elevations are in one autocad file, have
// mcp overlay the correct dwg survey on the right facade if missing, so it can build easier and the user would be able to
// check in elevation views"; "the folder is given to survey to revit anyway, should use the sources in there"). For each
// facade: its elevation (indexed from the survey folder if not yet), an elevation view looking at it, and the DWG linked into
// that view only, moved so the drawing's lines sit on the facade at their heights, the view cropped to that drawing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        internal static RevitTool ElevationOverlay(IReadOnlyList<RevitTool> own, JsonSerializerOptions json) => new RevitTool
        {
            Name = "elevation_overlay",
            Write = true,
            Description = "Put each facade's survey elevation behind the model in an elevation view, so building and checking work against the " +
                          "drawing: for every facade (or the ones named) its elevation is found (elevation-index.json; facades not yet indexed are " +
                          "indexed from the survey folder Survey to Revit reads, where the elevations usually share one DWG laid out side by side), " +
                          "an elevation view 'Survey <F>' looking at the facade is made if there is none, the DWG is linked into that view only and " +
                          "moved so its lines sit on the facade at their heights (the same fit the facade tools use), and the view is cropped to that " +
                          "drawing. Facades whose view already has the drawing are left alone (replace: true to redo). Reports a drawing that would " +
                          "show mirrored or at another scale instead of placing it.",
            InputSchema = Schema(
                ("facades", Arr("string", "Facade ids (default: every main facade in facades.json)"), false),
                ("replace", S("boolean", "Link again where the view already has the drawing (default false)"), false),
                ("survey_folder", S("string", "Folder of the survey DWGs (default: the one Survey to Revit reads / the linked plans' folder)"), false),
                ("source", S("string", "As survey_fidelity"), false)),
            Run = (app, a) =>
            {
                var doc = Writable(app);
                bool replace = Args.Bool(a, "replace") == true;
                string facadesFile = Path.Combine(ReportsFolder(doc), "facades.json");
                if (!File.Exists(facadesFile)) throw new ArgumentException("Run facade_list first.");
                var only = Args.Strings(a, "facades");
                var facades = JsonNode.Parse(File.ReadAllText(facadesFile))["facades"].AsArray().OfType<JsonObject>()
                    .Where(f => only.Count > 0 ? only.Any(o => o.Equals(f["id"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) : f["main"]?.GetValue<bool>() == true)
                    .Select(f => f["id"].GetValue<string>()).ToList();
                if (facades.Count == 0) throw new ArgumentException("No facades to overlay: give facades ids.");
                var notes = new List<string>();

                // facades with no indexed elevation: index them from the survey folder
                var missing = facades.Where(f => !ElevationSpecs(doc, null).Any(s => string.Equals(Args.Str(s, "facade"), f, StringComparison.OrdinalIgnoreCase))).ToList();
                if (missing.Count > 0)
                {
                    string folder = Args.Str(a, "survey_folder");
                    if (folder == null) { try { folder = ReadingFor(app, doc, a)?.Reading?.Folder; } catch { } }
                    if (folder == null)
                        foreach (var imp in CadLink.All(doc).Where(i => i.ViewSpecific))
                            try { if (CadLink.FilePath(doc, imp) is string fp && File.Exists(fp)) { folder = Path.GetDirectoryName(fp); break; } } catch { }
                    if (folder == null || !Directory.Exists(folder)) notes.Add("Facades with no indexed elevation and no survey folder found: " + string.Join(", ", missing) + " (give survey_folder).");
                    else
                    {
                        var t = own.FirstOrDefault(x => x.Name == "elevation_index");
                        try
                        {
                            var args = new JsonObject { ["paths"] = new JsonArray(folder), ["facades"] = new JsonArray(missing.Select(m => (JsonNode)m).ToArray()) };
                            t?.Run(app, args);
                            notes.Add($"Indexed {string.Join(", ", missing)} from {folder}.");
                        }
                        catch (Exception ex) { notes.Add("elevation_index: " + McpServer.Unwrap(ex).Message); }
                    }
                }

                Func<IEnumerable<string>, Dictionary<string, string>> roles = null;
                try { roles = ReadingFor(app, doc, a)?.Roles; } catch { }
                var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.Elevation)
                          ?? throw new InvalidOperationException("The model has no elevation view type.");
                var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null)
                           .OrderBy(v => v.GenLevel.Elevation).FirstOrDefault() ?? throw new InvalidOperationException("The model has no floor plan view to put the elevation marker in.");
                var results = new List<object>();
                foreach (var fid in facades)
                {
                    var spec = ElevationSpecs(doc, null).FirstOrDefault(s => string.Equals(Args.Str(s, "facade"), fid, StringComparison.OrdinalIgnoreCase));
                    if (spec == null) { results.Add(new { facade = fid, skipped = "no elevation found for it" }); continue; }
                    try { results.Add(Overlay(doc, spec, fid, roles, vft, plan, replace)); }
                    catch (Exception ex) { results.Add(new { facade = fid, failed = McpServer.Unwrap(ex).Message }); }
                }
                return new { facades = results, notes = notes.Count > 0 ? notes : null, next = "Open 'Survey <F>' views: the survey lines sit behind the model. facade_check measures what the eye sees there." };
            }
        };

        private static object Overlay(Document doc, JsonObject spec, string fid, Func<IEnumerable<string>, Dictionary<string, string>> roles, ViewFamilyType vft, ViewPlan plan, bool replace)
        {
            var fr = ElevationFrame(doc, spec, roles);
            var (nx, ny) = Outward(doc, fr);
            string path = Args.Str(spec, "path");
            string vname = $"Survey {fid}";
            var view = new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>().FirstOrDefault(v => !v.IsTemplate && v.Name == vname);
            var existing = view == null ? new List<ImportInstance>() : CadLink.All(doc).Where(i => i.OwnerViewId == view.Id && string.Equals(SafePath(doc, i), path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (existing.Count > 0 && !replace) return new { facade = fid, view = vname, skipped = "the view already has the drawing" };

            var file = DwgFile.Read(path);
            double xs0 = fr.Lines.Min(l => Math.Min(l.A.X, l.B.X)), xs1 = fr.Lines.Max(l => Math.Max(l.A.X, l.B.X));
            double ys0 = fr.Lines.Min(l => Math.Min(l.A.Y, l.B.Y)), ys1 = fr.Lines.Max(l => Math.Max(l.A.Y, l.B.Y));
            XYZ Q(double x, double y) { double u = fr.U(x); return new XYZ((fr.A.X + fr.Ux * u) / Mm, (fr.A.Y + fr.Uy * u) / Mm, fr.Z(y) / Mm); }
            string result = null; long linkId = 0; double scale = 1; bool mirrored = false;
            var txNotes = BuildTools.Tx.Run(doc, $"MCP: elevation overlay {fid}", () =>
            {
                if (existing.Count > 0) doc.Delete(existing.Select(i => i.Id).ToList());
                if (view == null)
                {
                    var mid = new XYZ((fr.A.X + fr.Ux * fr.Length / 2 + nx * 5000) / Mm, (fr.A.Y + fr.Uy * fr.Length / 2 + ny * 5000) / Mm, plan.GenLevel.Elevation);
                    var marker = ElevationMarker.CreateElevationMarker(doc, vft.Id, mid, 100);
                    view = marker.CreateElevation(doc, plan.Id, 0);
                    doc.Regenerate();
                    // turn the marker so the view looks at the facade from outside (ViewDirection points at the viewer)
                    double ang = Math.Atan2(ny, nx) - Math.Atan2(view.ViewDirection.Y, view.ViewDirection.X);
                    if (Math.Abs(ang) > 1e-9) ElementTransformUtils.RotateElement(doc, marker.Id, Line.CreateBound(mid, mid + XYZ.BasisZ), ang);
                    view.Name = vname;
                    doc.Regenerate();
                }
                var opt = new DWGImportOptions { ThisViewOnly = true, Placement = ImportPlacement.Centered, Unit = UnitFor(file.MmPerUnit) };
                if (!doc.Link(path, opt, view, out ElementId id)) throw new InvalidOperationException("Revit would not link " + path);
                var imp = (ImportInstance)doc.GetElement(id); linkId = id.Value;
                doc.Regenerate();
                var T = imp.GetTotalTransform();
                XYZ P(double x, double y) => T.OfPoint(new XYZ(x / Mm, y / Mm, 0));
                XYZ p0 = P(xs0, ys0), p1 = P(xs1, ys0), q0 = Q(xs0, ys0), q1 = Q(xs1, ys0);
                scale = q0.DistanceTo(q1) / Math.Max(1e-9, p0.DistanceTo(p1));
                mirrored = (p1 - p0).DotProduct(q1 - q0) < 0;
                if (mirrored || Math.Abs(scale - 1) > 0.002) { doc.Delete(id); linkId = 0; result = mirrored ? "the drawing would read mirrored in this view: not placed" : $"the drawing is at another scale than the model ({scale:0.000}): not placed"; return; }
                var d = q0 - p0; var vd = view.ViewDirection; d = d - vd.Multiply(d.DotProduct(vd));
                if (d.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, id, d);
                imp.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"MCP|elevation_overlay|{fid}");
                // crop to this drawing, with a margin
                var cb = view.CropBox; var inv = cb.Transform.Inverse;
                var corners = new[] { Q(xs0, ys0), Q(xs1, ys0), Q(xs0, ys1), Q(xs1, ys1) }.Select(p => inv.OfPoint(p)).ToList();
                double m = 1000 / Mm;
                cb.Min = new XYZ(corners.Min(c => c.X) - m, corners.Min(c => c.Y) - m, cb.Min.Z);
                cb.Max = new XYZ(corners.Max(c => c.X) + m, corners.Max(c => c.Y) + m, cb.Max.Z);
                view.CropBoxActive = true; view.CropBox = cb;
            });
            return new
            {
                facade = fid, view = vname, viewId = view?.Id.Value, link = linkId == 0 ? (long?)null : linkId, elevation = Args.Str(spec, "name"),
                placed = linkId != 0, problem = result, notes = txNotes.Count > 0 ? txNotes.Take(3).ToList() : null,
            };
        }

        private static string SafePath(Document doc, ImportInstance i) { try { return CadLink.FilePath(doc, i); } catch { return null; } }

        private static ImportUnit UnitFor(double mmPerUnit) =>
            Math.Abs(mmPerUnit - 1000) < 1e-6 ? ImportUnit.Meter : Math.Abs(mmPerUnit - 10) < 1e-6 ? ImportUnit.Centimeter
            : Math.Abs(mmPerUnit - 304.8) < 1e-6 ? ImportUnit.Foot : Math.Abs(mmPerUnit - 25.4) < 1e-6 ? ImportUnit.Inch : ImportUnit.Millimeter;
    }
}
