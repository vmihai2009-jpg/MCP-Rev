// 1.32.0: one "review these first" queue from every check's findings, and where each built element came from (its
// Comments tag). An element named by several checks rises; each item says why, from which report, and the fix tool.
// No Revit types: tested in tests/SurveyCore.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public sealed class ReviewEntry
    {
        public string Report, Kind, Where, Why, Fix;
        public long? Element;
        /// <summary>How much it matters: roughly mm of facade or wall wrong (a floor out = 20000, an element 3000-5000).</summary>
        public double Weight;
    }

    public sealed class QueueItem
    {
        public int Rank;
        public long? Element;
        public string Where;
        public double Weight, Confidence;
        public List<ReviewEntry> Reasons = new List<ReviewEntry>();
    }

    public static class ReviewQueue
    {
        /// <summary>Entries naming the same element become one item (weights add, so an element several checks question rises);
        /// entries with no element stay one each. Ordered by weight, heaviest first. Confidence = how sure the model is right
        /// there, 0-1, falling with the weight.</summary>
        public static List<QueueItem> Rank(IEnumerable<ReviewEntry> entries)
        {
            var items = new List<QueueItem>();
            foreach (var g in entries.Where(e => e.Element.HasValue).GroupBy(e => e.Element.Value))
                items.Add(new QueueItem { Element = g.Key, Where = g.First().Where, Weight = g.Sum(e => e.Weight), Reasons = g.OrderByDescending(e => e.Weight).ToList() });
            foreach (var e in entries.Where(e => !e.Element.HasValue))
                items.Add(new QueueItem { Where = e.Where, Weight = e.Weight, Reasons = new List<ReviewEntry> { e } });
            items = items.OrderByDescending(i => i.Weight).ThenBy(i => i.Where, StringComparer.Ordinal).ToList();
            for (int k = 0; k < items.Count; k++) { items[k].Rank = k + 1; items[k].Confidence = Math.Round(Math.Exp(-items[k].Weight / 10000), 2); }
            return items;
        }

        /// <summary>Where an element came from, from its Comments: Survey to Revit's tag (SurveyToRevit|level|what), this hub's
        /// (MCP|tool|...), or none (drawn by a person, or a tool that does not tag).</summary>
        public static (string by, string what) Provenance(string comments)
        {
            if (string.IsNullOrWhiteSpace(comments)) return ("untagged", null);
            var p = comments.Split('|');
            if (p[0] == "SurveyToRevit") return ("Survey to Revit", p.Length > 2 ? p[2] : null);
            if (p[0] == "MCP" || p[0] == "BGMCP") return (p.Length > 1 ? "MCP " + p[1] : "MCP", p.Length > 2 ? string.Join("|", p.Skip(2)) : null);
            if (comments.StartsWith("BGMCP", StringComparison.Ordinal)) return ("MCP", comments);
            return ("untagged", null);
        }
    }
}
