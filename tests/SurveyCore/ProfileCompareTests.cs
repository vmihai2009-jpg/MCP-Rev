using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class ProfileCompareTests
    {
        // a facade section as (out, z) lines: wall face at 0 from 0 to 12000, a string course 150 out at 3000-3200,
        // a cornice 400 out at 11000-11500 (a drawn test facade, not any real building)
        static List<(Pt, Pt, long)> Facade(double course, double cornice, long id = 0)
        {
            var l = new List<(Pt, Pt, long)> { (new Pt(0, 0), new Pt(0, 12000), id) };
            if (course > 0) l.AddRange(new[] { (new Pt(0, 3000), new Pt(course, 3000), id), (new Pt(course, 3000), new Pt(course, 3200), id), (new Pt(course, 3200), new Pt(0, 3200), id) });
            if (cornice > 0) l.AddRange(new[] { (new Pt(0, 11000), new Pt(cornice, 11500), id), (new Pt(cornice, 11500), new Pt(0, 11500), id) });
            return l;
        }

        public static void Run()
        {
            Console.WriteLine("facade depth (section vs model projection)");
            var (s, _) = ProfileCompare.Envelope(Facade(150, 400), 0, 12000);

            var (same, sameOwner) = ProfileCompare.Envelope(Facade(150, 400, 7), 0, 12000);
            var r0 = ProfileCompare.Compare(s, same, 0, 10, modelOwner: sameOwner);
            T.Check(r0.SharePct == 100 && r0.Bands.Count == 0 && r0.Rows > 0, $"depth: a model drawn as the section passes ({r0.SharePct}%, {r0.Bands.Count} bands)");

            // cornice built 250 out instead of 400 (a sloped cornice: the error grows with height)
            var (shallow, so) = ProfileCompare.Envelope(Facade(150, 250, 9), 0, 12000);
            var r1 = ProfileCompare.Compare(s, shallow, 0, 10, modelOwner: so);
            var b1 = r1.Bands.FirstOrDefault();
            T.Check(b1 != null && b1.Kind == "too_shallow" && b1.Z0 >= 11000 && b1.Z1 <= 11500 && b1.SurveyOut == 400 && b1.ModelOut == 250 && b1.Element == 9,
                $"depth: a cornice built too shallow is one band at its height, with its element ({b1?.Kind} {b1?.Z0}-{b1?.Z1} {b1?.SurveyOut}/{b1?.ModelOut} #{b1?.Element})");
            T.Check(r1.SharePct < 100 && r1.SharePct > 0, $"depth: the share drops but the string course still counts ({r1.SharePct}%)");

            // string course missing from the model
            var (noCourse, _) = ProfileCompare.Envelope(Facade(0, 400), 0, 12000);
            var r2 = ProfileCompare.Compare(s, noCourse, 0, 10);
            T.Check(r2.Bands.Count == 1 && r2.Bands[0].Kind == "missing" && r2.Bands[0].Z0 == 3000 && r2.Bands[0].Z1 == 3200,
                $"depth: a string course the model lacks is 'missing' at 3000-3200 ({string.Join(",", r2.Bands.Select(b => b.Kind + " " + b.Z0 + "-" + b.Z1))})");

            // a guessed 80 mm relief panel over flush wall at 6000-7000
            var extraLines = Facade(150, 400, 3); extraLines.AddRange(new[] { (new Pt(0, 6000), new Pt(80, 6000), 5L), (new Pt(80, 6000), new Pt(80, 7000), 5L), (new Pt(80, 7000), new Pt(0, 7000), 5L) });
            var (extra, eo) = ProfileCompare.Envelope(extraLines, 0, 12000);
            var r3 = ProfileCompare.Compare(s, extra, 0, 10, modelOwner: eo);
            T.Check(r3.Bands.Count == 1 && r3.Bands[0].Kind == "extra" && r3.Bands[0].Element == 5 && r3.Bands[0].ModelOut == 80,
                $"depth: a guessed relief over flush wall is 'extra', with the relief's element ({string.Join(",", r3.Bands.Select(b => b.Kind + " #" + b.Element))})");

            // where the section draws nothing (above its top), the model's detail is not judged
            var shortSec = (double[])s.Clone(); for (int i = 1050; i < shortSec.Length; i++) shortSec[i] = double.NaN;
            var r4 = ProfileCompare.Compare(shortSec, shallow, 0, 10);
            T.Check(r4.Bands.All(b => b.Z1 < 10500) && r4.SharePct == 100, $"depth: heights the section doesn't draw are not judged ({r4.SharePct}%)");

            // within tolerance: 8 mm out is fine at 10 mm
            var (near, _) = ProfileCompare.Envelope(Facade(158, 408), 0, 12000);
            T.Check(ProfileCompare.Compare(s, near, 0, 10).SharePct == 100, "depth: 8 mm out passes at a 10 mm tolerance");
            T.Check(ProfileCompare.Compare(s, near, 0, 10, tolMm: 5).SharePct < 100, "depth: and fails at 5 mm");
        }
    }
}
