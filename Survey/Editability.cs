// 1.33.0: how easy the model is for a person to correct (Mihai: "its work should always be easy to be corrected by a
// human"). Each finding is a kind of element that is hard to edit (a solid with no family or parameters, a curve built
// as straight pieces...) with what it costs to put right. No Revit types: tested in tests/SurveyCore.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public sealed class EditFinding
    {
        public string Kind, Why, Fix;
        public List<long> Elements = new List<long>();
    }

    public static class Editability
    {
        /// <summary>How hard each kind is to correct by hand, 0 (easy) to 1 (start again).</summary>
        public static readonly Dictionary<string, double> Cost = new Dictionary<string, double>
        {
            ["direct shape"] = 1.0,          // no family, no parameters, no grips: delete and remake
            ["in-place family"] = 0.8,       // one-off: edits don't carry to the others like it
            ["curve in straight pieces"] = 0.7, // each piece moved by hand to change the radius
            ["family without parameters"] = 0.5, // a loadable family, but its sizes are fixed
            ["one type per instance"] = 0.3, // windows that look alike but change one at a time
        };

        /// <summary>Share of the elements a person can correct directly: 1 - (sum of each hard element's cost) / total.</summary>
        public static double Score(IEnumerable<EditFinding> findings, int totalElements)
        {
            if (totalElements <= 0) return 1;
            var worst = new Dictionary<long, double>();
            foreach (var f in findings)
            {
                double c = Cost.TryGetValue(f.Kind, out var v) ? v : 0.5;
                foreach (var e in f.Elements) worst[e] = Math.Max(worst.TryGetValue(e, out var w) ? w : 0, c);
            }
            return Math.Round(Math.Max(0, 1 - worst.Values.Sum() / totalElements), 3);
        }
    }
}
