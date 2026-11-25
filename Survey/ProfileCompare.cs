// Revit-free. 1.39.0: facade_depth_check's core. The facade checks before this one (facade_correspondence,
// facade_course_check, opening_clear_check) look at the facade face-on, in the elevation's u/z frame: they can say a
// cornice is at the right height and the right length, but not whether it stands out 180 mm or 450 mm. Depth is only
// drawn in the survey's sections. This compares, at every height, how far the section draws the facade standing proud of
// its own wall face with how far the model does on the same cut plane, and names the stretches of height that disagree.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>A stretch of height where the model's projection and the section's disagree by more than the tolerance.</summary>
    public class DepthBand
    {
        public double Z0, Z1;
        /// <summary>Largest projection over the stretch, mm out from each one's own wall face.</summary>
        public double SurveyOut, ModelOut;
        /// <summary>missing (the section draws detail, the model is flush) | extra (the model stands out, the section is flush)
        /// | too_deep | too_shallow</summary>
        public string Kind;
        /// <summary>The model element that is outermost over most of the stretch (0 if none).</summary>
        public long Element;
        /// <summary>Error area (mm x mm) = how much it matters.</summary>
        public double Weight;
    }

    public class DepthCompareResult
    {
        /// <summary>Heights compared (every step mm where the section or the model stands out at least minProud).</summary>
        public int Rows;
        public int Within;
        public double SharePct, MeanAbsMm, MaxAbsMm;
        public List<DepthBand> Bands = new List<DepthBand>();
    }

    public static class ProfileCompare
    {
        /// <summary>
        /// The outermost point at each height (as SectionProfile.Envelope) and which line's owner gives it: lines as (out, z)
        /// mm with an owner id (a model element; 0 for drawn lines).
        /// </summary>
        public static (double[] env, long[] owner) Envelope(IList<(Pt a, Pt b, long owner)> lines, double z0, double z1, double step = 10,
                                                           double minOut = -3000, double maxOut = 2500)
        {
            int n = Math.Max(1, (int)Math.Ceiling((z1 - z0) / step) + 1);
            var o = Enumerable.Repeat(double.NaN, n).ToArray();
            var who = new long[n];
            foreach (var (a, b, id) in lines)
            {
                double lo = Math.Min(a.Y, b.Y), hi = Math.Max(a.Y, b.Y);
                int i0 = Math.Max(0, (int)Math.Ceiling((lo - z0) / step)), i1 = Math.Min(n - 1, (int)Math.Floor((hi - z0) / step));
                for (int i = i0; i <= i1; i++)
                {
                    double z = z0 + i * step;
                    double x = hi - lo < 1e-6 ? Math.Max(a.X, b.X) : a.X + (z - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                    if (x < minOut || x > maxOut) continue;
                    if (double.IsNaN(o[i]) || x > o[i]) { o[i] = x; who[i] = id; }
                }
            }
            return (o, who);
        }

        /// <summary>
        /// Compare two envelopes on the same heights (z0 + i * step), each already measured from its own wall face. Heights
        /// where the section draws nothing are not judged (no evidence there); where the model has nothing it counts as flush.
        /// Recesses behind the face count as flush (an envelope cannot see behind the face). Only heights where either
        /// stands out minProud or more are compared, so plain wall does not inflate the score. Bands shorter than minBandMm
        /// are left out of the list (still counted in the share).
        /// </summary>
        public static DepthCompareResult Compare(double[] survey, double[] model, double z0, double step, double tolMm = 10,
                                                 double minProud = 15, double minBandMm = 30, long[] modelOwner = null,
                                                 double zFrom = double.MinValue, double zTo = double.MaxValue)
        {
            var r = new DepthCompareResult();
            int n = Math.Min(survey.Length, model.Length);
            var diffs = new double?[n];
            var kinds = new string[n];
            double sumAbs = 0;
            for (int i = 0; i < n; i++)
            {
                double z = z0 + i * step;
                if (z < zFrom || z > zTo || double.IsNaN(survey[i])) continue;
                double s = Math.Max(0, survey[i]), m = double.IsNaN(model[i]) ? 0 : Math.Max(0, model[i]);
                if (s < minProud && m < minProud) continue;
                double d = m - s;
                r.Rows++;
                sumAbs += Math.Abs(d);
                r.MaxAbsMm = Math.Max(r.MaxAbsMm, Math.Abs(d));
                if (Math.Abs(d) <= tolMm) { r.Within++; continue; }
                diffs[i] = d;
                kinds[i] = m < minProud ? "missing" : s < minProud ? "extra" : d > 0 ? "too_deep" : "too_shallow";
            }
            r.SharePct = r.Rows == 0 ? 100 : Math.Round(100.0 * r.Within / r.Rows, 1);
            r.MeanAbsMm = r.Rows == 0 ? 0 : Math.Round(sumAbs / r.Rows, 1);
            r.MaxAbsMm = Math.Round(r.MaxAbsMm, 1);

            // contiguous rows of one kind make a band (a one-row gap of the same kind is bridged)
            int k = 0;
            while (k < n)
            {
                if (kinds[k] == null) { k++; continue; }
                string kind = kinds[k];
                int start = k, end = k;
                int j = k + 1;
                while (j < n && (kinds[j] == kind || (kinds[j] == null && j + 1 < n && kinds[j + 1] == kind))) { if (kinds[j] == kind) end = j; j++; }
                double h = (end - start + 1) * step;
                if (h >= minBandMm)
                {
                    double so = 0, mo = 0, w = 0;
                    var owners = new Dictionary<long, int>();
                    for (int i = start; i <= end; i++)
                    {
                        if (kinds[i] == null) continue;
                        so = Math.Max(so, Math.Max(0, survey[i]));
                        mo = Math.Max(mo, double.IsNaN(model[i]) ? 0 : Math.Max(0, model[i]));
                        w += Math.Abs(diffs[i].Value) * step;
                        if (modelOwner != null && i < modelOwner.Length && modelOwner[i] != 0 && !double.IsNaN(model[i]) && model[i] >= minProud)
                            owners[modelOwner[i]] = owners.TryGetValue(modelOwner[i], out var c) ? c + 1 : 1;
                    }
                    r.Bands.Add(new DepthBand
                    {
                        Z0 = Math.Round(z0 + start * step), Z1 = Math.Round(z0 + end * step), SurveyOut = Math.Round(so), ModelOut = Math.Round(mo),
                        Kind = kind, Weight = Math.Round(w), Element = owners.Count == 0 ? 0 : owners.OrderByDescending(x => x.Value).First().Key,
                    });
                }
                k = end + 1;
            }
            r.Bands = r.Bands.OrderByDescending(b => b.Weight).ToList();
            return r;
        }

        /// <summary>What to run for a band of this kind (named for the person reading the report).</summary>
        public static string Fix(string kind)
        {
            switch (kind)
            {
                case "missing": return "The section draws a band here that the model lacks: facade_profile (see its 'rejected' list for why this height was left out).";
                case "extra": return "The model stands out where the section is flush: a guessed relief depth (facade_relief / facade_types depth_mm) or a band from another bay's section.";
                case "too_deep":
                case "too_shallow": return "Projection differs: the profile came from another section, or the depth was guessed. Rebuild this band's profile from this section.";
                default: return "";
            }
        }
    }
}
