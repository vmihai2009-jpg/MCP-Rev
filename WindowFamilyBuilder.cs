// 1.12.2: one parametric window family, "BG Window", generated through the API from Metric Window.rft, so every
// surveyed window is a type of it (Mihai: one family you can control, types per survey window; another family only
// where the design really differs). Geometry is boxes locked to reference planes whose places are labelled
// dimensions and formulas, so a type flexes: frame, bottom rail, meeting rail, glazing bars across and up, glass.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BarrGazetas.RevitMcp
{
    internal static class WindowFamilyBuilder
    {
        public const string FamilyName = "BG Window";
        private const double Mm = 304.8;

        internal static string TemplatePath(UIApplication app)
        {
            string root = app.Application.FamilyTemplatePath;
            foreach (var dir in new[] { root, Path.GetDirectoryName(root.TrimEnd('\\')) })
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    var f = Directory.GetFiles(dir, "Metric Window.rft", SearchOption.AllDirectories).FirstOrDefault();
                    if (f != null) return f;
                }
            throw new FileNotFoundException("No Metric Window.rft under " + root);
        }

        /// <summary>What the template holds: reference planes, views, parameters, the opening and the wall.</summary>
        internal static object Probe(UIApplication app)
        {
            var fdoc = app.Application.NewFamilyDocument(TemplatePath(app));
            try
            {
                var planes = new FilteredElementCollector(fdoc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                    .Select(p => new { id = p.Id.Value, name = p.Name, normal = V(p.Normal), at = V(p.BubbleEnd), free = V(p.FreeEnd), isRef = p.get_Parameter(BuiltInParameter.ELEM_REFERENCE_NAME)?.AsValueString() }).ToList();
                var views = new FilteredElementCollector(fdoc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate)
                    .Select(v => new { id = v.Id.Value, name = v.Name, type = v.ViewType.ToString(), dir = V(v.ViewDirection) }).ToList();
                var fm = fdoc.FamilyManager;
                var prms = fm.Parameters.Cast<FamilyParameter>().Select(p => new { name = p.Definition.Name, instance = p.IsInstance, formula = p.Formula,
                    value = fm.CurrentType == null ? null : fm.CurrentType.AsValueString(p) }).ToList();
                var dims = new FilteredElementCollector(fdoc).OfClass(typeof(Dimension)).Cast<Dimension>()
                    .Select(d => new { id = d.Id.Value, label = SafeLabel(d), value = d.Value == null ? (double?)null : Math.Round(d.Value.Value * Mm, 1), view = d.View?.Name }).ToList();
                var wall = new FilteredElementCollector(fdoc).OfClass(typeof(Wall)).Cast<Wall>().FirstOrDefault();
                var open = new FilteredElementCollector(fdoc).OfClass(typeof(Opening)).Cast<Opening>().FirstOrDefault();
                var dimLines = new FilteredElementCollector(fdoc).OfClass(typeof(Dimension)).Cast<Dimension>().Where(d => d.Curve is Line)
                    .Select(d => new { id = d.Id.Value, origin = V(((Line)d.Curve).Origin), dir = V(((Line)d.Curve).Direction), refs = d.References.Size }).ToList();
                // which dimension lines Revit takes between Left and Right in the plan
                var tries = new List<string>();
                var planView = new FilteredElementCollector(fdoc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);
                var lp = new FilteredElementCollector(fdoc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().First(p => p.Name == "Left");
                var rp2 = new FilteredElementCollector(fdoc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().First(p => p.Name == "Right");
                using (var t = new Transaction(fdoc, "try"))
                {
                    t.Start();
                    foreach (var (name, a0, b0) in new[] { ("y-1 z0", new XYZ(-5, -1, 0), new XYZ(5, -1, 0)), ("y+1 z0", new XYZ(-5, 1, 0), new XYZ(5, 1, 0)), ("y-3 zLeft", new XYZ(-5, -3, lp.BubbleEnd.Z), new XYZ(5, -3, lp.BubbleEnd.Z)) })
                        try
                        {
                            var ra = new ReferenceArray(); ra.Append(lp.GetReference()); ra.Append(rp2.GetReference());
                            fdoc.FamilyCreate.NewLinearDimension(planView, Line.CreateBound(a0, b0), ra); tries.Add(name + ": ok");
                        }
                        catch (Exception ex) { tries.Add(name + ": " + ex.Message); }
                    try
                    {
                        var np = fdoc.FamilyCreate.NewReferencePlane(new XYZ(-1, lp.BubbleEnd.Y, lp.BubbleEnd.Z), new XYZ(-1, lp.FreeEnd.Y, lp.FreeEnd.Z), XYZ.BasisZ, planView);
                        tries.Add($"new plane normal {string.Join(",", V(np.Normal))}"); fdoc.Regenerate();
                        var ra = new ReferenceArray(); ra.Append(lp.GetReference()); ra.Append(np.GetReference());
                        fdoc.FamilyCreate.NewLinearDimension(planView, Line.CreateBound(new XYZ(-5, -1, 0), new XYZ(5, -1, 0)), ra); tries.Add("Left to new plane: ok");
                    }
                    catch (Exception ex) { tries.Add("Left to new plane: " + ex.Message); }
                    t.RollBack();
                }
                return new
                {
                    template = fdoc.PathName, planes, views, parameters = prms, dims, dimLines, tries,
                    wall = wall == null ? null : new { id = wall.Id.Value, width = Math.Round(wall.Width * Mm), orientation = V(wall.Orientation), line = ((LocationCurve)wall.Location).Curve is Line l ? new[] { V(l.GetEndPoint(0)), V(l.GetEndPoint(1)) } : null },
                    opening = open == null ? null : new { id = open.Id.Value, rect = open.IsRectBoundary ? open.BoundaryRect.Select(V).ToList() : null },
                    types = fm.Types.Cast<FamilyType>().Select(t => t.Name).ToList(),
                };
            }
            finally { fdoc.Close(false); }
        }

        // ------------------------------------------------------------------ the family

        /// <summary>Type values a window type sets (mm, counts, yes/no as 0/1). Names are the family's parameters.</summary>
        internal static readonly string[] LengthParams = { "Width", "Height", "Frame Width", "Bottom Rail", "Frame Depth", "Inset", "Glass Thickness", "Meeting Rail Height", "Meeting Rail Width", "Glazing Bar Width" };
        internal static readonly string[] IntParams = { "Panes Across", "Panes High Bottom", "Panes High Top", "Meeting Rail" };
        private const int MaxBarsAcross = 3, MaxBarsUp = 2;

        internal class BuildReport
        {
            public string Path;
            public List<string> Steps = new List<string>();
            public List<object> Flex = new List<object>();
            public int Planes, Boxes, Locks;
        }

        /// <summary>
        /// Generates "BG Window" from Metric Window.rft and saves it to path. cases: type values to try after building
        /// (each in a transaction rolled back), with whatever Revit says about them.
        /// </summary>
        internal static BuildReport Build(UIApplication app, string path, IList<Dictionary<string, double>> cases = null)
        {
            var rep = new BuildReport { Path = path };
            string stage = "template";
            var fdoc = app.Application.NewFamilyDocument(TemplatePath(app));
            try
            {
              try
              {
                var fm = fdoc.FamilyManager;
                var elev = new FilteredElementCollector(fdoc).OfClass(typeof(View)).Cast<View>().First(v => !v.IsTemplate && v.ViewType == ViewType.Elevation && v.ViewDirection.Y > 0.9);
                var plan = new FilteredElementCollector(fdoc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);
                var rps = new FilteredElementCollector(fdoc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
                ReferencePlane Named(string n) => rps.First(p => p.Name == n);
                var left = Named("Left"); var right = Named("Right"); var sill = Named("Sill"); var head = Named("Head");
                var wall = new FilteredElementCollector(fdoc).OfClass(typeof(Wall)).Cast<Wall>().First();
                var extFace = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior).First();
                double yExt = wall.Orientation.Y > 0 ? wall.Width / 2 : -wall.Width / 2; // the wall runs along X through y = 0
                double inward = wall.Orientation.Y > 0 ? -1 : 1;

                using (var t = new Transaction(fdoc, "BG Window"))
                {
                    t.Start();
                    if (fm.CurrentType == null) fm.NewType("Default");
                    stage = "parameters";
                    // ---- parameters
                    var P = new Dictionary<string, FamilyParameter>();
                    foreach (FamilyParameter p in fm.Parameters) P[p.Definition.Name] = p;
                    FamilyParameter Add(string n, ForgeTypeId spec, ForgeTypeId group)
                    {
                        if (!P.TryGetValue(n, out var p)) P[n] = p = fm.AddParameter(n, group, spec, false);
                        return p;
                    }
                    foreach (var n in LengthParams.Skip(2)) Add(n, SpecTypeId.Length, GroupTypeId.Construction);
                    foreach (var n in new[] { "Panes Across", "Panes High Bottom", "Panes High Top" }) Add(n, SpecTypeId.Int.Integer, GroupTypeId.Construction);
                    Add("Meeting Rail", SpecTypeId.Boolean.YesNo, GroupTypeId.Construction);
                    Add("Frame Material", SpecTypeId.Reference.Material, GroupTypeId.Materials);
                    Add("Glass Material", SpecTypeId.Reference.Material, GroupTypeId.Materials);
                    // derived (formulas)
                    foreach (var n in new[] { "Bottom Zone", "Top Zone Start", "Glass Front", "Glass Back" }) Add(n, SpecTypeId.Length, GroupTypeId.Constraints);
                    for (int k = 1; k <= MaxBarsAcross; k++) { Add($"VB{k} L", SpecTypeId.Length, GroupTypeId.Constraints); Add($"VB{k} On", SpecTypeId.Boolean.YesNo, GroupTypeId.Graphics); }
                    for (int k = 1; k <= MaxBarsUp; k++)
                        foreach (var z in new[] { "HB", "HT" }) { Add($"{z}{k} B", SpecTypeId.Length, GroupTypeId.Constraints); Add($"{z}{k} On", SpecTypeId.Boolean.YesNo, GroupTypeId.Graphics); }

                    stage = "values";
                    // ---- values everything is drawn at: every bar on, so every face exists to lock
                    void L(string n, double mm) => fm.Set(P[n], mm / Mm);
                    void I(string n, int v) => fm.Set(P[n], v);
                    L("Width", 1200); L("Height", 2400); L("Frame Width", 60); L("Bottom Rail", 80); L("Frame Depth", 70); L("Inset", 100);
                    L("Glass Thickness", 8); L("Meeting Rail Height", 1150); L("Meeting Rail Width", 50); L("Glazing Bar Width", 25);
                    I("Panes Across", MaxBarsAcross + 1); I("Panes High Bottom", MaxBarsUp + 1); I("Panes High Top", MaxBarsUp + 1); I("Meeting Rail", 1);
                    stage = "formulas";
                    fm.SetFormula(P["Bottom Zone"], "if(Meeting Rail, Meeting Rail Height, Height - Frame Width)");
                    fm.SetFormula(P["Top Zone Start"], "Bottom Zone + Meeting Rail Width");
                    fm.SetFormula(P["Glass Front"], "Frame Depth / 2 - Glass Thickness / 2");
                    fm.SetFormula(P["Glass Back"], "Frame Depth / 2 + Glass Thickness / 2");
                    for (int k = 1; k <= MaxBarsAcross; k++)
                    {
                        fm.SetFormula(P[$"VB{k} L"], $"if(Panes Across > {k}, Width * {k} / Panes Across, Width * {k} / {k + 1}) - Glazing Bar Width / 2");
                        fm.SetFormula(P[$"VB{k} On"], $"Panes Across > {k}");
                    }
                    for (int k = 1; k <= MaxBarsUp; k++)
                    {
                        fm.SetFormula(P[$"HB{k} B"], $"Bottom Rail + {k} * (Bottom Zone - Bottom Rail) / if(Panes High Bottom > {k}, Panes High Bottom, {k + 1}) - Glazing Bar Width / 2");
                        fm.SetFormula(P[$"HB{k} On"], $"Panes High Bottom > {k}");
                        fm.SetFormula(P[$"HT{k} B"], $"Top Zone Start + {k} * (Height - Frame Width - Top Zone Start) / if(Panes High Top > {k}, Panes High Top, {k + 1}) - Glazing Bar Width / 2");
                        fm.SetFormula(P[$"HT{k} On"], $"and(Meeting Rail, Panes High Top > {k})");
                    }
                    stage = "materials";
                    var frameMat = MaterialNamed(fdoc, "Window Frame", new Color(240, 240, 235), 0);
                    var glassMat = MaterialNamed(fdoc, "Glass", new Color(170, 200, 215), 60);
                    fm.Set(P["Frame Material"], frameMat); fm.Set(P["Glass Material"], glassMat);
                    fdoc.Regenerate();
                    rep.Steps.Add("parameters and formulas");

                    double ey = elev.Origin.Y; int dimCount = 0;
                    double X(ReferencePlane p) => p.BubbleEnd.X;
                    double Z(ReferencePlane p) => p.BubbleEnd.Z;
                    double V(string n) => fm.CurrentType.AsDouble(P[n]) ?? 0;

                    // ---- reference planes driven by labelled dimensions
                    ReferencePlane Vert(string name, double x)
                    {
                        stage = "plane " + name;
                        var p = fdoc.FamilyCreate.NewReferencePlane(new XYZ(x, left.BubbleEnd.Y, left.BubbleEnd.Z), new XYZ(x, left.FreeEnd.Y, left.FreeEnd.Z), XYZ.BasisZ, plan);
                        p.Name = name; rep.Planes++; fdoc.Regenerate(); return p;
                    }
                    ReferencePlane Horiz(string name, double z)
                    {
                        stage = "plane " + name;
                        var p = fdoc.FamilyCreate.NewReferencePlane(new XYZ(sill.BubbleEnd.X, sill.BubbleEnd.Y, z), new XYZ(sill.FreeEnd.X, sill.FreeEnd.Y, z), XYZ.BasisY, elev);
                        p.Name = name; rep.Planes++; fdoc.Regenerate(); return p;
                    }
                    void DimX(Autodesk.Revit.DB.Reference a, Autodesk.Revit.DB.Reference b, string label, double atZ)
                    {
                        var ra = new ReferenceArray(); ra.Append(a); ra.Append(b);
                        stage = $"dimension #{++dimCount} across " + label;
                        var d = fdoc.FamilyCreate.NewLinearDimension(plan, Line.CreateBound(new XYZ(X(left) - 2, yExt + 1 + (atZ - Z(head)) * 0.3, 0), new XYZ(X(right) + 2, yExt + 1 + (atZ - Z(head)) * 0.3, 0)), ra);
                        stage = "label " + label;
                        d.FamilyLabel = P[label];
                    }
                    void DimZ(Autodesk.Revit.DB.Reference a, Autodesk.Revit.DB.Reference b, string label, double atX)
                    {
                        var ra = new ReferenceArray(); ra.Append(a); ra.Append(b);
                        stage = $"dimension #{++dimCount} up " + label;
                        var d = fdoc.FamilyCreate.NewLinearDimension(elev, Line.CreateBound(new XYZ(atX, ey, Z(sill) - 2), new XYZ(atX, ey, Z(head) + 2)), ra);
                        stage = "label " + label;
                        d.FamilyLabel = P[label];
                    }
                    double xl = X(left), xr = X(right), zs = Z(sill), zh = Z(head);
                    double fw = V("Frame Width"), br = V("Bottom Rail"), bw = V("Glazing Bar Width");
                    stage = "frame planes";
                    var frameL = Vert("Frame L", xl + fw); DimX(left.GetReference(), frameL.GetReference(), "Frame Width", zh + 1.5);
                    var frameR = Vert("Frame R", xr - fw); DimX(frameR.GetReference(), right.GetReference(), "Frame Width", zh + 1.5);
                    var frameT = Horiz("Frame Top", zh - fw); DimZ(frameT.GetReference(), head.GetReference(), "Frame Width", xr + 1.5);
                    var frameB = Horiz("Frame Bottom", zs + br); DimZ(sill.GetReference(), frameB.GetReference(), "Bottom Rail", xr + 1.5);
                    var railB = Horiz("Rail B", zs + V("Bottom Zone")); DimZ(sill.GetReference(), railB.GetReference(), "Bottom Zone", xr + 2.5);
                    var railT = Horiz("Rail T", zs + V("Top Zone Start")); DimZ(railB.GetReference(), railT.GetReference(), "Meeting Rail Width", xr + 3.5);
                    stage = "bar planes";
                    var vb = new List<(ReferencePlane l, ReferencePlane r)>();
                    for (int k = 1; k <= MaxBarsAcross; k++)
                    {
                        var pl = Vert($"VB{k} L", xl + V($"VB{k} L")); DimX(left.GetReference(), pl.GetReference(), $"VB{k} L", zs - 1.5 - k);
                        var pr = Vert($"VB{k} R", xl + V($"VB{k} L") + bw); DimX(pl.GetReference(), pr.GetReference(), "Glazing Bar Width", zs - 1.5 - k);
                        vb.Add((pl, pr));
                    }
                    var hb = new List<(string key, ReferencePlane b, ReferencePlane t)>();
                    for (int k = 1; k <= MaxBarsUp; k++)
                        foreach (var zone in new[] { "HB", "HT" })
                        {
                            string n = $"{zone}{k}";
                            var pb = Horiz($"{n} B", zs + V($"{n} B")); DimZ(sill.GetReference(), pb.GetReference(), $"{n} B", xl - 1.5 - k - (zone == "HT" ? 3 : 0));
                            var pt = Horiz($"{n} T", zs + V($"{n} B") + bw); DimZ(pb.GetReference(), pt.GetReference(), "Glazing Bar Width", xl - 1.5 - k - (zone == "HT" ? 3 : 0));
                            hb.Add((n, pb, pt));
                        }
                    stage = "frame face plane";
                    // the frame's outside face: Inset in from the wall's outside face (so it follows any host wall)
                    double yFace = yExt + inward * V("Inset") ;
                    var face = fdoc.FamilyCreate.NewReferencePlane(new XYZ(-inward * 2, yFace, 0), new XYZ(inward * 2, yFace, 0), XYZ.BasisZ, plan);
                    if (face.Normal.Y * inward < 0) { fdoc.Delete(face.Id); face = fdoc.FamilyCreate.NewReferencePlane(new XYZ(inward * 2, yFace, 0), new XYZ(-inward * 2, yFace, 0), XYZ.BasisZ, plan); }
                    face.Name = "Frame Face"; rep.Planes++; fdoc.Regenerate();
                    {
                        var ra = new ReferenceArray(); ra.Append(extFace); ra.Append(face.GetReference());
                        var d = fdoc.FamilyCreate.NewLinearDimension(plan, Line.CreateBound(new XYZ(xr + 1, yExt + 1, 0), new XYZ(xr + 1, yExt - 3, 0)), ra);
                        d.FamilyLabel = P["Inset"];
                    }
                    fdoc.Regenerate();
                    rep.Steps.Add($"{rep.Planes} reference planes with labelled dimensions");

                    stage = "boxes";
                    // ---- boxes on the frame face, locked to the planes
                    var sp = SketchPlane.Create(fdoc, face.Id);
                    Extrusion Box(string what, ReferencePlane x0, ReferencePlane x1, ReferencePlane z0, ReferencePlane z1, string start, string end, string material, string visible)
                    {
                        stage = "box " + what;
                        double ax = X(x0), bx = X(x1), az = Z(z0), bz = Z(z1);
                        var loop = new CurveArray();
                        var c = new[] { new XYZ(ax, yFace, az), new XYZ(bx, yFace, az), new XYZ(bx, yFace, bz), new XYZ(ax, yFace, bz) };
                        for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(c[i], c[(i + 1) % 4]));
                        var prof = new CurveArrArray(); prof.Append(loop);
                        var ex = fdoc.FamilyCreate.NewExtrusion(true, prof, sp, V(end));
                        if (start != null) ex.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM).Set(V(start));
                        fdoc.Regenerate();
                        if (start != null) fm.AssociateElementParameterToFamilyParameter(ex.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM), P[start]);
                        fm.AssociateElementParameterToFamilyParameter(ex.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), P[end]);
                        fm.AssociateElementParameterToFamilyParameter(ex.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), P[material]);
                        foreach (var rp in new[] { x0, x1, z0, z1 }) LockFace(fdoc, elev, rp, ex, rep, what);
                        if (visible != null) fm.AssociateElementParameterToFamilyParameter(ex.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM), P[visible]);
                        rep.Boxes++;
                        return ex;
                    }
                    var frames = new List<Extrusion>
                    {
                        Box("left jamb", left, frameL, sill, head, null, "Frame Depth", "Frame Material", null),
                        Box("right jamb", frameR, right, sill, head, null, "Frame Depth", "Frame Material", null),
                        Box("head", left, right, frameT, head, null, "Frame Depth", "Frame Material", null),
                        Box("bottom rail", left, right, sill, frameB, null, "Frame Depth", "Frame Material", null),
                        Box("meeting rail", left, right, railB, railT, null, "Frame Depth", "Frame Material", "Meeting Rail"),
                    };
                    for (int k = 0; k < vb.Count; k++) frames.Add(Box($"bar across {k + 1}", vb[k].l, vb[k].r, sill, head, null, "Frame Depth", "Frame Material", $"VB{k + 1} On"));
                    foreach (var (key, b, tp) in hb) frames.Add(Box($"bar {key}", left, right, b, tp, null, "Frame Depth", "Frame Material", $"{key} On"));
                    var glass = Box("glass", left, right, sill, head, "Glass Front", "Glass Back", "Glass Material", null);
                    try
                    {
                        var subs = fdoc.OwnerFamily.FamilyCategory.SubCategories;
                        if (subs.Contains("Glass")) glass.Subcategory = subs.get_Item("Glass");
                        if (subs.Contains("Frame/Mullion")) foreach (var f in frames) f.Subcategory = subs.get_Item("Frame/Mullion");
                    }
                    catch { }
                    rep.Steps.Add($"{rep.Boxes} boxes, {rep.Locks} faces locked to planes");

                    // ---- the default type: a plain two-pane sash
                    I("Panes Across", 1); I("Panes High Bottom", 1); I("Panes High Top", 1); I("Meeting Rail", 1);
                    L("Width", 1000); L("Height", 1800); L("Meeting Rail Height", 850);
                    fdoc.Regenerate();
                    if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit would not keep the family");
                }

                foreach (var c in cases ?? new List<Dictionary<string, double>>()) rep.Flex.Add(TryType(fdoc, c));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                fdoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                rep.Steps.Add("saved " + path);
                return rep;
              }
              catch (Exception ex) when (!(ex is InvalidOperationException && ex.Message.StartsWith("at ")))
              { throw new InvalidOperationException($"at {stage}: {ex.Message}", ex); }
            }
            finally { fdoc.Close(false); }
        }

        private static void LockFace(Document fdoc, View view, ReferencePlane rp, Extrusion ex, BuildReport rep, string what)
        {
            var n = rp.Normal; double d = n.DotProduct(rp.BubbleEnd);
            var geo = ex.get_Geometry(new Options { ComputeReferences = true, IncludeNonVisibleObjects = true });
            foreach (var s in geo.OfType<Solid>())
                foreach (Face f in s.Faces)
                    if (f is PlanarFace pf && Math.Abs(Math.Abs(pf.FaceNormal.DotProduct(n)) - 1) < 1e-6 && Math.Abs(n.DotProduct(pf.Origin) - d) < 1e-5)
                    {
                        var al = fdoc.FamilyCreate.NewAlignment(view, rp.GetReference(), pf.Reference);
                        al.IsLocked = true; rep.Locks++;
                        return;
                    }
            throw new InvalidOperationException($"{what}: no face on plane {rp.Name} to lock");
        }

        /// <summary>One set of type values tried on the family (rolled back): what Revit said, and the panes it gives.</summary>
        internal static object TryType(Document fdoc, Dictionary<string, double> values)
        {
            var fm = fdoc.FamilyManager;
            var errors = new List<string>();
            using (var t = new Transaction(fdoc, "flex"))
            {
                var o = t.GetFailureHandlingOptions();
                o.SetFailuresPreprocessor(new Collect(errors)); o.SetClearAfterRollback(true);
                t.SetFailureHandlingOptions(o);
                t.Start();
                try
                {
                    foreach (var kv in values)
                    {
                        var p = fm.get_Parameter(kv.Key) ?? throw new ArgumentException("no parameter " + kv.Key);
                        if (p.StorageType == StorageType.Integer) fm.Set(p, (int)Math.Round(kv.Value)); else fm.Set(p, kv.Value / Mm);
                    }
                    fdoc.Regenerate();
                }
                catch (Exception ex) { errors.Add(ex.Message); }
                var visible = new FilteredElementCollector(fdoc).OfClass(typeof(Extrusion)).Cast<Extrusion>().Count(e => e.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM)?.AsInteger() != 0);
                t.RollBack();
                return new { values, ok = errors.Count == 0, errors, visibleBoxes = visible };
            }
        }

        private class Collect : IFailuresPreprocessor
        {
            private readonly List<string> _into;
            public Collect(List<string> into) { _into = into; }
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (var f in a.GetFailureMessages())
                {
                    _into.Add(f.GetDescriptionText());
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                }
                return a.GetFailureMessages().Any(f => f.GetSeverity() >= FailureSeverity.Error) ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }

        internal static ElementId MaterialNamed(Document d, string name, Color c, int transparency)
        {
            var m = new FilteredElementCollector(d).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == name);
            if (m != null) return m.Id;
            var id = Material.Create(d, name);
            m = (Material)d.GetElement(id);
            m.Color = c; m.Transparency = transparency;
            return id;
        }

        private static string SafeLabel(Dimension d) { try { return d.FamilyLabel?.Definition?.Name; } catch { return null; } }
        internal static double[] V(XYZ p) => p == null ? null : new[] { Math.Round(p.X * Mm, 1), Math.Round(p.Y * Mm, 1), Math.Round(p.Z * Mm, 1) };
    }
}
