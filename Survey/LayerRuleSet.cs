// Ported from the mcp-servers-for-revit survey pipeline (survey-pipeline.patch, base 86cf705). Revit-free.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BarrGazetas.RevitMcp.Survey
{
    public class LayerRuleSet
    {
        private readonly List<LayerRule> _ordered;
        private readonly Dictionary<LayerRule, List<Regex>> _match = new Dictionary<LayerRule, List<Regex>>();
        private readonly Dictionary<LayerRule, List<Regex>> _exclude = new Dictionary<LayerRule, List<Regex>>();
        private readonly Dictionary<string, LayerRule> _cache = new Dictionary<string, LayerRule>(StringComparer.OrdinalIgnoreCase);

        public List<string> ConfigProblems { get; } = new List<string>();

        public LayerRuleSet(IEnumerable<LayerRule> rules)
        {
            var list = (rules ?? new List<LayerRule>()).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (string.IsNullOrWhiteSpace(r.Name)) r.Name = "rule" + (i + 1);
                if (!Actions.IsValid(r.Action)) ConfigProblems.Add("Rule '" + r.Name + "': unknown action '" + r.Action + "' (valid: " + string.Join(", ", Actions.All) + ").");
                if (r.Match == null || r.Match.Count == 0) ConfigProblems.Add("Rule '" + r.Name + "': no 'match' patterns, it can never apply.");
                _match[r] = (r.Match ?? new List<string>()).Select(Glob).ToList();
                _exclude[r] = (r.Exclude ?? new List<string>()).Select(Glob).ToList();
            }
            // stable sort: priority desc, declaration order asc
            _ordered = list.Select((r, i) => new { r, i }).OrderByDescending(x => x.r.Priority).ThenBy(x => x.i).Select(x => x.r).ToList();
        }

        public IEnumerable<LayerRule> Rules { get { return _ordered; } }

        public static Regex Glob(string pattern)
        {
            string p = Regex.Escape((pattern ?? "").Trim()).Replace("\\*", ".*").Replace("\\?", ".");
            return new Regex("^" + p + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>Layer name as written in the CAD file, without xref prefixes ("XREF|A-WALL" -> "A-WALL", "X$0$A-WALL" -> "A-WALL").</summary>
        public static string StripXref(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return "";
            int i = Math.Max(layer.LastIndexOf('|'), layer.LastIndexOf('$'));
            return i >= 0 && i < layer.Length - 1 ? layer.Substring(i + 1) : layer;
        }

        public LayerRule Resolve(string layer)
        {
            layer = layer ?? "";
            LayerRule hit;
            if (_cache.TryGetValue(layer, out hit)) return hit;
            string bare = StripXref(layer);
            foreach (var r in _ordered)
            {
                if (_exclude[r].Any(rx => rx.IsMatch(layer) || rx.IsMatch(bare))) continue;
                if (_match[r].Any(rx => rx.IsMatch(layer) || rx.IsMatch(bare))) { hit = r; break; }
            }
            _cache[layer] = hit;
            return hit;
        }
    }
}
