// 1.14.6: editable facade detail around windows. Mihai (pointing at the relief round the windows): "these should also
// be sweeps or editable objects". A window's surround, pediment, consoles, sill and apron, read from the elevation,
// become one Generic Model family of plain extrusions (each one's sketch and depth editable in the Family Editor,
// one Material parameter), placed at the window and turned to the facade. Identical windows share the family.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static class DetailFamilyBuilder
    {
        private const double Mm = 304.8;

        /// <summary>One piece of a detail family: its outline (mm, X along the facade from the family origin, Y up from it) and its depth range out from the facade line.</summary>
        internal class Piece
        {
            public List<Pt> Ring;
            public double V0, V1;
        }

        /// <summary>A key for a set of pieces (to 10 mm), so identical windows share a family.</summary>
        internal static string Key(IEnumerable<Piece> pieces)
        {
            string N(double v) => Math.Round(v / 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.Join("|", pieces.Select(p => string.Join(";", p.Ring.Select(q => N(q.X) + "," + N(q.Y)).OrderBy(s => s, StringComparer.Ordinal)) + "@" + N(p.V0) + "-" + N(p.V1))
                .OrderBy(s => s, StringComparer.Ordinal));
        }

        /// <summary>
        /// A Generic Model family of one vertical extrusion: a plan outline (mm, X along the facade from the origin, Y out
        /// from the facade line; outSign as in Build) from z0 to z1 above the family's level (a pilaster's or pier's shaft,
        /// flutes and all). Height is a family parameter driving the extrusion's end. Saved to path.
        /// </summary>
        internal static string BuildVertical(UIApplication app, IList<Pt> ring, double z0, double z1, int outSign, string path)
        {
            var fdoc = app.Application.NewFamilyDocument(ProfileFamilyBuilder.Template(app, "Metric Generic Model.rft"));
            try
            {
                using (var t = new Transaction(fdoc, "shaft"))
                {
                    t.Start();
                    var fm = fdoc.FamilyManager;
                    if (fm.CurrentType == null) fm.NewType("Default");
                    var mat = fm.get_Parameter("Material") ?? fm.AddParameter("Material", GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                    fm.Set(mat, WindowFamilyBuilder.MaterialNamed(fdoc, "Stone", new Color(225, 220, 205), 0));
                    var hp = fm.get_Parameter("Shaft Height") ?? fm.AddParameter("Shaft Height", GroupTypeId.Geometry, SpecTypeId.Length, false);
                    var bp = fm.get_Parameter("Shaft Base") ?? fm.AddParameter("Shaft Base", GroupTypeId.Geometry, SpecTypeId.Length, false);
                    fm.Set(hp, (z1 - z0) / Mm); fm.Set(bp, z0 / Mm);
                    var pts = new List<Pt>();
                    foreach (var q in ring) if (pts.Count == 0 || q.Dist2D(pts[^1]) >= 1.5) pts.Add(q);
                    while (pts.Count > 3 && pts[^1].Dist2D(pts[0]) < 1.5) pts.RemoveAt(pts.Count - 1);
                    var sp = SketchPlane.Create(fdoc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                    var loop = new CurveArray();
                    XYZ P(Pt q) => new XYZ(q.X / Mm, outSign * q.Y / Mm, 0);
                    for (int i = 0; i < pts.Count; i++) loop.Append(Line.CreateBound(P(pts[i]), P(pts[(i + 1) % pts.Count])));
                    var prof = new CurveArrArray(); prof.Append(loop);
                    var ex = fdoc.FamilyCreate.NewExtrusion(true, prof, sp, z1 / Mm);
                    ex.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM).Set(z0 / Mm);
                    fdoc.Regenerate();
                    fm.SetFormula(hp, null);
                    fm.AssociateElementParameterToFamilyParameter(ex.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM), bp);
                    var endP = fm.get_Parameter("Shaft Top") ?? fm.AddParameter("Shaft Top", GroupTypeId.Geometry, SpecTypeId.Length, false);
                    fm.SetFormula(endP, "Shaft Base + Shaft Height");
                    fm.AssociateElementParameterToFamilyParameter(ex.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), endP);
                    fm.AssociateElementParameterToFamilyParameter(ex.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), mat);
                    if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit would not keep the family");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                fdoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                return path;
            }
            finally { fdoc.Close(false); }
        }

        /// <summary>
        /// A Generic Model family of extrusions, one per piece: sketched on a vertical plane facing out (outSign: +1 when
        /// the facade's outside is the family's +Y), from V0 to V1. Saved to path.
        /// </summary>
        internal static string Build(UIApplication app, IList<Piece> pieces, int outSign, string path)
        {
            var fdoc = app.Application.NewFamilyDocument(ProfileFamilyBuilder.Template(app, "Metric Generic Model.rft"));
            try
            {
                using (var t = new Transaction(fdoc, "detail"))
                {
                    t.Start();
                    var fm = fdoc.FamilyManager;
                    if (fm.CurrentType == null) fm.NewType("Default");
                    var mat = fm.get_Parameter("Material") ?? fm.AddParameter("Material", GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                    var stone = WindowFamilyBuilder.MaterialNamed(fdoc, "Stone", new Color(225, 220, 205), 0);
                    fm.Set(mat, stone);
                    foreach (var p in pieces)
                    {
                        var pts = new List<Pt>();
                        foreach (var q in p.Ring) if (pts.Count == 0 || q.Dist2D(pts[^1]) >= 1.5) pts.Add(q);
                        while (pts.Count > 3 && pts[^1].Dist2D(pts[0]) < 1.5) pts.RemoveAt(pts.Count - 1);
                        if (pts.Count < 3 || p.V1 - p.V0 < 1) continue;
                        double y0 = outSign * p.V0 / Mm;
                        var plane = Plane.CreateByNormalAndOrigin(new XYZ(0, outSign, 0), new XYZ(0, y0, 0));
                        var sp = SketchPlane.Create(fdoc, plane);
                        var loop = new CurveArray();
                        for (int i = 0; i < pts.Count; i++)
                            loop.Append(Line.CreateBound(new XYZ(pts[i].X / Mm, y0, pts[i].Y / Mm), new XYZ(pts[(i + 1) % pts.Count].X / Mm, y0, pts[(i + 1) % pts.Count].Y / Mm)));
                        var prof = new CurveArrArray(); prof.Append(loop);
                        try
                        {
                            var ex = fdoc.FamilyCreate.NewExtrusion(true, prof, sp, (p.V1 - p.V0) / Mm);
                            fm.AssociateElementParameterToFamilyParameter(ex.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), mat);
                        }
                        catch { /* a piece Revit won't extrude is left out */ }
                    }
                    if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit would not keep the family");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                fdoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                return path;
            }
            finally { fdoc.Close(false); }
        }
    }
}
