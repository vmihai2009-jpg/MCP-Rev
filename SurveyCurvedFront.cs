// 1.41.0: curved_front. A curved or canted corner has no facade id (facade_list makes one per STRAIGHT run), so no elevation could be
// given to it and the facade tools built nothing there (Swallow Place: Elevation 4, the three-bay curved corner, was matched to a
// straight wall of similar rhythm instead; docs/FACADE-LESSONS.md 19-20). A curved front is a chain of straight runs. This tool
// lays the chain along its developed length and gives each run (on every floor) the slice of the one drawn elevation that belongs to
// it, written into elevation-index.json as ordinary entries, so every existing tool (window_look, facade_profile, facade_columns,
// facade_types, facade_relief, facade_check) then works on each run unchanged. Pieces are small against the curve (a 0.7 m pilaster
// on a 12 m radius sits within 5 mm of the arc), long bands should be built as wall sweeps along the walls (facade_profile).
// Nothing here knows a building, a layer or a facade id: the chain, the drawing and its slice are arguments.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> CurvedFrontTools()
        {
            yield return new RevitTool
            {
                Name = "curved_front",
                Write = false,
                Description = "Give one drawn elevation to a CURVED or canted front (a rounded corner, a bow, a chamfered corner) that facade_list can only see as several straight runs. " +
                              "You name the chain of runs along the curve at one floor, left to right as seen from outside (e.g. the ground floor's three bay runs); the tool lays them end to end (the gaps between them, piers, count) as a developed line, projects EVERY other run within reach_mm of that curve (upper floors, set-backs, the mansard) onto it by arc length, and writes for each run its own slice of the drawing (sheet_x_range, along_direction, facade_line_saved) into elevation-index.json. " +
                              "Then facade_detail_pass / window_look / facade_profile / facade_columns / facade_types / facade_relief / facade_check work on each run as on any facade. The drawing's left edge is put so the chain sits centred in it unless sheet_offset_mm says which sheet x the chain's start is at; the answer compares the chain's developed length with the drawing's width and warns when they differ by more than 5 %. " +
                              "dry_run (default true) only reports; false writes the index (the old file is kept as .bak-curved) and curved-fronts.json. Changes nothing in the model.",
                InputSchema = Schema(
                    ("facades", Arr("string", "The chain: facade ids from facade_list in order, left to right as seen from outside (two or more)"), true),
                    ("path", S("string", "The elevation DWG"), true),
                    ("sheet_x_range", Arr("number", "[x0, x1] on the sheet: the whole drawn elevation of the curved front"), true),
                    ("sheet_y_range", Arr("number", "[y0, y1] on the sheet (default: the elevation's whole height, 19000..64000 is typical)"), false),
                    ("name", S("string", "The drawing's title, e.g. Elevation 4"), false),
                    ("along_direction", S("integer", "1 when the drawing runs the same way as the chain (default), -1 when it runs against it"), false),
                    ("sheet_offset_mm", S("number", "The sheet x where the chain's start (s = 0) is drawn; default: the chain centred in the drawing"), false),
                    ("reach_mm", S("number", "How far from the chain a run may stand to belong to the curve (default 2500)"), false),
                    ("angle_deg", S("number", "How far a run's direction may differ from the curve's there (default 25)"), false),
                    ("pad_mm", S("number", "Extra drawing taken at each end of a run's slice (default 150)"), false),
                    ("include_gaps", S("boolean", "Also make a small facade (CG1, CG2...) of each gap between two runs of the chain, the piers where the plan's pilaster outlines stand, with a fixed position along the drawing (default true)"), false),
                    ("gap_extend_mm", S("number", "How far a pier facade reaches past each end of its gap (default 150)"), false),
                    ("dry_run", S("boolean", "Only report (default true)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    string reports = ReportsFolder(doc);
                    string facadesFile = Path.Combine(reports, "facades.json");
                    if (!File.Exists(facadesFile)) throw new ArgumentException("Run facade_list first: curved_front lays its chain out from facades.json.");
                    bool dry = Args.Bool(a, "dry_run") != false;
                    var chainIds = Args.Strings(a, "facades");
                    if (chainIds.Count < 2) throw new ArgumentException("facades needs the chain of two or more runs, left to right as seen from outside.");
                    string path = Args.Str(a, "path") ?? throw new ArgumentException("path: the elevation DWG.");
                    var xr = (a["sheet_x_range"] as JsonArray)?.Select(v => v.GetValue<double>()).ToList();
                    if (xr == null || xr.Count != 2 || xr[1] <= xr[0]) throw new ArgumentException("sheet_x_range must be [x0, x1].");
                    var yr = (a["sheet_y_range"] as JsonArray)?.Select(v => v.GetValue<double>()).ToList() ?? new List<double> { 19000, 64000 };
                    int dir = Args.Int(a, "along_direction", 1) < 0 ? -1 : 1;
                    double reach = Args.Dbl(a, "reach_mm") ?? 2500, angleTol = Args.Dbl(a, "angle_deg") ?? 25, pad = Args.Dbl(a, "pad_mm") ?? 150;
                    string title = Args.Str(a, "name") ?? "curved front";

                    var root = JsonNode.Parse(File.ReadAllText(facadesFile));
                    var runs = (root?["facades"] as JsonArray)?.OfType<JsonObject>().Select(f => new CfRun(f)).Where(r => r.Ok).ToList() ?? new List<CfRun>();
                    var byId = runs.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
                    var chain = new List<CfRun>();
                    foreach (var id in chainIds) chain.Add(byId.TryGetValue(id, out var r) ? r : throw new ArgumentException($"No facade \"{id}\" in facades.json: facade_list again (ids change when the model is rebuilt)."));

                    // the developed line: each run A->B, then a straight gap to the next run's A
                    var pts = new List<(double x, double y, double s)>();
                    double s0 = 0;
                    void Add(double x, double y) { if (pts.Count > 0) s0 += Math.Sqrt((x - pts[^1].x) * (x - pts[^1].x) + (y - pts[^1].y) * (y - pts[^1].y)); pts.Add((x, y, s0)); }
                    var gaps = new List<double>();
                    for (int i = 0; i < chain.Count; i++)
                    {
                        if (i > 0) gaps.Add(Math.Sqrt(Math.Pow(chain[i].Ax - chain[i - 1].Bx, 2) + Math.Pow(chain[i].Ay - chain[i - 1].By, 2)));
                        Add(chain[i].Ax, chain[i].Ay); Add(chain[i].Bx, chain[i].By);
                    }
                    double total = pts[^1].s;
                    if (gaps.Any(g => g > 3000)) throw new ArgumentException($"The chain has a gap of {gaps.Max() / 1000:0.0} m between runs: name the runs in order without leaving one out ({string.Join(", ", gaps.Select(g => (g / 1000).ToString("0.00") + " m"))}).");

                    // nearest point on the developed line: distance, s, and the line's direction there
                    (double d, double s, double ang) Project(double x, double y)
                    {
                        (double d, double s, double ang) best = (double.MaxValue, 0, 0);
                        for (int i = 0; i + 1 < pts.Count; i++)
                        {
                            double dx = pts[i + 1].x - pts[i].x, dy = pts[i + 1].y - pts[i].y, L2 = dx * dx + dy * dy;
                            if (L2 < 1) continue;
                            double t = Math.Max(0, Math.Min(1, ((x - pts[i].x) * dx + (y - pts[i].y) * dy) / L2));
                            double px = pts[i].x + t * dx, py = pts[i].y + t * dy, d = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                            if (d < best.d) best = (d, pts[i].s + t * Math.Sqrt(L2), Math.Atan2(dy, dx) * 180 / Math.PI);
                        }
                        return best;
                    }

                    // members: the chain's own runs and every other run along the curve (any floor)
                    var members = new List<(CfRun run, double sa, double sb, double dist, double angDiff, bool inChain)>();
                    foreach (var r in runs)
                    {
                        bool inChain = chain.Any(c => string.Equals(c.Id, r.Id, StringComparison.OrdinalIgnoreCase));
                        var pa = Project(r.Ax, r.Ay); var pb = Project(r.Bx, r.By); var pm = Project((r.Ax + r.Bx) / 2, (r.Ay + r.By) / 2);
                        double runAng = Math.Atan2(r.By - r.Ay, r.Bx - r.Ax) * 180 / Math.PI;
                        double diff = Math.Abs(((runAng - pm.ang + 540) % 360) - 180);
                        diff = Math.Min(diff, 180 - diff);
                        // faces the same way: the outward normal (runs are left to right seen from outside, so it is (dy, -dx)) within 60 degrees of the curve's there
                        var cn = ChainNormal(pts, pm.s); double rl = Math.Max(1, Math.Sqrt((r.Bx - r.Ax) * (r.Bx - r.Ax) + (r.By - r.Ay) * (r.By - r.Ay)));
                        bool outward = ((r.By - r.Ay) / rl) * cn.nx + (-(r.Bx - r.Ax) / rl) * cn.ny >= 0.5;
                        if (!inChain && (pm.d > reach || diff > angleTol || !outward)) continue;
                        double sa = Math.Min(pa.s, pb.s), sb = Math.Max(pa.s, pb.s);
                        if (sb - sa < 300) continue;
                        members.Add((r, sa, sb, pm.d, diff, inChain));
                    }

                    double drawW = xr[1] - xr[0];
                    double xs0 = Args.Dbl(a, "sheet_offset_mm") ?? (xr[0] + (drawW - total) / 2);
                    string widthNote = Math.Abs(total - drawW) / Math.Max(1, drawW) <= 0.05
                        ? $"developed length {total / 1000:0.00} m against the drawing's {drawW / 1000:0.00} m: agree"
                        : $"WARNING developed length {total / 1000:0.00} m against the drawing's {drawW / 1000:0.00} m differ by more than 5 %: check the chain's order and that no run is missing, or the drawing is not this front";
                    double SheetX(double s) => dir > 0 ? xs0 + s : xs0 + total - s;

                    var entries = new List<JsonObject>(); var table = new List<object>();
                    foreach (var m in members.OrderBy(m => m.sa).ThenBy(m => m.run.BaseZ))
                    {
                        double xa = SheetX(m.sa), xb = SheetX(m.sb);
                        double lo = Math.Max(xr[0], Math.Min(xa, xb) - pad), hi = Math.Min(xr[1], Math.Max(xa, xb) + pad);
                        if (hi - lo < 300) continue;
                        var e = new JsonObject
                        {
                            ["name"] = $"{title} (curved front, {m.run.Id})", ["facade"] = m.run.Id,
                            ["facade_line_saved"] = new JsonArray(m.run.Ax, m.run.Ay, m.run.Bx, m.run.By),
                            ["path"] = path, ["sheet_x_range"] = new JsonArray(Math.Round(lo), Math.Round(hi)),
                            ["sheet_y_range"] = new JsonArray(yr[0], yr[1]), ["along_direction"] = dir,
                            ["curved_front"] = new JsonObject { ["s_mm"] = new JsonArray(Math.Round(m.sa), Math.Round(m.sb)), ["total_mm"] = Math.Round(total), ["chain"] = new JsonArray(chainIds.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()) },
                        };
                        entries.Add(e);
                        table.Add(new { facade = m.run.Id, levels = m.run.Levels, lengthMm = Math.Round(m.run.Length), s = new[] { Math.Round(m.sa), Math.Round(m.sb) }, sheetX = new[] { Math.Round(lo), Math.Round(hi) }, distFromCurveMm = Math.Round(m.dist), angleOffDeg = Math.Round(m.angDiff, 1), inChain = m.inChain ? true : (bool?)null });
                    }
                    if (entries.Count == 0) throw new ArgumentException("No run lies along the chain: raise reach_mm or check the ids.");

                    // 1.42.0: the piers between two bays. On a curved front the plan's pilaster outlines stand in the GAPS between the runs, where no
                    // run's plan reading sees them, so each gap becomes a small facade of its own ("CG1"...): its line from one run's end to the next
                    // run's start (a little longer), its slice of the drawing, and a FIXED position along it (along_offset_mm: a pier has no window to
                    // fit). facade_columns / facade_detail_map / facade_types then work on it like on any facade.
                    var gapFacades = new List<JsonObject>();
                    if (Args.Bool(a, "include_gaps") != false)
                    {
                        double ext = Args.Dbl(a, "gap_extend_mm") ?? 150;
                        for (int i = 0; i + 1 < chain.Count; i++)
                        {
                            double gx = chain[i + 1].Ax - chain[i].Bx, gy = chain[i + 1].Ay - chain[i].By, gl = Math.Sqrt(gx * gx + gy * gy);
                            if (gl < 300 || gl > 2500) continue;
                            double ux = gx / gl, uy = gy / gl;
                            double ax = chain[i].Bx - ux * ext, ay = chain[i].By - uy * ext, bx = chain[i + 1].Ax + ux * ext, by = chain[i + 1].Ay + uy * ext;
                            double sa = pts[2 * i + 1].s - ext, sb = pts[2 * i + 2].s + ext;
                            string gid = "CG" + (i + 1);
                            double lo = Math.Max(xr[0], Math.Min(SheetX(sa), SheetX(sb)) - pad), hi = Math.Min(xr[1], Math.Max(SheetX(sa), SheetX(sb)) + pad);
                            double offset = dir > 0 ? -(xs0 + sa) : xs0 + total - sa;
                            var lv = chain[i].Levels.Union(chain[i + 1].Levels).Distinct().ToList();
                            gapFacades.Add(new JsonObject
                            {
                                ["id"] = gid, ["name"] = "curved front pier", ["line"] = new JsonArray(Math.Round(ax, 1), Math.Round(ay, 1), Math.Round(bx, 1), Math.Round(by, 1)),
                                ["outwardBearingDeg"] = chain[i].Bearing, ["lengthMm"] = Math.Round(Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay))),
                                ["baseMm"] = Math.Min(chain[i].BaseZ, chain[i + 1].BaseZ), ["topMm"] = Math.Max(chain[i].TopZ, chain[i + 1].TopZ),
                                ["levels"] = new JsonArray(lv.Select(l => (JsonNode)JsonValue.Create(l)).ToArray()), ["windows"] = 0,
                                ["walls"] = chain[i].WallIds.Count + chain[i + 1].WallIds.Count,
                                ["wallIds"] = new JsonArray(chain[i].WallIds.Concat(chain[i + 1].WallIds).Distinct().Select(w => (JsonNode)JsonValue.Create(w)).ToArray()),
                                ["main"] = true, ["curved_gap"] = true,
                            });
                            entries.Add(new JsonObject
                            {
                                ["name"] = $"{title} (curved front pier {gid})", ["facade"] = gid,
                                ["facade_line_saved"] = new JsonArray(Math.Round(ax, 1), Math.Round(ay, 1), Math.Round(bx, 1), Math.Round(by, 1)),
                                ["path"] = path, ["sheet_x_range"] = new JsonArray(Math.Round(lo), Math.Round(hi)), ["sheet_y_range"] = new JsonArray(yr[0], yr[1]),
                                ["along_direction"] = dir, ["along_offset_mm"] = Math.Round(offset),
                                ["curved_front"] = new JsonObject { ["s_mm"] = new JsonArray(Math.Round(sa), Math.Round(sb)), ["total_mm"] = Math.Round(total), ["pier"] = true },
                            });
                            table.Add(new { facade = gid, levels = lv, lengthMm = Math.Round(gl + 2 * ext), s = new[] { Math.Round(sa), Math.Round(sb) }, sheetX = new[] { Math.Round(lo), Math.Round(hi) }, distFromCurveMm = 0.0, angleOffDeg = 0.0, inChain = (bool?)null, pier = true });
                        }
                    }

                    string indexFile = Path.Combine(reports, "elevation-index.json"), written = null, groupFile = null;
                    if (!dry)
                    {
                        var idx = File.Exists(indexFile) ? JsonNode.Parse(File.ReadAllText(indexFile)) as JsonObject ?? new JsonObject() : new JsonObject();
                        if (File.Exists(indexFile)) File.Copy(indexFile, indexFile + ".bak-curved", true);
                        if (gapFacades.Count > 0)
                        {
                            File.Copy(facadesFile, facadesFile + ".bak-curved", true);
                            var fl = (root["facades"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                            var gids = new HashSet<string>(gapFacades.Select(g => g["id"].GetValue<string>()), StringComparer.OrdinalIgnoreCase);
                            fl.RemoveAll(f => f["id"] != null && gids.Contains(f["id"].GetValue<string>()));
                            fl.AddRange(gapFacades);
                            root["facades"] = new JsonArray(fl.Select(f => (JsonNode)f.DeepClone()).ToArray());
                            File.WriteAllText(facadesFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        }
                        var list = (idx["elevations"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                        var ids = new HashSet<string>(entries.Select(e => e["facade"].GetValue<string>()), StringComparer.OrdinalIgnoreCase);
                        list.RemoveAll(x => x["facade"] != null && ids.Contains(x["facade"].GetValue<string>()));
                        list.AddRange(entries);
                        idx["elevations"] = new JsonArray(list.Select(x => (JsonNode)x.DeepClone()).ToArray());
                        File.WriteAllText(indexFile, idx.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        written = indexFile;
                        groupFile = Path.Combine(reports, "curved-fronts.json");
                        var groups = File.Exists(groupFile) ? JsonNode.Parse(File.ReadAllText(groupFile)) as JsonObject ?? new JsonObject() : new JsonObject();
                        var gl = (groups["fronts"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                        gl.RemoveAll(g => g["name"]?.GetValue<string>() == title);
                        gl.Add(new JsonObject { ["name"] = title, ["chain"] = new JsonArray(chainIds.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()), ["developedMm"] = Math.Round(total), ["path"] = path, ["sheet_x_range"] = new JsonArray(xr[0], xr[1]), ["sheet_offset_mm"] = Math.Round(xs0), ["along_direction"] = dir, ["members"] = new JsonArray(entries.Select(e => (JsonNode)e["facade"].DeepClone()).ToArray()), ["when"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm") });
                        groups["fronts"] = new JsonArray(gl.Select(x => (JsonNode)x.DeepClone()).ToArray());
                        File.WriteAllText(groupFile, groups.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                    return new
                    {
                        dryRun = dry, name = title, chain = chainIds, developedMm = Math.Round(total), gapsMm = gaps.Select(g => Math.Round(g)).ToList(), drawingWidthMm = Math.Round(drawW), sheetOffsetMm = Math.Round(xs0), note = widthNote,
                        members = table, written, groupFile,
                        next = dry ? "Check the members and the developed length against the drawing (dwg_image path + box), then dry_run: false. After it: facade_detail_pass facades: [members] (style classical), then facade_correspondence image: true on each member."
                                   : "Index written (old kept as elevation-index.json.bak-curved). Run facade_detail_pass on the members, then look at each with facade_correspondence image: true and dwg_image.",
                    };
                }
            };
        }

        /// <summary>The curve's outward unit normal (model axes) at distance s along the developed line: the line runs left to right as seen from outside, so it is (dy, -dx).</summary>
        private static (double nx, double ny) ChainNormal(List<(double x, double y, double s)> pts, double s)
        {
            int k = 0;
            while (k + 2 < pts.Count && pts[k + 1].s < s) k++;
            double dx = pts[k + 1].x - pts[k].x, dy = pts[k + 1].y - pts[k].y, l = Math.Max(1e-9, Math.Sqrt(dx * dx + dy * dy));
            return (dy / l, -dx / l);
        }

        private sealed class CfRun
        {
            public string Id; public double Ax, Ay, Bx, By, Bearing, BaseZ, TopZ, Length; public List<string> Levels = new List<string>(); public List<long> WallIds = new List<long>(); public bool Ok;
            public CfRun(JsonObject f)
            {
                try
                {
                    Id = f["id"]?.GetValue<string>(); var l = (f["line"] as JsonArray)?.Select(v => v.GetValue<double>()).ToList();
                    if (Id == null || l == null || l.Count != 4) return;
                    Ax = l[0]; Ay = l[1]; Bx = l[2]; By = l[3];
                    Bearing = f["outwardBearingDeg"]?.GetValue<double>() ?? 0; BaseZ = f["baseMm"]?.GetValue<double>() ?? 0; TopZ = f["topMm"]?.GetValue<double>() ?? 0;
                    Length = f["lengthMm"]?.GetValue<double>() ?? Math.Sqrt((Bx - Ax) * (Bx - Ax) + (By - Ay) * (By - Ay));
                    Levels = (f["levels"] as JsonArray)?.Select(v => v.GetValue<string>()).ToList() ?? new List<string>();
                    WallIds = (f["wallIds"] as JsonArray)?.Select(v => v.GetValue<long>()).ToList() ?? new List<long>();
                    Ok = true;
                }
                catch { Ok = false; }
            }
        }
    }
}
