// 1.11.0: Claude can look at the model. view_image exports a view as a PNG and returns it as MCP image content;
// check_view makes (or re-aims) one view for that: a facade seen square on, a survey section, or a box round elements.
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
        private const string CheckView3D = "BGMCP check", CheckViewSection = "BGMCP check section";

        public static IEnumerable<RevitTool> Views()
        {
            yield return new RevitTool
            {
                Name = "view_image",
                Description = "Look at the model: a view exported as a PNG and returned as an image (the active view by default; any plan, section, elevation, 3D view or sheet by id or name). Use it to compare the model with a survey elevation or section by eye, after check_view has aimed a view at a facade, a section or some elements. Changes nothing (the PNG is written to the reports folder).",
                InputSchema = Schema(
                    ("view_id", S("integer", "The view (default the active view)"), false),
                    ("view_name", S("string", "Or its name"), false),
                    ("width_px", S("integer", "Image width (default 1600, at most 4096)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    View view;
                    if (Args.Long(a, "view_id") is long id) view = doc.GetElement(new ElementId(id)) as View ?? throw new ArgumentException($"{id} is not a view.");
                    else if (Args.Str(a, "view_name") is string vn)
                        view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(vn, StringComparison.OrdinalIgnoreCase))
                               ?? throw new ArgumentException($"No view called \"{vn}\". list_views shows them.");
                    else view = doc.ActiveView ?? throw new InvalidOperationException("No active view.");
                    if (view.IsTemplate || !view.CanBePrinted) throw new ArgumentException($"\"{view.Name}\" can't be exported as an image (a template, schedule or browser view).");
                    int width = Math.Clamp(Args.Int(a, "width_px", 1600), 200, 4096);
                    var (file, png) = ExportPng(doc, view, width);
                    int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
                    var c = new ToolContent
                    {
                        Text = new JsonObject
                        {
                            ["view"] = view.Name, ["view_id"] = view.Id.Value, ["type"] = view.ViewType.ToString(),
                            ["widthPx"] = w, ["heightPx"] = h, ["file"] = file,
                            ["note"] = "The model as Revit draws this view. Compare with the survey drawing; survey_section with image true draws a section and the model cut over each other.",
                        }.ToJsonString(),
                    };
                    c.Images.Add(("image/png", png));
                    return c;
                }
            };

            yield return new RevitTool
            {
                Name = "check_view",
                Write = true,
                Description = "Aim one view for view_image (and for a person): facade = a facade id from facade_list, seen square on from outside, hidden line, boxed to that facade (compare with its elevation); section = a survey section's name (from section-index.json or Survey to Revit's views) or cut_line, as a section view looking the way the survey draws it; element_ids = a 3D view boxed round those elements, from the south-east. Reuses the views \"BGMCP check\" and \"BGMCP check section\". One undo step. Returns the view id: then view_image.",
                InputSchema = Schema(
                    ("facade", S("string", "A facade id (F1...) from facade_list"), false),
                    ("section", S("string", "A survey section's name or letter"), false),
                    ("cut_line", Arr("number", "Or [x1, y1, x2, y2] model mm: a section along it"), false),
                    ("flip", S("boolean", "Look the other way along the section (default false)"), false),
                    ("element_ids", Arr("integer", "Or elements to box in a 3D view"), false),
                    ("margin_mm", S("number", "Space round the facade or elements (default 1000)"), false),
                    ("open", S("boolean", "Also make it the active view (default false)"), false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    double margin = (Args.Dbl(a, "margin_mm") ?? 1000) / Mm;
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                    double zLo = (levels.Count > 0 ? levels.Min(l => l.Elevation) : 0) - 3000 / Mm, zHi = (levels.Count > 0 ? levels.Max(l => l.Elevation) : 0) + 15000 / Mm;
                    View made = null; string what = null;
                    var notes = BuildTools.Tx.Run(doc, "MCP: aim check view", () =>
                    {
                        if (Args.Str(a, "facade") is string fid)
                        {
                            var (fa, fb) = SavedFacade(doc, fid);
                            // the line runs left to right seen from outside, so outside is to its right
                            double len = fa.Dist2D(fb), dx = (fb.X - fa.X) / len, dy = (fb.Y - fa.Y) / len;
                            var outN = new XYZ(dy, -dx, 0);
                            var mid = new XYZ((fa.X + fb.X) / 2 / Mm, (fa.Y + fb.Y) / 2 / Mm, (zLo + zHi) / 2);
                            var v = Check3D(doc);
                            v.SetOrientation(new ViewOrientation3D(mid + outN.Multiply(30000 / Mm), XYZ.BasisZ, outN.Negate()));
                            Box(v, new[] { new XYZ(fa.X / Mm, fa.Y / Mm, zLo), new XYZ(fb.X / Mm, fb.Y / Mm, zHi) }, margin, outN, 2000 / Mm);
                            made = v; what = $"facade {fid} from outside";
                        }
                        else if (Args.Str(a, "section") != null || Args.Numbers(a, "cut_line").Count == 4)
                        {
                            Pt A, B;
                            var cl = Args.Numbers(a, "cut_line");
                            if (cl.Count == 4) { A = new Pt(cl[0], cl[1]); B = new Pt(cl[2], cl[3]); }
                            else
                            {
                                var spec = SectionSpecs(doc, new JsonObject { ["sections"] = new JsonArray(Args.Str(a, "section")) }, new List<string>()).FirstOrDefault()
                                           ?? throw new ArgumentException($"No section \"{Args.Str(a, "section")}\": run section_index, or give cut_line.");
                                A = spec.A; B = spec.B;
                            }
                            if (Args.Bool(a, "flip") == true) (A, B) = (B, A);
                            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.Section)
                                       ?? throw new InvalidOperationException("The project has no section view type.");
                            var old = new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>().FirstOrDefault(v => !v.IsTemplate && v.Name == CheckViewSection);
                            if (old != null && doc.ActiveView?.Id != old.Id) doc.Delete(old.Id);
                            var a3 = new XYZ(A.X / Mm, A.Y / Mm, 0); var b3 = new XYZ(B.X / Mm, B.Y / Mm, 0);
                            var dir = (b3 - a3).Normalize();
                            var tr = Transform.Identity;
                            tr.Origin = (a3 + b3) / 2; tr.BasisX = dir; tr.BasisY = XYZ.BasisZ; tr.BasisZ = dir.CrossProduct(XYZ.BasisZ);
                            double half = a3.DistanceTo(b3) / 2 + margin;
                            // as Survey to Revit makes its section views: the cut through the line, looking 30 m deep
                            var box = new BoundingBoxXYZ { Transform = tr, Min = new XYZ(-half, zLo, 0), Max = new XYZ(half, zHi, 30000 / Mm) };
                            var v = ViewSection.CreateSection(doc, type.Id, box);
                            try { v.Name = CheckViewSection; } catch { }
                            v.DetailLevel = ViewDetailLevel.Fine;
                            made = v; what = "section along " + $"[{A.X:0}, {A.Y:0}] to [{B.X:0}, {B.Y:0}]";
                        }
                        else
                        {
                            var ids = Args.Numbers(a, "element_ids").Select(x => new ElementId((long)x)).ToList();
                            var boxes = ids.Select(i => doc.GetElement(i)?.get_BoundingBox(null)).Where(bb => bb != null).ToList();
                            if (boxes.Count == 0) throw new ArgumentException("Give facade, section, cut_line or element_ids.");
                            var v = Check3D(doc);
                            var pts = boxes.SelectMany(bb => new[] { bb.Min, bb.Max }).ToList();
                            var c = new XYZ(pts.Average(p => p.X), pts.Average(p => p.Y), pts.Average(p => p.Z));
                            var fwd = new XYZ(-1, 1, -0.7).Normalize();
                            var up = fwd.CrossProduct(XYZ.BasisZ).CrossProduct(fwd).Normalize();
                            v.SetOrientation(new ViewOrientation3D(c - fwd.Multiply(30000 / Mm), up, fwd));
                            Box(v, pts, margin, null, 0);
                            made = v; what = $"{boxes.Count} elements from the south-east";
                        }
                    });
                    if (Args.Bool(a, "open") == true) try { new Autodesk.Revit.UI.UIDocument(doc).RequestViewChange(made); } catch { }
                    return new { view_id = made.Id.Value, view = made.Name, shows = what, revit = notes, next = $"view_image view_id {made.Id.Value}", undo = "One undo step in Revit." };
                }
            };
        }

        private static View3D Check3D(Document doc)
        {
            var v = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(x => !x.IsTemplate && x.Name == CheckView3D);
            if (v == null)
            {
                var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)
                           ?? throw new InvalidOperationException("The project has no 3D view type.");
                v = View3D.CreateIsometric(doc, type.Id);
                try { v.Name = CheckView3D; } catch { }
            }
            if (v.IsPerspective) throw new InvalidOperationException($"\"{CheckView3D}\" is a perspective view: rename or delete it.");
            v.DetailLevel = ViewDetailLevel.Fine;
            try { v.DisplayStyle = DisplayStyle.HLR; } catch { }
            return v;
        }

        /// <summary>A section box round the points (axis-aligned), grown by margin; depthNormal/depth: extra depth behind and in front of a facade.</summary>
        private static void Box(View3D v, IEnumerable<XYZ> pts, double margin, XYZ depthNormal, double depth)
        {
            var list = pts.ToList();
            if (depthNormal != null) list = list.SelectMany(p => new[] { p + depthNormal.Multiply(depth), p - depthNormal.Multiply(depth) }).ToList();
            var min = new XYZ(list.Min(p => p.X) - margin, list.Min(p => p.Y) - margin, list.Min(p => p.Z) - margin);
            var max = new XYZ(list.Max(p => p.X) + margin, list.Max(p => p.Y) + margin, list.Max(p => p.Z) + margin);
            v.IsSectionBoxActive = true;
            v.SetSectionBox(new BoundingBoxXYZ { Min = min, Max = max });
        }

        /// <summary>A view as a PNG, written to the reports folder (Revit names the file after the view).</summary>
        private static (string file, byte[] png) ExportPng(Document doc, View view, int widthPx)
        {
            string folder = Path.Combine(ReportsFolder(doc), "views");
            Directory.CreateDirectory(folder);
            string stem = $"view-{DateTime.Now:yyyyMMdd-HHmmss}";
            var opts = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews, FilePath = Path.Combine(folder, stem), ZoomType = ZoomFitType.FitToPage,
                PixelSize = widthPx, FitDirection = FitDirectionType.Horizontal, ImageResolution = ImageResolution.DPI_150,
                HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
            };
            opts.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(opts);
            var file = new DirectoryInfo(folder).GetFiles(stem + "*.png").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                       ?? throw new InvalidOperationException("Revit exported no image for that view.");
            return (file.FullName, File.ReadAllBytes(file.FullName));
        }
    }
}
