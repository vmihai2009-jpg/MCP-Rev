// Revit-free. The 1:1 score: how far a floor of the model is from its survey DWGs, against a pass gate.
// Walls: each face sampled and measured to the nearest parallel DWG face line. Thickness: the DWG face pair
// (WallFit). Openings: jambs against the plan's reveal and frame lines, sill and head against the S/H labels and
// the elevation where one is indexed. Level: the plan's spot heights. Everything in model mm.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>The pass gate. Starting values from the plan; tune them in settings.json (fidelityGate).</summary>
    public class FidelityGate
    {
        /// <summary>Share of wall face length within WallTolMm of the DWG (%).</summary>
        public double WallWithinPct { get; set; } = 95;
        public double WallTolMm { get; set; } = 10;
        /// <summary>No wall face further than this from the DWG.</summary>
        public double WallMaxMm { get; set; } = 25;
        public double ThicknessMm { get; set; } = 10;
        public double OpeningMm { get; set; } = 30;
        public double LevelMm { get; set; } = 10;
        public int MissingWalls { get; set; } = 0;
        public int MissingOpenings { get; set; } = 0;
    }

    /// <summary>A window or door in the model, for the score.</summary>
    public class FidInsert
    {
        public long Id;
        public string Category;
        public long HostWallId;
        public Pt At;
        public double W;
        public double? Sill, Head;
    }

    /// <summary>Everything one floor is measured against.</summary>
    public class FidelityInput
    {
        public string Level;
        /// <summary>The level's height in the model, and the datum (height above Ordnance Datum of model zero), mm.</summary>
        public double LevelZ, DatumMm;
        public List<FitWall> Walls = new List<FitWall>();
        public List<FidInsert> Inserts = new List<FidInsert>();
        /// <summary>The plan DWG's wall face lines, and its window and door lines.</summary>
        public List<Seg> WallFaces = new List<Seg>(), OpeningLines = new List<Seg>();
        /// <summary>The plan's text near openings ("S 0.94", "H 3.27", in m above the floor) in model mm.</summary>
        public List<DwgText> Labels = new List<DwgText>();
        /// <summary>Spot heights on the plan: where, and the value above datum (mm).</summary>
        public List<(Pt at, double aodMm)> SpotHeights = new List<(Pt, double)>();
        /// <summary>Elevation matches for this floor's inserts, and openings the elevations draw that the model lacks.</summary>
        public List<ElevMatch> Elevation = new List<ElevMatch>();
        public List<ElevMissing> ElevationMissing = new List<ElevMissing>();
        public List<string> Sources = new List<string>();
    }

    public class FidIssue
    {
        public string Kind { get; set; }
        public string Level { get; set; }
        public long? Id { get; set; }
        public double DevMm { get; set; }
        public string Against { get; set; }
        public double[] At { get; set; }
        public double[] Line { get; set; }
        public string Fix { get; set; }
        /// <summary>How far past its limit (deviation / limit); missing items rank by size.</summary>
        public double Severity { get; set; }
    }

    public class WallScore
    {
        public int Checked { get; set; }
        public int Unverified { get; set; }
        public double FaceLengthM { get; set; }
        public double WithinTolPct { get; set; }
        public double Within25Pct { get; set; }
        public double MaxMm { get; set; }
        public int ThicknessChecked { get; set; }
        public int ThicknessOver { get; set; }
        public int Missing { get; set; }
    }

    public class OpeningScore
    {
        public int Checked { get; set; }
        public int PlanChecked { get; set; }
        public int PlanOver { get; set; }
        public int LabelChecked { get; set; }
        public int LabelOver { get; set; }
        public int ElevationChecked { get; set; }
        public int ElevationOver { get; set; }
        public int Missing { get; set; }
        public int Extra { get; set; }
        public int NotChecked { get; set; }
    }

    public class LevelScore
    {
        public double ModelAodM { get; set; }
        public int SpotHeights { get; set; }
        public double? DevMm { get; set; }
        public bool Checked { get; set; }
        public string Note { get; set; }
    }

    public class FloorScore
    {
        public string Level { get; set; }
        public bool Pass { get; set; }
        public List<string> FailedOn { get; set; } = new List<string>();
        public List<string> Unverified { get; set; } = new List<string>();
        public List<string> Sources { get; set; } = new List<string>();
        public WallScore Walls { get; set; } = new WallScore();
        public OpeningScore Openings { get; set; } = new OpeningScore();
        public LevelScore LevelCheck { get; set; } = new LevelScore();
        /// <summary>0..1, for tracking progress; the gate decides, not this.</summary>
        public double Score { get; set; }
        public int IssueCount { get; set; }
    }

    public class FidelityResult
    {
        public string Model { get; set; }
        public string When { get; set; }
        public FidelityGate Gate { get; set; }
        public List<FloorScore> Floors { get; set; } = new List<FloorScore>();
        public List<FidIssue> Issues { get; set; } = new List<FidIssue>();
    }

    public static class Fidelity
    {
        public const double SampleStepMm = 250, EndSkipMm = 100, FaceSearchMm = 300, ParallelDot = 0.998;
        private static readonly Regex LabelRx = new Regex(@"^\s*([SH])\s*([0-9]+\.[0-9]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>"S 0.94" -> ('S', 940); "H 3.27" -> ('H', 3270); placeholders such as "S X.XX" -> null.</summary>
        public static (char kind, double mm)? Label(string text)
        {
            var m = LabelRx.Match(text ?? "");
            if (!m.Success) return null;
            return (char.ToUpperInvariant(m.Groups[1].Value[0]), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 1000);
        }

        /// <summary>
        /// The S and H labels that belong to a window: the nearest of each within 1.2 m, and only when no other label
        /// of that kind is within 200 mm as near (two windows' labels between them would be a guess).
        /// </summary>
        public static (double? sill, double? head) LabelsFor(Pt at, IEnumerable<DwgText> labels)
        {
            var parsed = labels.Select(t => (t.At, lab: Label(t.Value))).Where(x => x.lab != null).Select(x => (at: x.At, kind: x.lab.Value.kind, mm: x.lab.Value.mm, d: x.At.Dist2D(at))).ToList();
            double? Pick(char k)
            {
                var c = parsed.Where(x => x.kind == k && x.d <= 1200).OrderBy(x => x.d).ToList();
                if (c.Count == 0) return null;
                if (c.Count > 1 && c[1].d - c[0].d < 200 && Math.Abs(c[1].mm - c[0].mm) > 1) return null;
                return c[0].mm;
            }
            return (Pick('S'), Pick('H'));
        }

        /// <summary>One floor against the gate. Issues come back in the floor's own list (all of them, worst first).</summary>
        public static (FloorScore score, List<FidIssue> issues) Measure(FidelityInput f, FidelityGate g)
        {
            g = g ?? new FidelityGate();
            var s = new FloorScore { Level = f.Level, Sources = f.Sources.ToList() };
            var issues = new List<FidIssue>();
            var wallById = f.Walls.ToDictionary(w => w.Id);

            // ---- wall faces ----
            double total = 0, inTol = 0, in25 = 0, max = 0;
            foreach (var w in f.Walls)
            {
                double L = w.A.Dist2D(w.B);
                if (L < 300 || w.ThicknessMm < 30) continue;
                double ux = (w.B.X - w.A.X) / L, uy = (w.B.Y - w.A.Y) / L, nx = -uy, ny = ux;
                // DWG lines parallel to this wall and near it
                var near = f.WallFaces.Where(d =>
                {
                    double dl = d.Length2D; if (dl < 50) return false;
                    double dux = (d.B.X - d.A.X) / dl, duy = (d.B.Y - d.A.Y) / dl;
                    if (Math.Abs(dux * ux + duy * uy) < ParallelDot) return false;
                    double oa = (d.A.X - w.A.X) * nx + (d.A.Y - w.A.Y) * ny;
                    if (Math.Abs(oa) > w.ThicknessMm / 2 + FaceSearchMm) return false;
                    double a = (d.A.X - w.A.X) * ux + (d.A.Y - w.A.Y) * uy, b = (d.B.X - w.A.X) * ux + (d.B.Y - w.A.Y) * uy;
                    return Math.Max(a, b) >= 0 && Math.Min(a, b) <= L;
                }).ToList();

                double worstFace = 0; bool anyFace = false;
                foreach (int side in new[] { -1, 1 })
                {
                    double off = side * w.ThicknessMm / 2;
                    var devs = new List<double>();
                    int samples = 0;
                    // evenly spread samples, ends skipped; a short pier gets its middle
                    double usable = L - 2 * EndSkipMm;
                    int count = usable <= 0 ? 1 : (int)Math.Floor(usable / SampleStepMm) + 1;
                    for (int si = 0; si < count; si++)
                    {
                        double t = count == 1 ? L / 2 : EndSkipMm + usable * si / (count - 1);
                        samples++;
                        double px = w.A.X + ux * t + nx * off, py = w.A.Y + uy * t + ny * off;
                        double best = double.MaxValue;
                        foreach (var d in near)
                        {
                            double dl = d.Length2D, dux = (d.B.X - d.A.X) / dl, duy = (d.B.Y - d.A.Y) / dl;
                            double along = (px - d.A.X) * dux + (py - d.A.Y) * duy;
                            if (along < -20 || along > dl + 20) continue;
                            double perp = Math.Abs((px - d.A.X) * -duy + (py - d.A.Y) * dux);
                            if (perp < best) best = perp;
                        }
                        if (best <= FaceSearchMm) devs.Add(best);
                    }
                    if (samples == 0 || devs.Count < Math.Max(1, samples * 0.3)) continue; // face not drawn (openings, a party wall): unverified
                    anyFace = true;
                    double seg = L * devs.Count / samples;
                    total += seg;
                    inTol += seg * devs.Count(d => d <= g.WallTolMm) / devs.Count;
                    in25 += seg * devs.Count(d => d <= 25) / devs.Count;
                    devs.Sort();
                    double median = devs[devs.Count / 2];
                    worstFace = Math.Max(worstFace, median);
                }
                if (!anyFace) { s.Walls.Unverified++; continue; }
                s.Walls.Checked++;
                max = Math.Max(max, worstFace);
                if (worstFace > g.WallTolMm)
                    issues.Add(new FidIssue
                    {
                        Kind = "wall_face", Level = f.Level, Id = w.Id, DevMm = Math.Round(worstFace),
                        Against = "plan DWG wall face lines", At = Mid(w.A, w.B),
                        Fix = $"dwg_wall_fit level \"{f.Level}\", then dwg_wall_fit_apply ids [{w.Id}]",
                        Severity = worstFace / g.WallTolMm,
                    });
            }
            s.Walls.FaceLengthM = Math.Round(total / 1000, 1);
            s.Walls.WithinTolPct = total > 0 ? Math.Round(100 * inTol / total, 1) : 0;
            s.Walls.Within25Pct = total > 0 ? Math.Round(100 * in25 / total, 1) : 0;
            s.Walls.MaxMm = Math.Round(max);

            // ---- thickness: the DWG face pair each wall sits between ----
            var pairs = WallFit.Fit(f.Walls, f.WallFaces, new FitOptions { MinChangeMm = -1, RoundThicknessMm = 1 });
            // only pairs that clearly belong to the wall (both faces along most of it, a modest change): a weak pair
            // is usually a pier, a reveal or the next wall's face, and says nothing about this wall's thickness
            pairs = pairs.Where(p => p.Confidence == "moderate").ToList();
            s.Walls.ThicknessChecked = pairs.Count;
            foreach (var p in pairs)
            {
                double dT = Math.Abs(p.NewThicknessMm - p.ThicknessMm);
                if (dT <= g.ThicknessMm) continue;
                s.Walls.ThicknessOver++;
                issues.Add(new FidIssue
                {
                    Kind = "wall_thickness", Level = f.Level, Id = p.Id, DevMm = Math.Round(p.NewThicknessMm - p.ThicknessMm),
                    Against = $"DWG face pair {p.NewThicknessMm:0} mm (model {p.ThicknessMm:0} mm)",
                    At = wallById.TryGetValue(p.Id, out var w) ? Mid(w.A, w.B) : null,
                    Fix = $"dwg_wall_fit_apply ids [{p.Id}]", Severity = dT / g.ThicknessMm,
                });
            }

            // ---- missing walls ----
            var missing = WallFit.Missing(f.Walls, f.WallFaces, 60, 1300, 600);
            s.Walls.Missing = missing.Count;
            foreach (var m in missing)
                issues.Add(new FidIssue
                {
                    Kind = "wall_missing", Level = f.Level, Line = m.Line, DevMm = m.LengthMm,
                    Against = $"DWG face pair {m.ThicknessMm:0} mm, {m.LengthMm:0} mm long", At = new[] { Math.Round((m.Line[0] + m.Line[2]) / 2), Math.Round((m.Line[1] + m.Line[3]) / 2) },
                    Fix = $"create_walls level \"{f.Level}\" line {Fmt(m.Line)} thickness {m.ThicknessMm:0} (check with cad_lines first)",
                    Severity = 2 + m.LengthMm / 1000,
                });

            // ---- openings ----
            var labels = f.Labels.Select(t => (t, lab: Label(t.Value))).Where(x => x.lab != null).Select(x => (at: x.t.At, kind: x.lab.Value.kind, mm: x.lab.Value.mm)).ToList();
            var jambLines = f.WallFaces.Concat(f.OpeningLines).ToList();
            var elevById = f.Elevation.GroupBy(e => e.Insert.Id).ToDictionary(x => x.Key, x => x.First());
            foreach (var ins in f.Inserts)
            {
                s.Openings.Checked++;
                double worst = 0; string worstWhat = null; bool anyEvidence = false;
                void Take(double dev, string what) { anyEvidence = true; if (Math.Abs(dev) > Math.Abs(worst)) { worst = dev; worstWhat = what; } }

                // plan: the jambs either side, on the host wall
                if (wallById.TryGetValue(ins.HostWallId, out var host))
                {
                    var j = PlanJambs(host, ins, jambLines);
                    if (j != null)
                    {
                        s.Openings.PlanChecked++;
                        double dev = Math.Max(Math.Abs(j.Value.left), Math.Abs(j.Value.right));
                        if (dev > g.OpeningMm) s.Openings.PlanOver++;
                        Take(dev, $"plan jambs (left {j.Value.left:0}, right {j.Value.right:0} mm)");
                    }
                }
                // labels (windows only: a door's sill is the floor): the nearest S and H within 1.2 m
                bool isWindow = ins.Category == null || ins.Category.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0;
                double? Near(char k) => !isWindow ? null : labels.Where(l => l.kind == k && l.at.Dist2D(ins.At) <= 1200).OrderBy(l => l.at.Dist2D(ins.At)).Select(l => (double?)l.mm).FirstOrDefault();
                double? sl = Near('S'), hl = Near('H');
                bool labelChecked = false, labelOver = false;
                if (ins.Sill is double sill && sl is double slv)
                { labelChecked = true; double d = sill - slv; labelOver |= Math.Abs(d) > g.OpeningMm; Take(d, $"sill label S {slv / 1000:0.00}"); }
                if (ins.Head is double head && hl is double hlv)
                { labelChecked = true; double d = head - hlv; labelOver |= Math.Abs(d) > g.OpeningMm; Take(d, $"head label H {hlv / 1000:0.00}"); }
                if (labelChecked) { s.Openings.LabelChecked++; if (labelOver) s.Openings.LabelOver++; }

                // elevation: sill and head of windows (frame lines, as the labels measure them). Widths and jamb
                // positions come from the plan, which cuts through the opening; an elevation's reveal lines can
                // be the surround's and read wide.
                if (isWindow && elevById.TryGetValue(ins.Id, out var e) && (ins.Sill != null || ins.Head != null))
                {
                    s.Openings.ElevationChecked++;
                    double ds = ins.Sill is double s2 ? s2 - e.Sill : 0, dh = ins.Head is double h2 ? h2 - e.Head : 0;
                    if (Math.Abs(ds) > g.OpeningMm || Math.Abs(dh) > g.OpeningMm) s.Openings.ElevationOver++;
                    if (ins.Sill != null) Take(ds, $"elevation sill {e.Sill:0}");
                    if (ins.Head != null) Take(dh, $"elevation head {e.Head:0}");
                }

                if (!anyEvidence) { s.Openings.NotChecked++; continue; }
                if (Math.Abs(worst) > g.OpeningMm)
                    issues.Add(new FidIssue
                    {
                        Kind = "opening", Level = f.Level, Id = ins.Id, DevMm = Math.Round(worst), Against = worstWhat, At = new[] { Math.Round(ins.At.X), Math.Round(ins.At.Y) },
                        Fix = $"set_insert id {ins.Id} (width / sill / head / move) to match {worstWhat}", Severity = Math.Abs(worst) / g.OpeningMm,
                    });
            }
            s.Openings.Missing = f.ElevationMissing.Count;
            foreach (var m in f.ElevationMissing)
                issues.Add(new FidIssue
                {
                    Kind = "opening_missing", Level = f.Level, DevMm = Math.Round(m.W), Against = $"elevation: {m.W:0} wide, sill {m.Sill:0}, head {m.Head:0}",
                    Fix = "place_inserts on the facade wall at that place (dwg_elevation gives the point)", Severity = 2 + m.W / 1000,
                });

            // ---- level ----
            double aod = f.LevelZ + f.DatumMm;
            s.LevelCheck.ModelAodM = Math.Round(aod / 1000, 3);
            var spots = f.SpotHeights.Where(x => Math.Abs(x.aodMm - aod) <= 600).Select(x => x.aodMm).OrderBy(v => v).ToList();
            s.LevelCheck.SpotHeights = spots.Count;
            if (spots.Count > 0)
            {
                // the cluster nearest the level: values within 30 mm of the commonest
                double mode = spots.OrderByDescending(v => spots.Count(o => Math.Abs(o - v) <= 30)).ThenBy(v => Math.Abs(v - aod)).First();
                var cluster = spots.Where(v => Math.Abs(v - mode) <= 30).ToList();
                double dev = cluster[cluster.Count / 2] - aod;
                s.LevelCheck.Checked = true;
                s.LevelCheck.DevMm = Math.Round(dev);
                s.LevelCheck.Note = $"{cluster.Count} of {spots.Count} spot heights within 600 mm agree on {cluster[cluster.Count / 2] / 1000:0.000} m";
                if (Math.Abs(dev) > g.LevelMm)
                    issues.Add(new FidIssue
                    {
                        Kind = "level", Level = f.Level, DevMm = Math.Round(-dev), Against = s.LevelCheck.Note,
                        Fix = $"move level \"{f.Level}\" by {dev:+0;-0} mm (set_parameters Elevation), then re-check sills", Severity = 3 + Math.Abs(dev) / g.LevelMm,
                    });
            }
            else s.LevelCheck.Note = "no spot heights within 600 mm of the level on the plan";

            // ---- gate ----
            if (s.Walls.Checked == 0) s.Unverified.Add("walls");
            else
            {
                if (s.Walls.WithinTolPct < g.WallWithinPct || s.Walls.MaxMm > g.WallMaxMm) s.FailedOn.Add("walls");
            }
            if (s.Walls.ThicknessOver > 0) s.FailedOn.Add("thickness");
            if (s.Walls.Missing > g.MissingWalls) s.FailedOn.Add("missing walls");
            if (s.Openings.PlanOver + s.Openings.LabelOver + s.Openings.ElevationOver > 0) s.FailedOn.Add("openings");
            if (s.Openings.Missing > g.MissingOpenings) s.FailedOn.Add("missing openings");
            if (s.Openings.Checked > 0 && s.Openings.NotChecked == s.Openings.Checked) s.Unverified.Add("openings");
            if (!s.LevelCheck.Checked) s.Unverified.Add("level");
            else if (Math.Abs(s.LevelCheck.DevMm ?? 0) > g.LevelMm) s.FailedOn.Add("level");
            s.Pass = s.FailedOn.Count == 0;

            int openingsOver = issues.Count(i => i.Kind == "opening");
            int openingsMeasured = Math.Max(1, s.Openings.Checked - s.Openings.NotChecked);
            double wallPart = s.Walls.WithinTolPct / 100;
            double thickPart = s.Walls.ThicknessChecked == 0 ? 1 : 1 - (double)s.Walls.ThicknessOver / s.Walls.ThicknessChecked;
            double openPart = s.Openings.Checked == 0 ? 1 : Math.Max(0, 1 - (double)(openingsOver + s.Openings.Missing) / (openingsMeasured + s.Openings.Missing));
            double levelPart = !s.LevelCheck.Checked ? 1 : Math.Abs(s.LevelCheck.DevMm ?? 0) <= g.LevelMm ? 1 : 0;
            double missPart = s.Walls.Checked == 0 ? 1 : Math.Max(0, 1 - (double)s.Walls.Missing / (s.Walls.Checked + s.Walls.Missing));
            s.Score = Math.Round(0.35 * wallPart + 0.1 * thickPart + 0.15 * missPart + 0.3 * openPart + 0.1 * levelPart, 3);

            issues = issues.OrderByDescending(i => i.Severity).ToList();
            s.IssueCount = issues.Count;
            return (s, issues);
        }

        /// <summary>
        /// The plan's jambs for an insert on its host wall: lines across the wall (wall-layer reveal returns, or the
        /// window/door frame ends) near each end of the insert. Signed offsets (DWG minus model) of the nearest each
        /// side, or null when neither side has one within 300 mm.
        /// </summary>
        public static (double left, double right)? PlanJambs(FitWall host, FidInsert ins, IList<Seg> lines)
        {
            double L = host.A.Dist2D(host.B);
            if (L < 1) return null;
            double ux = (host.B.X - host.A.X) / L, uy = (host.B.Y - host.A.Y) / L, nx = -uy, ny = ux;
            double u = (ins.At.X - host.A.X) * ux + (ins.At.Y - host.A.Y) * uy;
            double half = ins.W / 2, reach = host.ThicknessMm / 2 + 250;
            var cands = new List<double>();
            foreach (var d in lines)
            {
                double dl = d.Length2D;
                if (dl < 30 || dl > host.ThicknessMm + 400) continue;
                double dux = (d.B.X - d.A.X) / dl, duy = (d.B.Y - d.A.Y) / dl;
                if (Math.Abs(dux * ux + duy * uy) > 0.1) continue; // across the wall
                double mx = (d.A.X + d.B.X) / 2, my = (d.A.Y + d.B.Y) / 2;
                if (Math.Abs((mx - host.A.X) * nx + (my - host.A.Y) * ny) > reach) continue;
                double c = (mx - host.A.X) * ux + (my - host.A.Y) * uy;
                if (c < u - half - 300 || c > u + half + 300) continue;
                cands.Add(c);
            }
            if (cands.Count == 0) return null;
            double l = cands.OrderBy(c => Math.Abs(c - (u - half))).First() - (u - half);
            double r = cands.OrderBy(c => Math.Abs(c - (u + half))).First() - (u + half);
            if (Math.Abs(l) > 300 || Math.Abs(r) > 300) return null;
            return (l, r);
        }

        private static double[] Mid(Pt a, Pt b) => new[] { Math.Round((a.X + b.X) / 2), Math.Round((a.Y + b.Y) / 2) };
        private static string Fmt(double[] v) => "[" + string.Join(", ", v.Select(x => x.ToString("0", CultureInfo.InvariantCulture))) + "]";

        /// <summary>A plain page of the result: one row per floor, then the issues.</summary>
        public static string Html(FidelityResult r)
        {
            string E(object o) => System.Net.WebUtility.HtmlEncode(Convert.ToString(o, CultureInfo.InvariantCulture) ?? "");
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Survey fidelity</title><style>")
              .Append(":root{--bg:#fff;--fg:#1b1b1b;--mut:#666;--line:#ddd;--pass:#1a7f37;--fail:#c62828}")
              .Append("@media (prefers-color-scheme:dark){:root{--bg:#141414;--fg:#eee;--mut:#aaa;--line:#333;--pass:#4caf50;--fail:#ef5350}}")
              .Append("body{background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,sans-serif;margin:24px 16px}h1{font-size:20px}h2{font-size:16px;margin-top:28px}")
              .Append("table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}th,td{border-bottom:1px solid var(--line);padding:5px 8px;text-align:left;vertical-align:top}")
              .Append("th{color:var(--mut);font-weight:600}.pass{color:var(--pass);font-weight:600}.fail{color:var(--fail);font-weight:600}.mut{color:var(--mut)}.wrap{overflow-x:auto}</style></head><body>");
            sb.Append($"<h1>Survey fidelity: {E(r.Model)}</h1><p class=\"mut\">{E(r.When)} · gate: walls {E(r.Gate.WallWithinPct)}% within {E(r.Gate.WallTolMm)} mm, none over {E(r.Gate.WallMaxMm)} mm; thickness {E(r.Gate.ThicknessMm)} mm; openings {E(r.Gate.OpeningMm)} mm; levels {E(r.Gate.LevelMm)} mm; {E(r.Gate.MissingWalls)} missing walls</p>");
            sb.Append("<div class=\"wrap\"><table><tr><th>Floor</th><th>Result</th><th>Score</th><th>Walls within tol</th><th>Max mm</th><th>Thickness out</th><th>Missing walls</th><th>Openings out (plan / labels / elev)</th><th>Missing openings</th><th>Level dev mm</th><th>Unverified</th></tr>");
            foreach (var f in r.Floors)
            {
                sb.Append("<tr><td>").Append(E(f.Level)).Append("</td><td class=\"").Append(f.Pass ? "pass\">pass" : "fail\">fail: " + E(string.Join(", ", f.FailedOn))).Append("</td>")
                  .Append($"<td>{E(f.Score)}</td><td>{E(f.Walls.WithinTolPct)}% of {E(f.Walls.FaceLengthM)} m ({E(f.Walls.Checked)} walls)</td><td>{E(f.Walls.MaxMm)}</td>")
                  .Append($"<td>{E(f.Walls.ThicknessOver)} of {E(f.Walls.ThicknessChecked)}</td><td>{E(f.Walls.Missing)}</td>")
                  .Append($"<td>{E(f.Openings.PlanOver)}/{E(f.Openings.PlanChecked)} · {E(f.Openings.LabelOver)}/{E(f.Openings.LabelChecked)} · {E(f.Openings.ElevationOver)}/{E(f.Openings.ElevationChecked)}</td>")
                  .Append($"<td>{E(f.Openings.Missing)}</td><td>{(f.LevelCheck.Checked ? E(f.LevelCheck.DevMm) : "<span class=\"mut\">–</span>")}</td><td class=\"mut\">{E(string.Join(", ", f.Unverified))}</td></tr>");
            }
            sb.Append("</table></div><h2>Issues, worst first</h2><div class=\"wrap\"><table><tr><th>Floor</th><th>Kind</th><th>Element</th><th>Deviation mm</th><th>Measured against</th><th>Where (model mm)</th><th>Fix</th></tr>");
            foreach (var i in r.Issues)
                sb.Append($"<tr><td>{E(i.Level)}</td><td>{E(i.Kind)}</td><td>{E(i.Id)}</td><td>{E(i.DevMm)}</td><td>{E(i.Against)}</td><td class=\"mut\">{(i.At == null ? "" : E(string.Join(", ", i.At)))}</td><td class=\"mut\">{E(i.Fix)}</td></tr>");
            sb.Append("</table></div><p class=\"mut\">Wall faces are sampled every 250 mm and measured to the nearest parallel DWG face line; faces the DWG does not draw are left unverified. Opening sills and heads are measured as the survey's S/H labels and the elevation frame lines give them; widths against the masonry opening where the elevation draws it.</p></body></html>");
            return sb.ToString();
        }
    }
}
