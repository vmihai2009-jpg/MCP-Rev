// Revit-free. 1.26.0: one family per repeated detail. facade_detail_map groups repeated pieces (consoles, keystones,
// pediments, hoods, balusters, modillions...) into types; this takes a type's representative item, cuts its drawn lines
// into faces (the drawn openings left out), and gives them relative to the item's own anchor (middle along, bottom), so one
// family made from them fits every item of the type: placed at each item's anchor, it is the repeated detail, editable once.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public static class FacadeTypes
    {
        /// <summary>Kinds that are runs along the facade, not repeated pieces: built by other tools.</summary>
        public static readonly HashSet<string> NotPieces = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "rustication", "unclassified", "quoins", "band", "cornice", "entablature", "string course", "plinth" };

        public static bool IsPieceType(FacadeType t) => t != null && t.Count >= 1 && !NotPieces.Any(k => (t.Kind ?? "").IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>The item's anchor: middle along (u), bottom (z).</summary>
        public static (double u, double z) Anchor(FacadeItem it) => ((it.U0 + it.U1) / 2, it.Z0);

        /// <summary>
        /// The faces of one item (closed shapes its lines make inside its box, grown by growMm), cut back to outside the
        /// drawn openings, relative to its anchor. Empty when its lines close no shape.
        /// </summary>
        public static List<List<Pt>> Faces(FacadeItem it, IEnumerable<(Pt a, Pt b)> lines, IList<(double u0, double u1, double z0, double z1)> openings,
            double growMm = 20, double minFaceMm2 = 1500)
        {
            double u0 = it.U0 - growMm, u1 = it.U1 + growMm, z0 = it.Z0 - growMm, z1 = it.Z1 + growMm;
            bool In(Pt p) => p.X >= u0 && p.X <= u1 && p.Y >= z0 && p.Y <= z1;
            var segs = lines.Where(l => In(l.a) && In(l.b)).Select(l => new Seg(l.a, l.b, 0)).ToList();
            var (au, az) = Anchor(it);
            var res = new List<List<Pt>>();
            foreach (var ring in SurveyDetail.Faces(segs, 3, minFaceMm2))
                foreach (var piece in FacadeRelief.OutsideOpenings(ring, openings, minFaceMm2))
                    res.Add(piece.Select(p => new Pt(p.X - au, p.Y - az)).ToList());
            // a face inside another is its moulding's step: keep both (they get depths in steps), but not the same ring twice
            return res.GroupBy(r => string.Join(";", r.Select(p => $"{Math.Round(p.X)},{Math.Round(p.Y)}").OrderBy(s => s))).Select(g => g.First()).ToList();
        }
    }
}
