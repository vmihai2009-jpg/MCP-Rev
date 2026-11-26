using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using BarrGazetas.RevitMcp.Survey;
using Seg = BarrGazetas.RevitMcp.Survey.ModelShapes.Seg;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// Model Library phase 2 (docs/MODEL-LIBRARY.md): how a finished model's facade detail was built. model_profiles reads
    /// the moulding profiles (as lines and arcs, normalised to height 1), model_cut cuts the model by a plane and keeps
    /// curves as curves, model_facade finds every facade detail and measures how far it stands out for its height.
    /// Read-only, in the learning group with the phase 1 tools. Arithmetic in Survey/ModelShapes.cs.
    /// </summary>
    internal static partial class ModelAnalysisTools
    {
        private static IEnumerable<RevitTool> FacadeTools()
        {
            yield return new RevitTool
            {
                Name = "model_profiles",
                Description = "The moulding profiles a model uses (Profile families, e.g. on wall sweeps): how many sweeps use each and where (side, " +
                              "horizontal or vertical, length), the profile's size and depth-to-height ratio, how many lines and arcs it has, and with " +
                              "shape: true the outline itself as lines and arcs normalised to height 1 (arcs stay arcs), ready to reuse at any size.",
                InputSchema = McpServer.Obj(
                    ("family", "string", "Only profile families whose name contains this", false),
                    ("limit", "integer", "Most profiles to read, most used first (default 20)", false),
                    ("shape", "boolean", "Include the normalised outline (default false)", false),
                    ("max_segments", "integer", "Most lines/arcs per outline (default 80)", false)),
                Run = (app, a) => Profiles(Doc(app), a),
            };

            yield return new RevitTool
            {
                Name = "model_cut",
                Description = "Cut the model by a plane and get each element's outline in that plane as lines and arcs (mm): a vertical plane " +
                              "through a facade gives the section of cornices, bands, sills and walls as built; a horizontal one gives a plan. " +
                              "u = along the plane, v = height (model mm) for a vertical cut; u, v = x, y from origin for a horizontal cut.",
                InputSchema = McpServer.Obj(
                    ("origin", "number[]", "A point on the plane [x, y, z] mm", true),
                    ("normal", "number[]", "The plane's normal [x, y, z]; [0,0,1] for a plan cut", true),
                    ("categories", "string[]", "Categories to cut (default walls, generic models, columns, windows, doors, floors, roofs)", false),
                    ("element_ids", "integer[]", "Or just these elements", false),
                    ("near_mm", "number", "For a vertical cut, only elements within this distance along the plane of origin (default: all)", false),
                    ("limit", "integer", "Most elements (default 40)", false),
                    ("segments", "boolean", "Include the outlines (default true); false gives only each element's extent", false)),
                Run = (app, a) => CutTool(Doc(app), a),
            };

            yield return new RevitTool
            {
                Name = "model_facade",
                Description = "Every facade detail of a model (wall sweeps, in-place and loadable families, generic models on the external walls), grouped " +
                              "per facade and classified (cornice, string course, architrave, pilaster, pediment, console, keystone, sill, balustrade...) " +
                              "from its name, else its shape (marked guessed). For each kind: count, height, projection in front of the wall face and " +
                              "depth ratio (projection / height), which is what a new model needs when a section is missing. Facades are located by their face line.",
                InputSchema = McpServer.Obj(
                    ("facades", "integer", "Most facades to list, longest first (default 12)", false),
                    ("min_projection_mm", "number", "Pieces standing out less than this are counted as flush (default 5)", false),
                    ("list", "integer", "Example ids per kind (default 0)", false)),
                Run = (app, a) => Facade(Doc(app), a),
            };
        }

        // ---------------------------------------------------------------- profiles

        private static object Profiles(Document doc, JsonObject a)
        {
            string only = Args.Str(a, "family");
            int limit = Math.Clamp(Args.Int(a, "limit", 20), 1, 100), maxSeg = Math.Clamp(Args.Int(a, "max_segments", 80), 4, 400);
            bool shape = Args.Bool(a, "shape") == true;

            var sweeps = new FilteredElementCollector(doc).OfClass(typeof(WallSweep)).Cast<WallSweep>().ToList();
            var usage = new Dictionary<long, List<(WallSweep s, WallSweepInfo info)>>();
            foreach (var s in sweeps)
            {
                WallSweepInfo info;
                try { info = s.GetWallSweepInfo(); } catch { continue; }
                var sym = doc.GetElement(info.ProfileId) as FamilySymbol;
                long fid = sym?.Family?.Id.Value ?? -1;
                if (!usage.TryGetValue(fid, out var l)) usage[fid] = l = new List<(WallSweep, WallSweepInfo)>();
                l.Add((s, info));
            }

            var fams = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => f.FamilyCategory?.Id.Value == (long)BuiltInCategory.OST_ProfileFamilies)
                .Where(f => only == null || f.Name.IndexOf(only, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(f => usage.TryGetValue(f.Id.Value, out var l) ? l.Count : 0).ThenBy(f => f.Name).ToList();

            var items = new List<object>();
            foreach (var f in fams.Take(limit))
            {
                usage.TryGetValue(f.Id.Value, out var used);
                used ??= new List<(WallSweep, WallSweepInfo)>();
                List<Seg> segs = null; string error = null;
                if (f.IsEditable)
                {
                    Document fdoc = null;
                    try
                    {
                        fdoc = doc.EditFamily(f);
                        segs = new List<Seg>();
                        foreach (var ce in new FilteredElementCollector(fdoc).OfClass(typeof(CurveElement)).Cast<CurveElement>())
                        {
                            if (ce.CurveElementType != CurveElementType.ModelCurve && ce.CurveElementType != CurveElementType.DetailCurve &&
                                ce.CurveElementType != CurveElementType.SymbolicCurve) continue;
                            segs.AddRange(ToSegs(ce.GeometryCurve, p => (p.X * Mm, p.Y * Mm)));
                        }
                    }
                    catch (Exception ex) { error = McpServer.Unwrap(ex).Message; }
                    finally { try { fdoc?.Close(false); } catch { } }
                }
                else error = "not editable";
                var size = segs == null ? null : ModelShapes.Measure(segs);
                items.Add(new
                {
                    family = f.Name,
                    types = f.GetFamilySymbolIds().Count,
                    sweeps = used.Count,
                    lengthM = Math.Round(used.Sum(u => (u.s.LookupParameter("Length")?.AsDouble() ?? 0) * Mm) / 1000, 1),
                    horizontal = used.Count(u => !u.info.IsVertical), vertical = used.Count(u => u.info.IsVertical),
                    reveals = used.Count(u => u.info.WallSweepType == WallSweepType.Reveal),
                    sides = ModelStats.Top(used.Select(u => u.info.WallSide.ToString()), 2),
                    size,
                    outline = shape && segs != null ? ModelShapes.Normalise(segs).Take(maxSeg).ToList() : null,
                    error,
                });
            }
            int defaultProfile = usage.TryGetValue(-1, out var dflt) ? dflt.Count : 0;
            return new
            {
                profileFamilies = fams.Count,
                wallSweeps = sweeps.Count,
                sweepsWithDefaultProfile = defaultProfile,
                profiles = items,
                note = "size: width = how far it stands out, height = its height, depthRatio = width / height. outline: normalised to height 1, " +
                       "origin at the box's lower left; arcs as {k:arc, a, b, c (centre), r, m (a point halfway)}.",
            };
        }

        // ---------------------------------------------------------------- cut

        private static object CutTool(Document doc, JsonObject a)
        {
            var o = Args.Numbers(a, "origin"); var nn = Args.Numbers(a, "normal");
            if (o.Count != 3 || nn.Count != 3) throw new ArgumentException("origin [x, y, z] and normal [x, y, z] are needed.");
            var origin = new XYZ(o[0] / Mm, o[1] / Mm, o[2] / Mm);
            var normal = new XYZ(nn[0], nn[1], nn[2]);
            if (normal.GetLength() < 1e-9) throw new ArgumentException("normal must not be zero.");
            normal = normal.Normalize();
            bool vertical = Math.Abs(normal.Z) < 0.9;
            XYZ ex = vertical ? new XYZ(-normal.Y, normal.X, 0).Normalize() : XYZ.BasisX, ey = vertical ? XYZ.BasisZ : XYZ.BasisY;
            (double, double) UV(XYZ p) { var d = p - origin; return (d.DotProduct(ex) * Mm, vertical ? p.Z * Mm : d.DotProduct(ey) * Mm); }
            int limit = Math.Clamp(Args.Int(a, "limit", 40), 1, 300);
            bool withSegs = Args.Bool(a, "segments") != false;
            double? near = Args.Dbl(a, "near_mm");

            List<Element> els;
            var ids = Args.Numbers(a, "element_ids");
            if (ids.Count > 0) els = ids.Select(i => doc.GetElement(new ElementId((long)i))).Where(e => e != null).ToList();
            else
            {
                var names = Args.Strings(a, "categories");
                var cats = names.Count > 0
                    ? names.Select(n => ReferenceTools.ResolveCategory(doc, n).Id).ToList()
                    : new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_Columns, BuiltInCategory.OST_Windows,
                              BuiltInCategory.OST_Doors, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs }.Select(c => new ElementId(c)).ToList();
                els = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType().ToList();
            }
            var plane = Plane.CreateByNormalAndOrigin(normal, origin);
            var crossing = els.Where(e =>
            {
                var bb = e.get_BoundingBox(null);
                if (bb == null) return false;
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (var x in new[] { bb.Min.X, bb.Max.X }) foreach (var y in new[] { bb.Min.Y, bb.Max.Y }) foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                {
                    double d = (new XYZ(x, y, z) - origin).DotProduct(normal);
                    lo = Math.Min(lo, d); hi = Math.Max(hi, d);
                }
                if (lo > 0 || hi < 0) return false;
                if (near is double nm && vertical)
                {
                    var c = (bb.Min + bb.Max) / 2;
                    double u = (c - origin).DotProduct(ex) * Mm, half = Math.Max(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y) * Mm / 2;
                    if (Math.Abs(u) - half > nm) return false;
                }
                return true;
            }).ToList();

            var outs = new List<object>();
            foreach (var e in crossing.Take(limit))
            {
                var segs = new List<Seg>();
                foreach (var s in SolidsOf(e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine })))
                {
                    Solid part;
                    try { part = BooleanOperationsUtils.CutWithHalfSpace(s, plane); } catch { continue; }
                    if (part == null || part.Faces.Size == 0) continue;
                    foreach (Face f in part.Faces)
                    {
                        if (!(f is PlanarFace pf) || pf.FaceNormal.DotProduct(normal) > -0.999) continue;
                        if (Math.Abs((pf.Origin - origin).DotProduct(normal)) > 1e-4) continue;
                        foreach (var loop in pf.GetEdgesAsCurveLoops()) foreach (var c in loop) segs.AddRange(ToSegs(c, UV));
                    }
                }
                if (segs.Count == 0) continue;
                var size = ModelShapes.Measure(segs);
                outs.Add(new
                {
                    id = e.Id.Value, what = ReferenceTools.Label(doc, e), category = e.Category?.Name,
                    u = new[] { size.minX, Math.Round(size.minX + size.widthMm, 1) }, v = new[] { size.minY, Math.Round(size.minY + size.heightMm, 1) },
                    lines = size.lines, arcs = size.arcs,
                    outline = withSegs ? ModelShapes.Round(segs).Take(200).ToList() : null,
                });
            }
            return new
            {
                axes = vertical ? "u = along the plane (mm from origin), v = height (model mm)" : "u = x, v = y (mm from origin)",
                along = vertical ? new[] { Math.Round(ex.X, 4), Math.Round(ex.Y, 4), 0 } : null,
                crossing = crossing.Count, cut = outs.Count, elements = outs,
                note = crossing.Count > limit ? $"{crossing.Count - limit} more cross the plane: raise limit, narrow categories or use near_mm." : null,
            };
        }

        // ---------------------------------------------------------------- facade

        private sealed class FacadeFace
        {
            public int Group; public XYZ N, Ex; public double OffMm, U0 = double.MaxValue, U1 = double.MinValue, Z0 = double.MaxValue, Z1 = double.MinValue; public int Walls; public double LenMm;
        }

        private sealed class Piece
        {
            public Element E; public string Name, Source, Kind; public bool ByName; public double H, L, Proj; public int Facade;
        }

        private static object Facade(Document doc, JsonObject a)
        {
            int maxFacades = Math.Clamp(Args.Int(a, "facades", 12), 1, 60), list = Math.Clamp(Args.Int(a, "list", 0), 0, 20);
            double minProj = Args.Dbl(a, "min_projection_mm") ?? 5;

            // 1. the facades: straight external walls grouped by the plane of their outer face
            var faces = new List<FacadeFace>();
            var wallFace = new Dictionary<long, FacadeFace>();
            foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                var wt = w.WallType;
                if (wt == null || !(w.Location is LocationCurve lc) || !(lc.Curve is Line line)) continue;
                double thick = w.Width * Mm;
                if (ModelStats.WallUse(wt.Name, Function(wt), wt.Kind == WallKind.Curtain, thick) != "external") continue;
                var n = new XYZ(w.Orientation.X, w.Orientation.Y, 0);
                if (n.GetLength() < 1e-6) continue;
                n = n.Normalize();
                double off = OuterOffset(w, n, line) * Mm;
                var bb = w.get_BoundingBox(null);
                faces.Add(new FacadeFace { N = n, Ex = new XYZ(-n.Y, n.X, 0), OffMm = off, Walls = 1, Group = -1,
                    U0 = Math.Min(line.GetEndPoint(0).DotProduct(new XYZ(-n.Y, n.X, 0)), line.GetEndPoint(1).DotProduct(new XYZ(-n.Y, n.X, 0))) * Mm,
                    U1 = Math.Max(line.GetEndPoint(0).DotProduct(new XYZ(-n.Y, n.X, 0)), line.GetEndPoint(1).DotProduct(new XYZ(-n.Y, n.X, 0))) * Mm,
                    Z0 = (bb?.Min.Z ?? 0) * Mm, Z1 = (bb?.Max.Z ?? 0) * Mm, LenMm = line.Length * Mm });
                wallFace[w.Id.Value] = faces[faces.Count - 1];
            }
            var groupOf = ModelShapes.FacadeGroups(faces.Select(f => (Math.Atan2(f.N.Y, f.N.X) * 180 / Math.PI, f.OffMm)).ToList());
            var facades = new List<FacadeFace>();
            for (int i = 0; i < faces.Count; i++)
            {
                faces[i].Group = groupOf[i];
                while (facades.Count <= groupOf[i]) facades.Add(null);
                var g = facades[groupOf[i]];
                if (g == null) facades[groupOf[i]] = g = new FacadeFace { N = faces[i].N, Ex = faces[i].Ex, OffMm = faces[i].OffMm, Group = groupOf[i] };
                else g.Walls++;
                g.U0 = Math.Min(g.U0, faces[i].U0); g.U1 = Math.Max(g.U1, faces[i].U1);
                g.Z0 = Math.Min(g.Z0, faces[i].Z0); g.Z1 = Math.Max(g.Z1, faces[i].Z1); g.LenMm += faces[i].LenMm;
            }

            // 2. the pieces: sweeps on those walls, and families / shapes that sit on a facade
            var pieces = new List<Piece>();
            foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(WallSweep)).Cast<WallSweep>())
            {
                var host = s.GetHostIds().Select(id => wallFace.TryGetValue(id.Value, out var f) ? f : null).FirstOrDefault(f => f != null);
                if (host == null) continue;
                string prof = null;
                try { prof = (doc.GetElement(s.GetWallSweepInfo().ProfileId) as FamilySymbol)?.FamilyName; } catch { }
                pieces.Add(new Piece { E = s, Facade = host.Group, Source = "wall sweep", Name = (doc.GetElement(s.GetTypeId())?.Name ?? "") + " " + (prof ?? "") });
            }
            var cats = new HashSet<long> { (long)BuiltInCategory.OST_GenericModel, (long)BuiltInCategory.OST_Columns, (long)BuiltInCategory.OST_StructuralColumns };
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(e => e is FamilyInstance || e is DirectShape))
            {
                var fi = e as FamilyInstance;
                if (fi?.SuperComponent != null || e.Category == null) continue;
                long cat = e.Category.Id.Value;
                bool inPlace = fi?.Symbol?.Family?.IsInPlace == true;
                if (!cats.Contains(cat) && !(inPlace && cat != (long)BuiltInCategory.OST_Floors && cat != (long)BuiltInCategory.OST_Roofs && cat != (long)BuiltInCategory.OST_Ceilings)) continue;
                var bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                var c = (bb.Min + bb.Max) / 2;
                FacadeFace best = null; double bestD = double.MaxValue;
                foreach (var f in facades)
                {
                    if (f == null) continue;
                    double d = c.DotProduct(f.N) * Mm - f.OffMm, u = c.DotProduct(f.Ex) * Mm, z = c.Z * Mm;
                    double reach = Math.Max(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y) * Mm / 2 + 600;
                    if (Math.Abs(d) > reach || u < f.U0 - 500 || u > f.U1 + 500 || z < f.Z0 - 500 || z > f.Z1 + 3000) continue;
                    if (Math.Abs(d) < bestD) { bestD = Math.Abs(d); best = f; }
                }
                if (best == null) continue;
                string name = fi != null ? fi.Symbol.FamilyName + " " + fi.Symbol.Name : "DirectShape " + (e.Name ?? "");
                pieces.Add(new Piece { E = e, Facade = best.Group, Name = name, Source = inPlace ? "in-place" : fi != null ? "loadable" : "DirectShape" });
            }

            // 3. measure each piece in its facade's frame and say what it is
            foreach (var p in pieces)
            {
                var f = facades[p.Facade];
                double z0 = double.MaxValue, z1 = double.MinValue, u0 = double.MaxValue, u1 = double.MinValue, d1 = double.MinValue;
                foreach (var pt in Points(p.E.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }), 3000))
                {
                    double z = pt.Z * Mm, u = pt.DotProduct(f.Ex) * Mm, d = pt.DotProduct(f.N) * Mm - f.OffMm;
                    z0 = Math.Min(z0, z); z1 = Math.Max(z1, z); u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); d1 = Math.Max(d1, d);
                }
                if (z0 == double.MaxValue) { p.Kind = "no geometry"; continue; }
                p.H = z1 - z0; p.L = u1 - u0; p.Proj = d1;
                var (kind, byName) = ModelShapes.DetailKind(p.Name, p.H, p.L, f.Z1 - z1);
                p.Kind = p.Proj < minProj ? "flush " + kind : kind; p.ByName = byName;
            }

            object Kinds(IEnumerable<Piece> ps) => ps.Where(p => p.Kind != "no geometry").GroupBy(p => p.Kind).OrderByDescending(g => g.Count()).Select(g => new
            {
                kind = g.Key, count = g.Count(), guessed = g.Count(p => !p.ByName),
                heightMm = Mid(g.Select(p => (double?)p.H)), projectionMm = Mid(g.Select(p => (double?)p.Proj)),
                depthRatio = ModelStats.Of(g.Where(p => p.H > 1).Select(p => p.Proj / p.H), 3)?.median,
                lengthM = Math.Round(g.Sum(p => p.L) / 1000, 1),
                sources = ModelStats.Top(g.Select(p => p.Source), 3),
                families = ModelStats.Top(g.Select(p => p.Name.Trim()), 3),
                examples = list > 0 ? g.Take(list).Select(p => p.E.Id.Value).ToList() : null,
            }).ToList();

            var listed = facades.Where(f => f != null).OrderByDescending(f => f.LenMm).Take(maxFacades).ToList();
            return new
            {
                facades = facades.Count(f => f != null),
                pieces = pieces.Count,
                perFacade = listed.Select(f =>
                {
                    double ang = Math.Atan2(f.N.Y, f.N.X) * 180 / Math.PI;
                    var a0 = f.N * (f.OffMm / Mm) + f.Ex * (f.U0 / Mm); var a1 = f.N * (f.OffMm / Mm) + f.Ex * (f.U1 / Mm);
                    return new
                    {
                        faces = ModelShapes.Facing(ang), angleDeg = Math.Round(ang, 1),
                        line = new[] { Math.Round(a0.X * Mm), Math.Round(a0.Y * Mm), Math.Round(a1.X * Mm), Math.Round(a1.Y * Mm) },
                        walls = f.Walls, lengthM = Math.Round((f.U1 - f.U0) / 1000, 1), heightM = Math.Round((f.Z1 - f.Z0) / 1000, 1),
                        details = Kinds(pieces.Where(p => p.Facade == f.Group)),
                    };
                }).ToList(),
                allFacades = Kinds(pieces),
                note = "projection = how far in front of the wall's outer face it reaches; depthRatio = projection / height (median). " +
                       "guessed = classified by shape, not by a word in its name: check those with list. Facades are named by facing (model axes) and face line (x0, y0, x1, y1 mm).",
            };
        }

        /// <summary>Where a straight wall's outer face is along its outward normal (feet): from its exterior face, else centre line + half width.</summary>
        private static double OuterOffset(Wall w, XYZ n, Line line)
        {
            try
            {
                foreach (var r in HostObjectUtils.GetSideFaces(w, ShellLayerType.Exterior))
                    if (w.GetGeometryObjectFromReference(r) is PlanarFace pf) return pf.Origin.DotProduct(n);
            }
            catch { }
            return line.Evaluate(0.5, true).DotProduct(n) + w.Width / 2;
        }

        // ---------------------------------------------------------------- geometry to 2D

        /// <summary>A Revit curve as lines and arcs in a 2D frame; arcs stay arcs, anything else is cut into short lines.</summary>
        private static IEnumerable<Seg> ToSegs(Curve c, Func<XYZ, (double u, double v)> uv)
        {
            if (c == null) yield break;
            if (c is Line)
            {
                var (ax, ay) = uv(c.GetEndPoint(0)); var (bx, by) = uv(c.GetEndPoint(1));
                yield return Seg.Line(ax, ay, bx, by);
            }
            else if (c is Arc arc && c.IsBound)
            {
                var (ax, ay) = uv(c.GetEndPoint(0)); var (bx, by) = uv(c.GetEndPoint(1));
                var (mx, my) = uv(c.Evaluate(0.5, true)); var (cx, cy) = uv(arc.Center);
                yield return Seg.Arc(ax, ay, bx, by, cx, cy, arc.Radius * Mm, mx, my);
            }
            else
            {
                var pts = c.Tessellate();
                for (int i = 1; i < pts.Count; i++)
                {
                    var (ax, ay) = uv(pts[i - 1]); var (bx, by) = uv(pts[i]);
                    yield return Seg.Line(ax, ay, bx, by);
                }
            }
        }

        private static IEnumerable<Solid> SolidsOf(GeometryElement ge)
        {
            if (ge == null) yield break;
            foreach (var g in ge)
            {
                if (g is Solid s && s.Volume > 1e-9) yield return s;
                else if (g is GeometryInstance gi) foreach (var x in SolidsOf(gi.GetInstanceGeometry())) yield return x;
            }
        }
    }
}
