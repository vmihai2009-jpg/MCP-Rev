// Revit-free. 1.12.0 (survey_run, PLAN V2): the score's issues grouped by their likely cause before they are listed,
// so Claude fixes one cause ("256 walls exactly 50 mm off: single-line walls centred on their face") instead of
// working 400 items one by one. Each cause has a count, the typical deviation, sample element ids and what to do.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace BarrGazetas.RevitMcp.Survey
{
    public class IssueCause
    {
        public string Cause { get; set; }
        public string Kind { get; set; }
        public int Count { get; set; }
        public double MedianMm { get; set; }
        /// <summary>True when the deviations agree (spread under 30 mm): one systematic error, not scattered ones.</summary>
        public bool Systematic { get; set; }
        public List<string> Levels { get; set; }
        public List<long> Ids { get; set; }
        public double[] At { get; set; }
        public string Action { get; set; }
        /// <summary>For ranking: the issues' severities summed.</summary>
        public double Weight { get; set; }
    }

    public static class IssueCauses
    {
        private static readonly Regex Thick = new Regex(@"DWG face pair (\d+) mm \(model (\d+) mm\)");

        public static List<IssueCause> Group(IEnumerable<FidIssue> issues, int sampleIds = 8)
        {
            var groups = new Dictionary<string, (string kind, string action, List<FidIssue> items)>();
            foreach (var i in issues ?? Enumerable.Empty<FidIssue>())
            {
                var (key, action) = Classify(i);
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = (i.Kind, action, new List<FidIssue>());
                g.items.Add(i);
            }
            var list = new List<IssueCause>();
            foreach (var kv in groups)
            {
                var items = kv.Value.items;
                var devs = items.Select(x => x.DevMm).OrderBy(v => v).ToList();
                double med = devs[devs.Count / 2];
                double q1 = devs[devs.Count / 4], q3 = devs[(devs.Count * 3) / 4];
                list.Add(new IssueCause
                {
                    Cause = kv.Key, Kind = kv.Value.kind, Count = items.Count, MedianMm = Math.Round(med),
                    Systematic = items.Count >= 3 && q3 - q1 <= 30 && !kv.Value.kind.EndsWith("_missing"),
                    Levels = items.Select(x => x.Level).Where(x => x != null).Distinct().ToList(),
                    Ids = items.OrderByDescending(x => x.Severity).Where(x => x.Id != null).Select(x => x.Id.Value).Distinct().Take(sampleIds).ToList(),
                    At = items.OrderByDescending(x => x.Severity).Select(x => x.At).FirstOrDefault(a => a != null),
                    Action = kv.Value.action,
                    Weight = Math.Round(items.Sum(x => Math.Max(1, x.Severity)), 1),
                });
            }
            return list.OrderByDescending(c => c.Weight).ToList();
        }

        /// <summary>The cause an issue most likely has, and what to do about all of them at once.</summary>
        public static (string cause, string action) Classify(FidIssue i)
        {
            double d = Math.Abs(i.DevMm);
            string against = i.Against ?? "";
            switch (i.Kind)
            {
                case "wall_face":
                    if (d >= 45 && d <= 60)
                        return ("walls ~50 mm off their face: a single drawn line read as a wall's centre",
                                "One cause, don't fix one by one: Survey to Revit centres a 100 mm wall on a single drawn face line (PLAN S1). survey_autofix fits the ones with both faces drawn; note the rest in the run results.");
                    if (d <= 25)
                        return ("walls 10-25 mm off: small offsets",
                                "survey_autofix openings:false (or dwg_wall_fit + dwg_wall_fit_apply) fits these; what it leaves is often a wall between two face pairs: split_wall.");
                    if (d <= 150)
                        return ("walls 25-150 mm off: wrong face or wrong thickness",
                                "dwg_image round the issue's `at` to see which line the wall should be on, then set_wall_line / change_type (set_wall_joins false first on facade walls).");
                    return ("walls over 150 mm off: wall on the wrong line",
                            "dwg_image round `at`: usually a wall built on a furniture/joinery/stair line or the wrong side of a chimney breast. Delete and survey_wall, or set_wall_line.");
                case "wall_face_curved":
                    return ("curved walls off their arc", "survey_autofix re-curves these; list what remains.");
                case "wall_thickness":
                {
                    var m = Thick.Match(against);
                    string model = m.Success ? m.Groups[2].Value : "?";
                    return ($"walls modelled {model} mm thick that the plan draws thicker or thinner",
                            "dwg_wall_fit_apply with the ids (types to the measured pair are made on demand); if many share one model thickness, it is a build default (layers.txt / S2R wall type), say so.");
                }
                case "wall_missing":
                    if (d < 1000)
                        return ("short walls/piers under 1 m missing",
                                "Piers and nibs between openings: dwg_image round `at` first (a chimney breast, a step or joinery outline is not a wall), then survey_wall with the two face lines.");
                    return ("walls over 1 m missing",
                            "Check they cluster on one layer (survey_layers only_unassigned): a layer fix + rebuild of the floor beats hand-adding. Else survey_wall with the face lines.");
                case "opening":
                {
                    string what = against.StartsWith("elevation sill") ? "sill vs elevation" : against.StartsWith("elevation head") ? "head vs elevation"
                        : against.StartsWith("sill label") ? "sill vs plan label" : against.StartsWith("head label") ? "head vs plan label"
                        : against.StartsWith("plan jambs") ? "width/position vs plan jambs" : "other";
                    string dir = i.DevMm > 0 ? "model too high/wide" : "model too low/narrow";
                    string action = what.Contains("elevation") ? "survey_autofix walls:false sets sill/head from the elevation; if many agree on one offset, the elevation's level marks or the level itself is off: check levelCheck and dwg_image the elevation."
                        : what.Contains("label") ? "Labels are measured inside: if an elevation draws these windows, run facade_list + elevation_index so the elevation decides; else survey_autofix walls:false."
                        : what.Contains("jambs") ? "survey_autofix walls:false resizes from the plan reveals; wide ones may be merged windows: split_opening."
                        : "set_insert per issue.";
                    return ($"windows/doors: {what}, {dir}", action);
                }
                case "opening_missing":
                    if (against.StartsWith("elevation")) return ("windows the elevation draws that the model lacks", "survey_autofix places them (sill/head from the elevation); dwg_image facade/along to look first if many on one facade.");
                    if (against.Contains("a door")) return ("doors on the plan the model lacks", "place_inserts a door in the wall at each (width from the plan); pick the family and swing.");
                    return ("windows on the plan the model lacks", "survey_autofix places them (plan width, labelled/elevation heights).");
                case "level":
                    return ("level off the plan's spot heights", "Fix this FIRST: every sill, head and slab on the floor follows the level. set_parameters Elevation, then re-run.");
                case "room_missing": case "room_name":
                    return ("rooms missing or misnamed", "place_rooms at the plan's labels (dwg_read text near `at`).");
                case "ceiling": case "ceiling_missing":
                    return ("ceilings off the plan's notes", "survey_ceilings apply / create_missing.");
                default:
                    return (i.Kind ?? "other", i.Fix ?? "See each issue's fix.");
            }
        }

        /// <summary>One CSV row per number, for runs/score-history.csv (V5): when, model, version, level, metric, value.</summary>
        public static string HistoryRows(string when, string model, string version, string level, IEnumerable<(string metric, double value)> values) =>
            string.Concat(values.Select(v => string.Join(",", Csv(when), Csv(model), Csv(version), Csv(level), Csv(v.metric), v.value.ToString("0.###", CultureInfo.InvariantCulture)) + "\n"));

        public const string HistoryHeader = "when,model,version,level,metric,value\n";

        private static string Csv(string s) => s == null ? "" : s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
