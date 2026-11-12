// 1.17.0: facade_detail_map. Names every piece of facade detail an elevation draws (bands, rustication, window
// surrounds, sills, hoods, pediments, keystones, consoles, pilasters, quoins, balustrades, and what is left over),
// groups repeats into types, says how to build each and where its depth comes from, and, given a reference model
// (a person's model of the same building), says how that person built each item and how far it projects.
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
        public static IEnumerable<RevitTool> FacadeMapTools()
        {
            yield return new RevitTool
            {
                Name = "facade_detail_map",
                Description = "Read one facade's elevation and name every piece of detail it draws, so you know what to model before modelling: " +
                              "bands (cornice, entablature, string course, plinth) with their line heights and runs; rustication (joint heights, pitch); " +
                              "per window: architrave (flat or moulded, width, steps), sill (horns), hood, consoles, pediment (triangular or curved, " +
                              "span, rise), keystone; pilasters (width, height, capital/base lines); quoins (long/short, course); balustrades " +
                              "(balusters, pitch, rails); modillions/consoles under bands; and every shape left over (unclassified, with its box). " +
                              "Repeats are grouped into types (T1...): make each type once, then copy it. Each item says how to build it and where its " +
                              "depth comes from (an elevation shows no depth). Positions: u = mm along the facade from its left end seen from outside, " +
                              "z = mm above datum and model mm. With reference (a reference_open name): for each item, the reference model's elements " +
                              "there (family/type, in-place or not, how far they project), and the reference's facade elements no item explains. " +
                              "image true draws the elevation with each item boxed. Writes facade-map-<facade>.json. Changes nothing. " +
                              "Needs facade_list and the facade in elevation-index.json.",
                InputSchema = Schema(
                    ("facade", S("string", "Facade id from facade_list (F29...)"), true),
                    ("reference", S("string", "A reference model's name (reference_open) to learn from: how it built each item, and its projections. \"active\": our own model, to see which items have nothing built yet"), false),
                    ("image", S("boolean", "Return a drawing of the elevation with every item boxed (default false)"), false),
                    ("kinds", Arr("string", "List only these kinds (e.g. pilaster, architrave (moulded), unclassified)"), false),
                    ("max_items", S("integer", "Most items to list (default 150; the JSON file has them all)"), false),
                    ("band_cover", S("number", "Share of the facade a line must run along to be a band (default 0.25)"), false),
                    ("ignore_layers", Arr("string", "Extra layer globs to leave out (scaffold, signs, a neighbour's building)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    string fid = Args.Str(a, "facade") ?? throw new ArgumentException("facade is needed (an id from facade_list).");
                    var spec = ElevationSpecs(doc, null).FirstOrDefault(s => string.Equals(Args.Str(s, "facade"), fid, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"No elevation for {fid} in elevation-index.json: run elevation_index (or add one by hand).");
                    var geo = ElevationGeometry(doc, spec);
                    var opts = new FacadeMapOptions();
                    if (Args.Dbl(a, "band_cover") is double bc) opts.BandCover = bc;
                    var extra = Args.Strings(a, "ignore_layers");
                    if (extra.Count > 0) opts.IgnoreLayers = opts.IgnoreLayers.Concat(extra).ToArray();
                    var facadeLines = geo.lines.Select(l => new DwgLine
                    {
                        Layer = l.Layer,
                        A = new Pt(geo.along.Along(l.A.X * geo.heights.A), geo.heights.Height(l.A.Y)),
                        B = new Pt(geo.along.Along(l.B.X * geo.heights.A), geo.heights.Height(l.B.Y)),
                    }).ToList();
                    var map = FacadeMap.Read(facadeLines, geo.length, opts);

                    // what a person's model has on this facade
                    object refSummary = null;
                    var refByItem = new Dictionary<string, List<RefEl>>();
                    if (Args.Str(a, "reference") is string refName)
                    {
                        // "active": our own model, to see which items nothing has been built for yet
                        bool own = refName.Equals("active", StringComparison.OrdinalIgnoreCase);
                        var refDoc = own ? doc : Reference.Get(refName);
                        var (move, fit) = own ? (Transform.Identity, (object)"our own model") : ReferenceTools.Alignment(doc, refDoc, new JsonObject());
                        var els = FacadeElements(refDoc, move, geo.fa, geo.fb, geo.datumMm);
                        var explained = new HashSet<long>();
                        foreach (var it in map.Items)
                        {
                            var hit = els.Where(e => Overlap(it, e) >= 0.3).ToList();
                            if (hit.Count == 0) continue;
                            refByItem[it.Id] = hit;
                            foreach (var e in hit) explained.Add(e.Id);
                        }
                        refSummary = new
                        {
                            alignment = fit, elementsOnFacade = els.Count, explainedByItems = explained.Count,
                            howTheyBuiltIt = els.GroupBy(e => (e.Category, e.Label, e.InPlace)).OrderByDescending(g => g.Count()).Take(40).Select(g => new
                            {
                                category = g.Key.Category, label = g.Key.Label, inPlace = g.Key.InPlace, count = g.Count(),
                                projectionMm = Math.Round(g.Max(e => e.Out)), heightMm = Math.Round(g.Average(e => e.Z1 - e.Z0)), widthMm = Math.Round(g.Average(e => e.U1 - e.U0)),
                            }).ToList(),
                            notInMap = els.Where(e => !explained.Contains(e.Id)).GroupBy(e => e.Label).OrderByDescending(g => g.Count()).Take(25).Select(g => new
                            {
                                label = g.Key, count = g.Count(), ids = g.Take(8).Select(e => e.Id).ToList(),
                                at = g.Take(4).Select(e => new[] { Math.Round(e.U0), Math.Round(e.U1), Math.Round(e.Z0), Math.Round(e.Z1) }).ToList(),
                            }).ToList(),
                            note = "Reference boxes are in the facade's u/z (mm along, mm above datum); projectionMm is how far past our facade line the reference's element reaches: use it as the depth the elevation cannot give.",
                        };
                    }

                    var kinds = Args.Strings(a, "kinds");
                    int max = Args.Int(a, "max_items", 150);
                    object ItemOut(FacadeItem i) => new
                    {
                        id = i.Id, kind = i.Kind, type = i.Type, window = i.Window, count = i.Count > 1 ? i.Count : (int?)null,
                        u = new[] { Math.Round(i.U0), Math.Round(i.U1) }, aodMm = new[] { Math.Round(i.Z0), Math.Round(i.Z1) },
                        modelMm = new[] { Math.Round(i.Z0 - geo.datumMm), Math.Round(i.Z1 - geo.datumMm) },
                        size = i.Size.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
                        lineHeightsAod = i.LineHeights, runs = i.Runs?.Select(r => new[] { Math.Round(r.u0), Math.Round(r.u1) }).ToList(),
                        note = i.Note, build = i.Build, depth = i.Depth,
                        reference = refByItem.TryGetValue(i.Id, out var rs) ? rs.Take(5).Select(e => new { e.Id, e.Category, e.Label, e.InPlace, projectionMm = Math.Round(e.Out) }).ToList() : null,
                    };
                    var listed = map.Items.Where(i => kinds.Count == 0 || kinds.Any(k => string.Equals(k, i.Kind, StringComparison.OrdinalIgnoreCase))).ToList();
                    var answer = new
                    {
                        facade = fid, elevation = Args.Str(spec, "name"), lengthMm = Math.Round(geo.length),
                        windows = map.Windows.Count,
                        detailLines = map.DetailLines, explainedLines = map.Explained,
                        explainedShare = map.DetailLines == 0 ? 0 : Math.Round((double)map.Explained / map.DetailLines, 2),
                        kinds = map.Items.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Sum(i => i.Count)),
                        types = map.Types.Select(t => new { t.Id, t.Kind, t.Count, t.Signature, items = t.Items.Take(12).ToList() }).ToList(),
                        layers = map.Layers.OrderByDescending(kv => kv.Value).Take(30).Select(kv => new { layer = kv.Key, lines = kv.Value, use = map.LayerUse.TryGetValue(kv.Key, out var u) ? u : null }).ToList(),
                        items = listed.Take(max).Select(ItemOut).ToList(),
                        notListed = Math.Max(0, listed.Count - max),
                        windowBoxes = map.Windows.Select(w => new { w.Index, u = new[] { Math.Round(w.U0), Math.Round(w.U1) }, aodMm = new[] { Math.Round(w.Z0), Math.Round(w.Z1) } }).ToList(),
                        reference = refSummary,
                        next = "Use this to know what the facade holds before building: facade_profile for the bands (wall sweeps), facade_columns for pilasters/columns, window_look for windows, facade_relief for the stonework (a surround family per window design). Then check type by type, most repeated first, that each type became one family placed at every item of it, and view_image the facade. " +
                               "A large unclassified share means the elevation uses layers or shapes the map does not know: look with image true and say what they are in your report. " +
                               (refSummary == null ? "Run again with reference (a reference_open name) to see how the human model built each item and how far each projects." : ""),
                    };
                    string folder = ReportsFolder(doc);
                    string file = Path.Combine(folder, $"facade-map-{fid}.json");
                    var full = new { answer.facade, answer.elevation, answer.lengthMm, items = map.Items.Select(ItemOut).ToList(), answer.types, answer.windowBoxes, answer.reference };
                    File.WriteAllText(file, JsonSerializer.Serialize(full, JsonOpts));
                    string text = JsonSerializer.Serialize(new { answer, file }, JsonOpts);
                    if (Args.Bool(a, "image") != true) return text;

                    var img = new LineImage(2000);
                    foreach (var l in facadeLines)
                        img.Add(l.A.X, l.A.Y, l.B.X, l.B.Y, DwgFile.LayerMatches(l.Layer, opts.WindowLayers) ? LineImage.Blue : LineImage.Grey);
                    foreach (var i in map.Items)
                    {
                        var c = i.Kind == "unclassified" ? LineImage.Red : (i.Runs != null ? ((byte)0, (byte)150, (byte)60) : ((byte)200, (byte)110, (byte)0));
                        double p = 15;
                        img.Add(i.U0 - p, i.Z0 - p, i.U1 + p, i.Z0 - p, c, 2); img.Add(i.U1 + p, i.Z0 - p, i.U1 + p, i.Z1 + p, c, 2);
                        img.Add(i.U1 + p, i.Z1 + p, i.U0 - p, i.Z1 + p, c, 2); img.Add(i.U0 - p, i.Z1 + p, i.U0 - p, i.Z0 - p, c, 2);
                    }
                    var png = img.Png();
                    string pngFile = Path.Combine(folder, $"facade-map-{fid}.png");
                    File.WriteAllBytes(pngFile, png);
                    var content = new ToolContent { Text = text + "\nImage: grey = detail lines, blue = window lines, green boxes = bands and rustication, orange = other items, red = unclassified. " + pngFile };
                    content.Images.Add(("image/png", png));
                    return content;
                }
            };
        }

        private class RefEl
        {
            public long Id; public string Category, Label; public bool InPlace;
            public double U0, U1, Z0, Z1, Out;
        }

        /// <summary>Overlap of an item's box and an element's box on the facade, as a share of the smaller box.</summary>
        private static double Overlap(FacadeItem i, RefEl e)
        {
            double du = Math.Min(i.U1, e.U1) - Math.Max(i.U0, e.U0), dz = Math.Min(i.Z1, e.Z1) - Math.Max(i.Z0, e.Z0);
            if (du <= 0 || dz <= 0) return 0;
            double ai = Math.Max(1, (i.U1 - i.U0) * Math.Max(20, i.Z1 - i.Z0)), ae = Math.Max(1, (e.U1 - e.U0) * (e.Z1 - e.Z0));
            return du * dz / Math.Min(ai, ae);
        }

        /// <summary>
        /// A reference model's elements on a facade (detail categories), boxes moved into our frame and given in the
        /// facade's u (along), z (above datum) and how far they stand out past our facade line.
        /// </summary>
        private static List<RefEl> FacadeElements(Document refDoc, Transform move, Pt fa, Pt fb, double datumMm)
        {
            var cats = new[] { BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Windows,
                               BuiltInCategory.OST_Doors, BuiltInCategory.OST_StairsRailing, BuiltInCategory.OST_Cornices, BuiltInCategory.OST_Reveals,
                               BuiltInCategory.OST_Mass, BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_Walls };
            double fl = fa.Dist2D(fb), ux = (fb.X - fa.X) / fl, uy = (fb.Y - fa.Y) / fl;
            var result = new List<RefEl>();
            foreach (var bic in cats)
                foreach (var e in new FilteredElementCollector(refDoc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (e is FamilyInstance fi && fi.SuperComponent != null) continue;
                    var bb = e.get_BoundingBox(null);
                    if (bb == null) continue;
                    double u0 = double.MaxValue, u1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue, o0 = double.MaxValue, o1 = double.MinValue;
                    foreach (var x in new[] { bb.Min.X, bb.Max.X })
                        foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                            foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                            {
                                var p = move.OfPoint(bb.Transform.OfPoint(new XYZ(x, y, z)));
                                double px = p.X * Mm, py = p.Y * Mm;
                                double u = (px - fa.X) * ux + (py - fa.Y) * uy, off = (px - fa.X) * uy - (py - fa.Y) * ux; // outward = (uy, -ux)
                                u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); z0 = Math.Min(z0, p.Z * Mm + datumMm); z1 = Math.Max(z1, p.Z * Mm + datumMm);
                                o0 = Math.Min(o0, off); o1 = Math.Max(o1, off);
                            }
                    // on this facade: along it, and reaching out past its face (walls: only those standing on the face line)
                    if (u1 < -300 || u0 > fl + 300 || o1 < -50 || o0 > 1500) continue;
                    if (bic == BuiltInCategory.OST_Walls && (o1 - o0 > 1200 || u1 - u0 > fl * 0.5)) continue;
                    result.Add(new RefEl
                    {
                        Id = e.Id.Value, Category = e.Category?.Name ?? bic.ToString(), Label = ReferenceTools.Label(refDoc, e),
                        InPlace = (e as FamilyInstance)?.Symbol?.Family?.IsInPlace == true,
                        U0 = Math.Max(0, u0), U1 = Math.Min(fl, u1), Z0 = z0, Z1 = z1, Out = Math.Max(0, o1),
                    });
                }
            return result;
        }
    }
}
