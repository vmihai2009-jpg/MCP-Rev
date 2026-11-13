// 1.30.0: layer_roles_check: what each layer of the survey's plans holds, from the lines' geometry, against the role the
// layer rules give it (Survey to Revit's rules and the job's layers.txt). Disagreements are where a build goes wrong for
// every floor at once (hatching read as walls, walls on a layer the rules ignore).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> LayerRoleTools()
        {
            yield return new RevitTool
            {
                Name = "layer_roles_check",
                Description = "What each layer of the survey's plan DWGs holds, judged from its lines' shapes, not its name: wall faces (parallel " +
                              "pairs a wall's thickness apart), hatching (many parallel lines at a steady close pitch), furniture/fittings (small " +
                              "closed shapes), ticks; against the role the layer rules give the layer (Survey to Revit's rules plus layers.txt). " +
                              "Lists the layers where the two disagree, with what to put in layers.txt. Works on any surveyor's layer names. " +
                              "Default: the plan DWGs linked into the model's plan views; or paths. Changes nothing.",
                InputSchema = Schema(
                    ("paths", Arr("string", "DWG files to look at (default: the linked plans)"), false),
                    ("min_length_m", S("number", "Leave out layers with less line than this (default 5)"), false),
                    ("source", S("string", "As survey_fidelity"), false),
                    ("survey_folder", S("string", "As survey_fidelity"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var paths = Args.Strings(a, "paths");
                    if (paths.Count == 0)
                        paths = CadLink.All(doc).Where(i => i.ViewSpecific && doc.GetElement(i.OwnerViewId) is ViewPlan).Select(i => SafePath(doc, i)).Where(p => p != null && File.Exists(p)).Distinct().ToList();
                    if (paths.Count == 0) throw new ArgumentException("No plan DWGs linked: give paths.");
                    Func<IEnumerable<string>, Dictionary<string, string>> roles = null;
                    try { roles = ReadingFor(app, doc, a)?.Roles; } catch { }
                    double minLen = Args.Dbl(a, "min_length_m") ?? 5;
                    var files = new List<object>(); int disagree = 0;
                    foreach (var p in paths)
                    {
                        var f = DwgFile.Read(p);
                        var lines = f.Lines.Where(l => l.A.Dist2D(l.B) > 0).ToList();
                        var guesses = LineRoles.ByLayer(lines, LineRoles.Classify(lines)).Where(g => g.LengthM >= minLen).ToList();
                        var rule = roles?.Invoke(guesses.Select(g => g.Layer)) ?? new Dictionary<string, string>();
                        var rows = guesses.Select(g =>
                        {
                            rule.TryGetValue(g.Layer, out var r);
                            bool ruleWall = r == "Wall" || r == "CurtainWall" || (r == null && DwgFile.LayerMatches(g.Layer, new[] { "*Wall*" }));
                            bool geoWall = g.Guess == LineRoles.WallFace;
                            string issue = ruleWall && (g.Guess == LineRoles.Hatch || g.Guess == LineRoles.Fitting) ? $"read as walls, but holds {g.Guess}: walls will be built from it"
                                         : !ruleWall && geoWall && (g.Shares.TryGetValue(LineRoles.WallFace, out var ws) ? ws : 0) >= 0.6 && r != "Window" && r != "Door" && r != "Stair" ? "holds wall face pairs, but is not read as walls: walls on it are missed"
                                         : null;
                            return new { layer = g.Layer, rule = r ?? "(none)", looksLike = g.Guess, g.Shares, lengthM = g.LengthM, issue, suggest = issue == null ? null : (ruleWall ? $"{g.Layer} = Ignore" : $"{g.Layer} = Wall") };
                        }).ToList();
                        disagree += rows.Count(x => x.issue != null);
                        files.Add(new { file = Path.GetFileName(p), layers = rows.Count, disagreements = rows.Where(x => x.issue != null).ToList(), all = rows.Take(40).ToList() });
                    }
                    var answer = new
                    {
                        files, disagreements = disagree,
                        next = disagree == 0 ? "Layer roles agree with what the layers hold." :
                               "Check each disagreement with dwg_image (layer filter), then put the suggested line in the job's layers.txt (survey_layer_rules confirm: true) and read again (survey_analyze fresh: true).",
                    };
                    File.WriteAllText(Path.Combine(ReportsFolder(doc), "layer-roles-check.json"), JsonSerializer.Serialize(answer, JsonOpts));
                    return answer;
                }
            };
        }
    }
}
