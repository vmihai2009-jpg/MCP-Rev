// 1.14.5: editable facade detail. Mihai: "none of these elements are editable, I thought they would be profiles".
// A surveyed profile (from a section) becomes a Profile family (Metric Profile.rft, its outline as lines), which a
// native wall sweep (or a family's sweep) uses: the profile can be edited or swapped, the sweep moved, like any
// hand-made cornice.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static class ProfileFamilyBuilder
    {
        private const double Mm = 304.8;

        internal static string Template(UIApplication app, string name = "Metric Profile.rft")
        {
            string root = app.Application.FamilyTemplatePath;
            foreach (var dir in new[] { root, Path.GetDirectoryName(root.TrimEnd('\\')) })
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    var f = Directory.GetFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (f != null) return f;
                }
            throw new FileNotFoundException($"No {name} under {root}");
        }

        /// <summary>
        /// A profile family whose outline is ring (mm: X = out from the host face, Y = up from the sweep's height), saved
        /// to path. Returns path.
        /// </summary>
        internal static string Build(UIApplication app, IList<Pt> ring, string path)
        {
            var fdoc = app.Application.NewFamilyDocument(Template(app));
            try
            {
                using (var t = new Transaction(fdoc, "profile"))
                {
                    t.Start();
                    var view = new FilteredElementCollector(fdoc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate)
                        ?? throw new InvalidOperationException("The profile template has no plan view.");
                    var pts = new List<Pt>();
                    foreach (var q in ring) if (pts.Count == 0 || q.Dist2D(pts[^1]) >= 1) pts.Add(q);
                    while (pts.Count > 3 && pts[^1].Dist2D(pts[0]) < 1) pts.RemoveAt(pts.Count - 1);
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var a = new XYZ(pts[i].X / Mm, pts[i].Y / Mm, 0); var b = new XYZ(pts[(i + 1) % pts.Count].X / Mm, pts[(i + 1) % pts.Count].Y / Mm, 0);
                        fdoc.FamilyCreate.NewDetailCurve(view, Line.CreateBound(a, b));
                    }
                    if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit would not keep the profile");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                fdoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                return path;
            }
            finally { fdoc.Close(false); }
        }
    }
}
