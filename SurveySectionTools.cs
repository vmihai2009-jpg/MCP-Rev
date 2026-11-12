// 1.11.0: the 3D part of the 1:1 score. survey_section cuts the model on each survey section's plane and measures
// every element there (slabs, roofs, wall and parapet tops, stairs: Survey/SectionFit.cs) against the section sheet;
// survey_section_autofix makes the height fixes that are certain. Where a section is comes from section_index
// (section-index.json), else Survey to Revit's "Survey Section X" views (cut lines from the key plan), else the call.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        private static readonly string[] SectionIgnoreLayers = { "Zz_*", "*Text*", "*Level*", "*Frame*", "*Border*", "*Title*", "*Grid*", "*Hatch*", "Defpoints" };

        private static JsonObject SectionSchema(bool apply)
        {
            var props = new List<(string, JsonObject, bool)>
            {
                ("sections", Arr("string", "Sections by name or letter (\"A\", \"Section-B\"). Default: every section in section-index.json, else every \"Survey Section\" view Survey to Revit made"), false),
                ("path", S("string", "The section sheet (DWG/DXF), for a section given here rather than indexed"), false),
                ("sheet_x_range", Arr("number", "[x1, x2] of that section on the sheet, sheet mm (default: halfway to the titles beside it)"), false),
                ("sheet_y_range", Arr("number", "Optional [y1, y2] on the sheet"), false),
                ("cut_line", Arr("number", "[x1, y1, x2, y2] the cut in plan, model mm (or view_id of a section view)"), false),
                ("view_id", S("integer", "A section view whose plane is the cut"), false),
                ("along_direction", S("integer", "1 if the sheet's left to right runs from the cut line's start to its end, -1 the other way (default: found from the wall faces)"), false),
                ("line_layers", Arr("string", "Which layers of the sheet are the section (default: *Profile* where the sheet has it, else all but text, level and frame layers)"), false),
                ("level_layers", Arr("string", "Level mark layers (default *Level*)"), false),
            };
            if (apply)
            {
                props.Add(("dry_run", S("boolean", "Try and roll back (default false)"), false));
                props.Add(("max_change_mm", S("number", "Leave alone anything the section says is further off than this (default 3000)"), false));
                props.Add(("walls", S("boolean", "Wall and parapet tops (default true)"), false));
                props.Add(("slabs", S("boolean", "Slab tops (default true)"), false));
                props.Add(("roofs", S("boolean", "Roofs moved up or down when ridge and eaves agree (default true)"), false));
            }
            else
            {
                props.Add(("image", S("boolean", "Also return a picture per section: the section's lines dark, the model's cut red, issues ringed (default false)"), false));
                props.Add(("max_issues", S("integer", "Worst issues to return (default 20)"), false));
                props.Add(("write_report", S("boolean", "Write JSON and the pictures to the reports folder (default true)"), false));
            }
            return Schema(props.ToArray());
        }

        public static IEnumerable<RevitTool> Sections3D()
        {
            yield return new RevitTool
            {
                Name = "survey_section",
                Description = "The 1:1 score in 3D: the model cut on each survey section's plane and measured against the section sheet. Every cut edge of every wall, floor, roof, ceiling, stair and generic model against the nearest parallel section line (profile: share within fidelityGate.sectionTolMm, default 20 mm); and per element: slab top and thickness, wall and parapet tops (a parapet the model lacks shows as its wall's top too low), roof ridge, eaves and pitch (a roof modelled flat that the section draws pitched is a roof_form issue with the pitch to build), stair risers. Long section lines no model element follows are listed (a missing slab or roof slope, or detail beyond the cut). " +
                              "Where the section is: section_index's section-index.json, else Survey to Revit's \"Survey Section X\" views and the sheet titled \"Section X\", else path + cut_line. image true returns a picture of each section with the model drawn over it. Changes nothing.",
                InputSchema = SectionSchema(false),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var gate = McpSettings.Load().FidelityGate ?? new FidelityGate();
                    var (inputs, notes) = SectionInputs(doc, a, app);
                    var scores = new List<SectionScore>(); var all = new List<FidIssue>(); var images = new List<(string name, byte[] png)>();
                    foreach (var input in inputs)
                    {
                        var (score, issues) = SectionFit.Measure(input, gate);
                        scores.Add(score); all.AddRange(issues);
                        if (Args.Bool(a, "image") == true || Args.Bool(a, "write_report") != false)
                            try { images.Add((input.Name, SectionFit.Overlay(input, issues, 1600))); } catch (Exception ex) { notes.Add($"{input.Name}: no picture ({ex.Message})"); }
                    }
                    all = all.OrderByDescending(i => i.Severity).ToList();
                    object report = null;
                    if (Args.Bool(a, "write_report") != false)
                    {
                        string stem = Path.Combine(ReportsFolder(doc), $"section-score-{DateTime.Now:yyyyMMdd-HHmmss}");
                        File.WriteAllText(stem + ".json", SurveyConfig.ToJson(new { when = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), gate, sections = scores, issues = all }).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        var pngs = new List<string>();
                        foreach (var (name, png) in images) { string f = $"{stem}-{Safe(name)}.png"; File.WriteAllBytes(f, png); pngs.Add(f); }
                        report = new { json = stem + ".json", pictures = pngs };
                    }
                    int max = Math.Clamp(Args.Int(a, "max_issues", 20), 0, 200);
                    var answer = new
                    {
                        gate = new { gate.SectionTolMm, gate.SectionWithinPct, gate.HeightMm, gate.SlabThicknessMm, gate.PitchDeg, gate.RiserMm },
                        passed = scores.Count(s => s.Pass), sectionsMeasured = scores.Count,
                        sections = scores.Select(s => new
                        {
                            section = s.Section, pass = s.Pass, failedOn = s.FailedOn, unverified = s.Unverified.Count > 0 ? s.Unverified : null, score = s.Score,
                            profile = new { s.Elements, s.Verified, s.CutLengthM, withinTolPct = s.WithinTolPct },
                            over = new { wallTops = s.TopsOver, slabs = s.SlabsOver, roofs = s.RoofsOver, stairs = s.StairsOver },
                            unmatched = new { lines = s.Unmatched, metres = s.UnmatchedM },
                            sources = s.Sources, issues = s.IssueCount,
                        }).ToList(),
                        notes = notes.Count > 0 ? notes : null,
                        issues = SurveyConfig.ToJson(all.Take(max).ToList()), issuesTotal = all.Count,
                        report,
                        units = "Issue At is [u along the cut, z model height] in mm; Level is the section's name; DevMm is survey minus model.",
                        next = scores.All(s => s.Pass) ? "Every section passes." : "survey_section_autofix for wall tops, parapets, slab tops and roof heights; the rest by hand (each issue says how), then survey_section again.",
                    };
                    string text = JsonSerializer.Serialize(answer, JsonOpts);
                    if (Args.Bool(a, "image") != true) return text;
                    var c = new ToolContent { Text = text };
                    foreach (var (_, png) in images.Take(4)) c.Images.Add(("image/png", png));
                    return c;
                }
            };

            yield return new RevitTool
            {
                Name = "survey_section_autofix",
                Write = true,
                Description = "Make the section fixes that are certain, then score the sections again. Wall and parapet tops: the top offset (or unconnected height) moved to the section's top; walls attached to a roof or floor are left. Slab tops: the floor's height offset. Roofs: moved up or down when ridge and eaves are off by the same (a different pitch is left for create_roof). An element two sections disagree on (more than 20 mm apart) is left. One undo step; dry_run tries and rolls back.",
                InputSchema = SectionSchema(true),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var gate = McpSettings.Load().FidelityGate ?? new FidelityGate();
                    bool dry = Args.Bool(a, "dry_run") == true;
                    double maxChange = Args.Dbl(a, "max_change_mm") ?? 3000;
                    var (inputs, notes) = SectionInputs(doc, a, app);
                    var before = inputs.Select(i => SectionFit.Measure(i, gate)).ToList();
                    // one change per element: every section that measured it must agree
                    var byElement = new Dictionary<(long id, string what), List<double>>();
                    void Want(long id, string what, double dev) { if (!byElement.TryGetValue((id, what), out var l)) byElement[(id, what)] = l = new List<double>(); l.Add(dev); }
                    foreach (var (score, issues) in before)
                    {
                        foreach (var i in issues.Where(i => i.Id != null && Math.Abs(i.DevMm) <= maxChange))
                        {
                            if ((i.Kind == "wall_top" || i.Kind == "parapet_top") && Args.Bool(a, "walls") != false) Want(i.Id.Value, "top", i.DevMm);
                            else if (i.Kind == "slab_top" && Args.Bool(a, "slabs") != false) Want(i.Id.Value, "slab", i.DevMm);
                        }
                        if (Args.Bool(a, "roofs") != false)
                            foreach (var it in score.Items.Where(x => x.Kind == "roof" && x.RidgeDevMm is double rd && Math.Abs(rd) > gate.HeightMm && Math.Abs(rd) <= maxChange))
                                if (it.EavesDevMm == null || Math.Abs(it.EavesDevMm.Value - it.RidgeDevMm.Value) <= 15)
                                    Want(it.Id, "roof", it.EavesDevMm == null ? it.RidgeDevMm.Value : (it.RidgeDevMm.Value + it.EavesDevMm.Value) / 2);
                    }
                    var done = new List<object>(); var left = new List<string>();
                    using (var g = new TransactionGroup(doc, "MCP: survey section autofix"))
                    {
                        g.Start();
                        foreach (var kv in byElement)
                        {
                            var devs = kv.Value;
                            if (devs.Max() - devs.Min() > 20) { left.Add($"{kv.Key.what} {kv.Key.id}: the sections disagree ({string.Join(", ", devs.Select(d => d.ToString("+0;-0")))} mm)"); continue; }
                            double dev = devs.Average();
                            var el = doc.GetElement(new ElementId(kv.Key.id));
                            try
                            {
                                string how = null;
                                BuildTools.Tx.Run(doc, $"MCP: section fix {kv.Key.what} {kv.Key.id}", () => how = ApplySectionFix(el, kv.Key.what, dev));
                                if (how == null) left.Add($"{kv.Key.what} {kv.Key.id}: no parameter to change ({el?.Category?.Name})");
                                else done.Add(new { id = kv.Key.id, what = kv.Key.what, byMm = Math.Round(dev), how });
                            }
                            catch (Exception ex) { left.Add($"{kv.Key.what} {kv.Key.id}: {McpServer.Unwrap(ex).Message}"); }
                        }
                        var (after, notes2) = SectionInputs(doc, a, app);
                        var afterScores = after.Select(i => SectionFit.Measure(i, gate)).ToList();
                        if (dry || done.Count == 0) g.RollBack(); else g.Assimilate();
                        return new
                        {
                            dryRun = dry, changed = done, left = left.Count > 0 ? left : null,
                            sections = afterScores.Select((x, k) => new
                            {
                                section = x.score.Section, pass = x.score.Pass, failedOn = x.score.FailedOn,
                                score = new { before = k < before.Count ? before[k].score.Score : (double?)null, after = x.score.Score },
                                stillFailing = SurveyConfig.ToJson(x.issues.Where(i => i.Kind != "section_unmatched").Take(10).ToList()),
                            }).ToList(),
                            notes = notes.Concat(notes2).Distinct().ToList(),
                            undo = dry ? "dry run: nothing kept" : done.Count > 0 ? "one undo step in Revit" : "nothing changed",
                        };
                    }
                }
            };
        }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };

        /// <summary>One height fix; returns what was set, or null when the element has nothing to set.</summary>
        private static string ApplySectionFix(Element el, string what, double devMm)
        {
            double d = devMm / Mm;
            switch (what)
            {
                case "top" when el is Wall w:
                {
                    if (w.get_Parameter(BuiltInParameter.WALL_TOP_IS_ATTACHED)?.AsInteger() == 1) throw new InvalidOperationException("its top is attached to a roof or floor: detach it or move that instead");
                    var topType = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId();
                    if (topType != null && topType != ElementId.InvalidElementId)
                    {
                        var p = w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
                        p.Set(p.AsDouble() + d);
                        return $"top offset {p.AsDouble() * Mm:0} mm";
                    }
                    var h = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                    h.Set(h.AsDouble() + d);
                    return $"unconnected height {h.AsDouble() * Mm:0} mm";
                }
                case "slab" when el is Floor f:
                {
                    var p = f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                    p.Set(p.AsDouble() + d);
                    return $"height offset {p.AsDouble() * Mm:0} mm";
                }
                case "roof" when el is RoofBase r:
                {
                    var p = r.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
                    if (p == null || p.IsReadOnly) p = r.get_Parameter(BuiltInParameter.ROOF_CONSTRAINT_OFFSET_PARAM);
                    if (p == null || p.IsReadOnly) return null;
                    p.Set(p.AsDouble() + d);
                    return $"base offset {p.AsDouble() * Mm:0} mm";
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ where the sections are

        private sealed class SectionSpec
        {
            public string Name, Path;
            public double X0 = double.MinValue, X1 = double.MaxValue, Y0 = double.MinValue, Y1 = double.MaxValue;
            public Pt A, B;
            public AlongFit Along;
            public string From;
        }

        private static (List<SectionInput> inputs, List<string> notes) SectionInputs(Document doc, JsonObject a, Autodesk.Revit.UI.UIApplication app)
        {
            var notes = new List<string>();
            // Survey to Revit's reading of the survey: section cut lines and the role of each layer; null without it
            SurveySource source = null;
            if (app != null) { try { source = ReadingFor(app, doc, a); } catch (Exception ex) { notes.Add("Survey to Revit's reading: " + McpServer.Unwrap(ex).Message); } }
            var specs = SectionSpecs(doc, a, notes, source?.Reading);
            if (specs.Count == 0) throw new ArgumentException("No section to measure: run section_index (or Survey to Revit's survey_build with sections), or give path and cut_line. " + string.Join(" ", notes));
            var inputs = new List<SectionInput>();
            foreach (var spec in specs)
            {
                try { inputs.Add(SectionInputFor(doc, spec, a, notes, source?.Roles)); }
                catch (Exception ex) { notes.Add($"{spec.Name}: {McpServer.Unwrap(ex).Message}"); }
            }
            if (inputs.Count == 0) throw new InvalidOperationException("No section could be read: " + string.Join(" ", notes));
            return (inputs, notes);
        }

        private static bool SameSection(string a, string b)
        {
            string N(string s) => (s ?? "").Trim().ToUpperInvariant().Replace("SURVEY SECTION", "").Replace("SECTION", "").Trim(' ', '-', '_');
            return N(a) == N(b);
        }

        private static List<SectionSpec> SectionSpecs(Document doc, JsonObject a, List<string> notes, SurveyReading reading = null)
        {
            var wanted = Args.Strings(a, "sections");
            var specs = new List<SectionSpec>();

            // given in the call
            if (Args.Str(a, "path") is string path)
            {
                var spec = new SectionSpec { Name = wanted.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(path), Path = path, From = "the call" };
                var xr = Args.Numbers(a, "sheet_x_range");
                if (xr.Count == 2) { spec.X0 = Math.Min(xr[0], xr[1]); spec.X1 = Math.Max(xr[0], xr[1]); }
                else if (wanted.Count > 0) (spec.X0, spec.X1) = SheetRangeOf(path, wanted[0]);
                var yr = Args.Numbers(a, "sheet_y_range");
                if (yr.Count == 2) { spec.Y0 = Math.Min(yr[0], yr[1]); spec.Y1 = Math.Max(yr[0], yr[1]); }
                var cl = Args.Numbers(a, "cut_line");
                if (cl.Count == 4) { spec.A = new Pt(cl[0], cl[1]); spec.B = new Pt(cl[2], cl[3]); }
                else if (Args.Long(a, "view_id") is long vid && doc.GetElement(new ElementId(vid)) is ViewSection vs) (spec.A, spec.B) = CutOf(vs);
                else if (SurveySectionViews(doc).FirstOrDefault(v => SameSection(v.Name, spec.Name)) is ViewSection sv) (spec.A, spec.B) = CutOf(sv);
                else throw new ArgumentException("Give cut_line [x1, y1, x2, y2] (model mm) or view_id of the section view.");
                specs.Add(spec);
                return specs;
            }

            // section_index's file
            string idx = Path.Combine(ReportsFolder(doc), "section-index.json");
            if (File.Exists(idx))
            {
                var root = JsonNode.Parse(File.ReadAllText(idx));
                foreach (var o in (root?["sections"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    string name = o["name"]?.ToString(), letter = o["letter"]?.ToString();
                    if (wanted.Count > 0 && !wanted.Any(w => SameSection(w, name) || SameSection(w, letter))) continue;
                    var cut = (o["cut"] as JsonArray)?.Select(v => v.GetValue<double>()).ToList();
                    var xr = (o["sheet_x_range"] as JsonArray)?.Select(v => v.GetValue<double>()).ToList();
                    if (cut?.Count != 4 || xr?.Count != 2) continue;
                    var yr = (o["sheet_y_range"] as JsonArray)?.Select(v => v.GetValue<double>()).ToList();
                    var spec = new SectionSpec
                    {
                        Name = name ?? letter, Path = o["path"]?.ToString(), A = new Pt(cut[0], cut[1]), B = new Pt(cut[2], cut[3]),
                        X0 = Math.Min(xr[0], xr[1]), X1 = Math.Max(xr[0], xr[1]), From = "section-index.json",
                    };
                    if (yr?.Count == 2) { spec.Y0 = Math.Min(yr[0], yr[1]); spec.Y1 = Math.Max(yr[0], yr[1]); }
                    int dir = (int)(o["dir"]?.GetValue<double>() ?? 0), matched = (int)(o["matched"]?.GetValue<double>() ?? 0);
                    double offset = o["offset"]?.GetValue<double>() ?? 0;
                    // section_index: sheet x = dir * t + offset, so t = dir * x - dir * offset
                    if (dir != 0 && matched >= 4) spec.Along = new AlongFit { Dir = dir, Offset = -dir * offset, Matched = matched, Note = $"placed by section_index ({matched} walls matched)" };
                    specs.Add(spec);
                }
                if (specs.Count > 0) return specs;
                notes.Add("section-index.json has none of those sections.");
            }

            // Survey to Revit's reading: the cut lines it placed from each sheet's key plan
            if (reading != null)
            {
                foreach (var rs in reading.Sections.Where(x => x.Cut != null && x.File != null))
                {
                    if (wanted.Count > 0 && !wanted.Any(w => SameSection(w, rs.Name))) continue;
                    string sheetPath = Path.IsPathRooted(rs.File) ? rs.File : reading.Folder != null ? Path.Combine(reading.Folder, rs.File) : null;
                    if (sheetPath == null || !File.Exists(sheetPath)) { notes.Add($"Section {rs.Name}: its sheet {rs.File} is not in the survey folder."); continue; }
                    var (x0, x1) = SheetRangeOf(sheetPath, rs.Name);
                    specs.Add(new SectionSpec { Name = "Section " + rs.Name, Path = sheetPath, X0 = x0, X1 = x1, A = rs.Cut.Value.A, B = rs.Cut.Value.B, From = $"Survey to Revit's reading (key-plan cut on {rs.File})" });
                }
                if (specs.Count > 0) return specs;
            }

            // Survey to Revit's section views, with the sheet titled for each
            var views = SurveySectionViews(doc).Where(v => wanted.Count == 0 || wanted.Any(w => SameSection(w, v.Name))).ToList();
            if (views.Count == 0) return specs;
            var sheets = SectionSheets(doc);
            foreach (var v in views)
            {
                string name = v.Name.Replace("Survey Section", "").Trim();
                var sheet = sheets.FirstOrDefault(s => s.titles.Any(t => SameSection(t.name, name)));
                if (sheet.path == null) { notes.Add($"{v.Name}: no section sheet titled \"Section {name}\" among the linked or neighbouring DWGs; give path."); continue; }
                var (x0, x1) = SectionFit.SheetRange(sheet.titles.Select(t => (t.name, t.x)).ToList(), name);
                var (A, B) = CutOf(v);
                specs.Add(new SectionSpec { Name = "Section " + name, Path = sheet.path, X0 = x0, X1 = x1, A = A, B = B, From = $"view \"{v.Name}\" (Survey to Revit's key-plan cut) and {Path.GetFileName(sheet.path)}" });
            }
            return specs;
        }

        private static List<ViewSection> SurveySectionViews(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Section && v.Name.StartsWith("Survey Section", StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>A section view's cut in plan, model mm: through its origin along its right direction, over its crop box.</summary>
        private static (Pt a, Pt b) CutOf(ViewSection v)
        {
            var box = v.CropBox;
            var t = box.Transform;
            XYZ p0 = t.OfPoint(new XYZ(box.Min.X, 0, 0)), p1 = t.OfPoint(new XYZ(box.Max.X, 0, 0));
            // onto the view's own cut plane (through its origin)
            var n = v.ViewDirection;
            XYZ On(XYZ p) => p - n.Multiply((p - v.Origin).DotProduct(n));
            p0 = On(p0); p1 = On(p1);
            return (CadLink.ToMm(p0), CadLink.ToMm(p1));
        }

        /// <summary>The DWGs that are section sheets: those linked into drafting views, and those beside the plans, with their section titles.</summary>
        private static List<(string path, List<(string name, double x, double y)> titles)> SectionSheets(Document doc)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var imp in CadLink.All(doc))
            {
                string p = null;
                try { p = CadLink.FilePath(doc, imp); } catch { }
                if (p == null || !File.Exists(p)) continue;
                paths.Add(p);
                try { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(p), "*.dwg")) paths.Add(f); } catch { }
            }
            var result = new List<(string, List<(string, double, double)>)>();
            foreach (var p in paths.Take(60))
            {
                try
                {
                    var titles = SectionFit.Titles(DwgFile.Read(p).Texts);
                    if (titles.Count > 0) result.Add((p, titles));
                }
                catch { /* not readable: not a sheet we can use */ }
            }
            return result;
        }

        private static (double, double) SheetRangeOf(string path, string name)
        {
            var titles = SectionFit.Titles(DwgFile.Read(path).Texts);
            var hit = titles.FirstOrDefault(t => SameSection(t.name, name));
            if (hit.name == null) return (double.MinValue, double.MaxValue);
            return SectionFit.SheetRange(titles.Select(t => (t.name, t.x)).ToList(), hit.name);
        }

        private static SectionInput SectionInputFor(Document doc, SectionSpec spec, JsonObject a, List<string> notes, Func<IEnumerable<string>, Dictionary<string, string>> roles = null)
        {
            if (spec.Path == null || !File.Exists(spec.Path)) throw new FileNotFoundException("The section sheet can't be found: " + spec.Path);
            double len = spec.A.Dist2D(spec.B);
            if (len < 1000) throw new ArgumentException("The cut line is shorter than a metre.");
            var file = DwgFile.Read(spec.Path);
            bool InSheet(Pt p) => p.X >= spec.X0 && p.X <= spec.X1 && p.Y >= spec.Y0 && p.Y <= spec.Y1;
            var inRange = file.Lines.Where(l => InSheet(l.A) && InSheet(l.B)).ToList();
            var layers = Args.Strings(a, "line_layers");
            // what Survey to Revit says each layer is: level marks, and annotation that is never geometry
            Dictionary<string, string> role = null;
            try { role = roles?.Invoke(file.Lines.Select(l => l.Layer).Concat(file.Texts.Select(t => t.Layer)).Distinct()); } catch { }
            bool Is(string layer, string r) => role != null && layer != null && role.TryGetValue(layer, out var v) && v == r;
            List<DwgLine> sec;
            if (layers.Count > 0) sec = inRange.Where(l => DwgFile.LayerMatches(l.Layer, layers)).ToList();
            else
            {
                sec = inRange.Where(l => DwgFile.LayerMatches(l.Layer, new[] { "*Profile*" })).ToList();
                if (sec.Count < 20) sec = inRange.Where(l => !DwgFile.LayerMatches(l.Layer, SectionIgnoreLayers) && !Is(l.Layer, "Annotation") && !Is(l.Layer, "LevelText")).ToList();
            }
            if (sec.Count == 0) throw new InvalidOperationException("No section lines in that part of the sheet; check sheet_x_range and line_layers.");
            var lvl = Args.Strings(a, "level_layers");
            if (lvl.Count == 0 && role != null) lvl = role.Where(kv => kv.Value == "LevelText").Select(kv => kv.Key).ToList();
            if (lvl.Count == 0) lvl = new List<string> { "*Level*" };
            double sx0 = sec.Min(l => Math.Min(l.A.X, l.B.X)), sx1 = sec.Max(l => Math.Max(l.A.X, l.B.X));
            var heights = ElevationFit.Heights(file.Texts.Where(t => DwgFile.LayerMatches(t.Layer, lvl) && InSheet(t.At)), inRange.Where(l => DwgFile.LayerMatches(l.Layer, lvl)), sx0, sx1);
            double datumMm = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;

            // the model on the plane
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            double zLo = (levels.Count > 0 ? levels.Min(l => l.Elevation) * Mm : 0) - 4000, zHi = (levels.Count > 0 ? levels.Max(l => l.Elevation) * Mm : 0) + 20000;
            var elements = CutModel(doc, spec.A, spec.B, zLo, zHi, out int failed);
            if (failed > 0) notes.Add($"{spec.Name}: {failed} elements could not be cut (unusual geometry) and are left out.");

            var along = spec.Along;
            if (along == null)
            {
                int? dir = Args.Dbl(a, "along_direction") is double dg ? (dg < 0 ? -1 : 1) : (int?)null;
                along = ElevationFit.Along(SectionFit.SheetFaces(sec, 600 / Math.Max(0.01, heights.A)), SectionFit.ModelFaces(elements), heights.A, 60, dir);
                if (along.Matched < 3) notes.Add($"{spec.Name}: the section sits on the cut poorly ({along.Note}); check the cut line and sheet range");
            }
            var input = new SectionInput { Name = spec.Name, Elements = elements, Lines = SectionFit.ToPlane(sec, heights, along, datumMm) };
            input.Sources.Add($"section: {Path.GetFileName(spec.Path)} x {(spec.X0 > -1e300 ? spec.X0.ToString("0") : "-")}..{(spec.X1 < 1e300 ? spec.X1.ToString("0") : "-")} ({sec.Count} lines)");
            input.Sources.Add($"cut: [{spec.A.X:0}, {spec.A.Y:0}, {spec.B.X:0}, {spec.B.Y:0}] from {spec.From}");
            input.Sources.Add("heights: " + heights.Note);
            input.Sources.Add("along: " + along.Note);
            return input;
        }

        // ------------------------------------------------------------------ the model cut

        /// <summary>
        /// Every wall, floor, roof, ceiling, stair and generic model the vertical plane through a..b cuts, as its outline
        /// on the plane (u along a..b, z model height, mm): each element's solids intersected with a 5 mm slice of the plane.
        /// </summary>
        internal static List<SecElement> CutModel(Document doc, Pt a, Pt b, double zLoMm, double zHiMm, out int failed)
        {
            failed = 0;
            double len = a.Dist2D(b);
            var A = new XYZ(a.X / Mm, a.Y / Mm, 0);
            var dir = new XYZ((b.X - a.X) / len, (b.Y - a.Y) / len, 0);
            var n = dir.CrossProduct(XYZ.BasisZ).Normalize();
            double half = 2.5 / Mm, ext = 1000 / Mm, zLo = zLoMm / Mm, zHi = zHiMm / Mm, L = len / Mm;
            XYZ P(double u, double z) => A + dir.Multiply(u) + XYZ.BasisZ.Multiply(z) - n.Multiply(half);
            var loop = CurveLoop.Create(new List<Curve>
            {
                Line.CreateBound(P(-ext, zLo), P(L + ext, zLo)), Line.CreateBound(P(L + ext, zLo), P(L + ext, zHi)),
                Line.CreateBound(P(L + ext, zHi), P(-ext, zHi)), Line.CreateBound(P(-ext, zHi), P(-ext, zLo)),
            });
            var slice = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, n, 2 * half);
            var corners = new[] { P(-ext, zLo), P(L + ext, zLo), P(-ext, zHi), P(L + ext, zHi) }.SelectMany(p => new[] { p, p + n.Multiply(2 * half) }).ToList();
            var outline = new Outline(new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z)), new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z)));
            var cats = new List<BuiltInCategory> { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Stairs, BuiltInCategory.OST_GenericModel };
            var found = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline)).ToElements();
            var opts = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };
            var result = new List<SecElement>();
            foreach (var el in found.Take(4000))
            {
                var solids = new List<Solid>();
                Collect(el.get_Geometry(opts), solids);
                if (el is Stairs st && solids.Count == 0)
                    foreach (var id in st.GetStairsRuns().Concat(st.GetStairsLandings())) Collect(doc.GetElement(id)?.get_Geometry(opts), solids);
                if (solids.Count == 0) continue;
                var e = new SecElement { Id = el.Id.Value, Kind = KindOf(el), Name = $"{el.Category?.Name}: {doc.GetElement(el.GetTypeId())?.Name ?? el.Name}" };
                if (el is Stairs s2) { e.Risers = s2.ActualRisersNumber; e.RiserMm = s2.ActualRiserHeight * Mm; }
                foreach (var solid in solids)
                {
                    Solid cut;
                    try { cut = BooleanOperationsUtils.ExecuteBooleanOperation(solid, slice, BooleanOperationsType.Intersect); }
                    catch { failed++; continue; }
                    if (cut == null || cut.Volume <= 1e-9) continue;
                    foreach (Face f in cut.Faces)
                    {
                        if (!(f is PlanarFace pf) || pf.FaceNormal.DotProduct(n) > -0.999) continue; // the slice's near side only
                        foreach (EdgeArray ea in pf.EdgeLoops)
                            foreach (Edge edge in ea)
                            {
                                var pts = edge.Tessellate();
                                for (int i = 0; i + 1 < pts.Count; i++)
                                {
                                    var p0 = pts[i] - A; var p1 = pts[i + 1] - A;
                                    var sg = new SecSeg(p0.DotProduct(dir) * Mm, pts[i].Z * Mm, p1.DotProduct(dir) * Mm, pts[i + 1].Z * Mm);
                                    if (sg.Length >= 3) e.Cut.Add(sg);
                                }
                            }
                    }
                }
                if (e.Cut.Count > 0) result.Add(e);
            }
            return result;
        }

        private static void Collect(GeometryElement ge, List<Solid> into)
        {
            if (ge == null) return;
            foreach (var o in ge)
            {
                if (o is Solid s && s.Volume > 1e-9) into.Add(s);
                else if (o is GeometryInstance gi) Collect(gi.GetInstanceGeometry(), into);
            }
        }

        private static string KindOf(Element el)
        {
            switch (el)
            {
                case Wall _: return "wall";
                case Floor _: return "floor";
                case RoofBase _: return "roof";
                case Ceiling _: return "ceiling";
                case Stairs _: return "stair";
            }
            string n = ((el as FamilyInstance)?.Symbol?.FamilyName ?? el.Name ?? "").ToLowerInvariant();
            return n.Contains("stair") ? "stair" : "other";
        }
    }
}
