// 1.36.0: the roof's structure from its faces (roof plan outlines, each with a plane fitted to its spot heights): every
// edge named (ridge, hip, valley, eave, verge, abutment, step), each face's pitch and the way it falls, and the edge a
// native Revit roof would slope from (the face's eave), with how far that single-slope roof would sit off the fitted plane.
// No Revit types: tested in tests/SurveyCore.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public sealed class RoofFace
    {
        public int Id;
        public List<Pt> Ring;
        public PlaneZ Plane;
        public Pt Centroid => new Pt(Ring.Average(p => p.X), Ring.Average(p => p.Y));
        public double PitchDeg => Plane?.SlopeDeg ?? 0;
        /// <summary>Plan direction the face falls towards, degrees from +x anticlockwise; null when flat.</summary>
        public double? FallsTowardDeg => Plane == null || Plane.SlopeDeg < 0.5 ? (double?)null : Math.Round(Math.Atan2(-Plane.B, -Plane.A) * 180 / Math.PI, 1);
    }

    public sealed class RoofEdge
    {
        public int A, B = -1, IndexInA;
        public Pt P0, P1;
        public string Kind;
        /// <summary>The two faces' planes apart along the edge (mm, at its middle); large = a step or a bad fit.</summary>
        public double GapMm;
        public double LengthMm => P0.Dist2D(P1);
    }

    public sealed class EaveFit
    {
        public int Edge;
        public double BaseZ, Slope, OffPlaneMm;
    }

    public static class RoofSolver
    {
        static double DistToSeg(Pt p, Pt a, Pt b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            double t = l2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2));
            return p.Dist2D(new Pt(a.X + t * dx, a.Y + t * dy));
        }

        static double Z(RoofFace f, Pt p) => f.Plane?.At(p.X, p.Y) ?? 0;

        /// <summary>Each face's ring edges, shared ones once (the other face holds the edge's middle on one of its edges).</summary>
        public static List<RoofEdge> Edges(IList<RoofFace> faces, double tolMm = 50, double flatDeg = 0.5)
        {
            var list = new List<RoofEdge>();
            for (int i = 0; i < faces.Count; i++)
            {
                var r = faces[i].Ring;
                for (int k = 0; k < r.Count; k++)
                {
                    Pt a = r[k], b = r[(k + 1) % r.Count];
                    if (a.Dist2D(b) < 1) continue;
                    var mid = new Pt((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    int other = -1;
                    for (int j = 0; j < faces.Count && other < 0; j++)
                    {
                        if (j == i) continue;
                        var q = faces[j].Ring;
                        for (int m = 0; m < q.Count; m++) if (DistToSeg(mid, q[m], q[(m + 1) % q.Count]) <= tolMm) { other = j; break; }
                    }
                    if (other >= 0 && other < i) continue; // listed from the other face
                    var e = new RoofEdge { A = i, B = other, IndexInA = k, P0 = a, P1 = b };
                    e.Kind = Classify(faces, e, mid, flatDeg);
                    if (other >= 0) e.GapMm = Math.Round(Math.Abs(Z(faces[i], mid) - Z(faces[other], mid)));
                    if (other >= 0 && e.GapMm > 150) e.Kind = "step";
                    list.Add(e);
                }
            }
            return list;
        }

        static string Classify(IList<RoofFace> faces, RoofEdge e, Pt mid, double flatDeg)
        {
            var fa = faces[e.A];
            bool flatA = fa.PitchDeg < flatDeg;
            double rise = Math.Abs(Z(fa, e.P0) - Z(fa, e.P1)), len = Math.Max(1, e.LengthMm);
            bool level = rise / len < 0.02;
            // how a face lies from the edge: + it rises away from the edge, - it falls away
            double Away(RoofFace f) => Z(f, f.Centroid) - Z(f, mid);
            if (e.B < 0)
            {
                if (flatA) return "flat edge";
                if (!level) return "verge";
                return Away(fa) > 0 ? "eave" : "abutment";
            }
            var fb = faces[e.B];
            if (flatA && fb.PitchDeg < flatDeg) return "flat join";
            double wa = Away(fa), wb = Away(fb);
            if (wa < -20 && wb < -20) return level ? "ridge" : "hip";
            if (wa > 20 && wb > 20) return level ? "valley gutter" : "valley";
            return "pitch change";
        }

        /// <summary>The edge a single-slope native roof springs from: the face's most level boundary-side edge at its low side
        /// (an eave when there is one), the base height there, the slope (rise over run) and how far that roof would be off
        /// the fitted plane at the face's corners.</summary>
        public static EaveFit Eave(RoofFace f)
        {
            if (f.Plane == null) return null;
            var r = f.Ring; double g = Math.Sqrt(f.Plane.A * f.Plane.A + f.Plane.B * f.Plane.B);
            EaveFit best = null; double bestScore = double.MaxValue;
            for (int k = 0; k < r.Count; k++)
            {
                Pt a = r[k], b = r[(k + 1) % r.Count];
                double len = a.Dist2D(b); if (len < 300) continue;
                double za = Z(f, a), zb = Z(f, b), lev = Math.Abs(za - zb) / len, zMid = (za + zb) / 2;
                double score = lev * 10000 + (zMid - r.Min(p => Z(f, p))); // most level first, then lowest
                if (score >= bestScore) continue;
                // slope perpendicular to the edge, from the plane's gradient across it
                double ux = (b.X - a.X) / len, uy = (b.Y - a.Y) / len, nx = -uy, ny = ux;
                var c = f.Centroid; if ((c.X - a.X) * nx + (c.Y - a.Y) * ny < 0) { nx = -nx; ny = -ny; } // n points into the face
                double slope = f.Plane.A * nx + f.Plane.B * ny;
                double off = r.Max(p => Math.Abs(zMid + slope * ((p.X - (a.X + b.X) / 2) * nx + (p.Y - (a.Y + b.Y) / 2) * ny) - Z(f, p)));
                bestScore = score;
                best = new EaveFit { Edge = k, BaseZ = Math.Round(zMid), Slope = slope, OffPlaneMm = Math.Round(off) };
            }
            if (best != null && g < 1e-6) best.Slope = 0;
            return best;
        }
    }
}
