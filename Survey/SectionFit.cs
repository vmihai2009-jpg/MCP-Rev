// Revit-free. The 3D part of the 1:1 score: one survey section against the model cut on the same plane.
// Coordinates are in the section plane, model mm: u along the cut line (from its start), z model height.
// The model side is each element's outline where the plane cuts it (walls, floors, roofs, ceilings, stairs,
// generic models); the survey side is the section sheet's lines, put on the plane by its level marks (heights)
// and by matching its cut wall faces to the model's (position along the cut).
// Measured: every model cut edge against the nearest parallel section line (the profile), and per element the
// heights a person checks on a section: slab top and thickness, wall and parapet tops, roof ridge,
// eaves and pitch, stair riser count and height.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>A line in the section plane, model mm (u along the cut, z height).</summary>
    public class SecSeg
    {
        public double U0, Z0, U1, Z1;
        public string Layer;
        public SecSeg() { }
        public SecSeg(double u0, double z0, double u1, double z1, string layer = null) { U0 = u0; Z0 = z0; U1 = u1; Z1 = z1; Layer = layer; }
        public double Length => Math.Sqrt((U1 - U0) * (U1 - U0) + (Z1 - Z0) * (Z1 - Z0));
        public bool IsVertical(double tolDeg = 3) => Math.Abs(U1 - U0) <= Length * Math.Sin(tolDeg * Math.PI / 180);
        public bool IsHorizontal(double tolDeg = 3) => Math.Abs(Z1 - Z0) <= Length * Math.Sin(tolDeg * Math.PI / 180);
        public double UMin => Math.Min(U0, U1);
        public double UMax => Math.Max(U0, U1);
        public double ZMin => Math.Min(Z0, Z1);
        public double ZMax => Math.Max(Z0, Z1);
        /// <summary>Pitch from horizontal, degrees (0..90).</summary>
        public double PitchDeg => Math.Atan2(Math.Abs(Z1 - Z0), Math.Abs(U1 - U0)) * 180 / Math.PI;
        /// <summary>The line's height at u, or null when it does not pass over u (vertical lines never do).</summary>
        public double? ZAt(double u)
        {
            if (u < UMin - 1 || u > UMax + 1 || Math.Abs(U1 - U0) < 1) return null;
            return Z0 + (Z1 - Z0) * (u - U0) / (U1 - U0);
        }
    }

    /// <summary>One model element where the section plane cuts it.</summary>
    public class SecElement
    {
        public long Id;
        /// <summary>wall, floor, roof, ceiling, stair or other.</summary>
        public string Kind;
        public string Name;
        public List<SecSeg> Cut = new List<SecSeg>();
        /// <summary>Stairs: the model's riser count and riser height (mm).</summary>
        public int? Risers;
        public double? RiserMm;
        public double UMin => Cut.Count == 0 ? 0 : Cut.Min(c => c.UMin);
        public double UMax => Cut.Count == 0 ? 0 : Cut.Max(c => c.UMax);
        public double ZMin => Cut.Count == 0 ? 0 : Cut.Min(c => c.ZMin);
        public double ZMax => Cut.Count == 0 ? 0 : Cut.Max(c => c.ZMax);
    }

    public class SectionInput
    {
        public string Name;
        public List<SecElement> Elements = new List<SecElement>();
        /// <summary>The section sheet's lines on the plane.</summary>
        public List<SecSeg> Lines = new List<SecSeg>();
        public List<string> Sources = new List<string>();
    }

    public class SecElementScore
    {
        public long Id { get; set; }
        public string Kind { get; set; }
        public string Name { get; set; }
        public double CutLengthM { get; set; }
        public double DrawnPct { get; set; }
        public double WithinTolPct { get; set; }
        public double MedianMm { get; set; }
        public bool Verified { get; set; }
        /// <summary>Signed: survey minus model (positive = the survey has it higher / thicker).</summary>
        public double? TopDevMm { get; set; }
        public double? BottomDevMm { get; set; }
        public double? ThicknessDevMm { get; set; }
        public double? RidgeDevMm { get; set; }
        public double? EavesDevMm { get; set; }
        public double? PitchDevDeg { get; set; }
        public double? SurveyPitchDeg { get; set; }
        public int? SurveyRisers { get; set; }
        public double? SurveyRiserMm { get; set; }
        public bool Parapet { get; set; }
        /// <summary>The survey's value the deviation was measured to (top, soffit or ridge), model mm.</summary>
        public double? SurveyTopZ { get; set; }
        public double? SurveyBottomZ { get; set; }
    }

    public class SectionScore
    {
        public string Section { get; set; }
        public bool Pass { get; set; }
        public List<string> FailedOn { get; set; } = new List<string>();
        public List<string> Unverified { get; set; } = new List<string>();
        public List<string> Sources { get; set; } = new List<string>();
        public int Elements { get; set; }
        public int Verified { get; set; }
        public double CutLengthM { get; set; }
        public double WithinTolPct { get; set; }
        public int TopsOver { get; set; }
        public int SlabsOver { get; set; }
        public int RoofsOver { get; set; }
        public int StairsOver { get; set; }
        /// <summary>Long section lines no model edge follows (a missing slab, roof slope or parapet; or detail beyond the cut).</summary>
        public int Unmatched { get; set; }
        public double UnmatchedM { get; set; }
        public double Score { get; set; }
        public int IssueCount { get; set; }
        public List<SecElementScore> Items { get; set; } = new List<SecElementScore>();
    }

    public static class SectionFit
    {
        public const double SampleStepMm = 100, EndSkipMm = 40, SearchMm = 300, ParallelDeg = 12;
        private const double HeightSearchMm = 300;

        /// <summary>
        /// Sheet lines -> section plane. heights maps sheet y to height above datum; along maps sheet x (times the
        /// height scale, as dwg_elevation does) to distance along the cut.
        /// </summary>
        public static List<SecSeg> ToPlane(IEnumerable<DwgLine> sheet, HeightFit heights, AlongFit along, double datumMm)
        {
            return sheet.Select(l => new SecSeg(along.Along(l.A.X * heights.A), heights.Height(l.A.Y) - datumMm,
                                                along.Along(l.B.X * heights.A), heights.Height(l.B.Y) - datumMm, l.Layer))
                        .Where(s => s.Length >= 1).ToList();
        }

        /// <summary>
        /// The u positions of the model's cut wall faces (vertical edges of wall cuts at least 600 mm tall), merged
        /// within 20 mm: what the section's own vertical wall lines are matched to.
        /// </summary>
        public static List<double> ModelFaces(IEnumerable<SecElement> elements)
        {
            var us = elements.Where(e => e.Kind == "wall").SelectMany(e => e.Cut).Where(c => c.IsVertical() && c.Length >= 600)
                             .Select(c => (c.U0 + c.U1) / 2).OrderBy(u => u).ToList();
            return Merge(us, 20);
        }

        /// <summary>The section sheet's tall vertical lines (sheet x), merged within 20 mm: cut wall faces and the building's edges.</summary>
        public static List<double> SheetFaces(IEnumerable<DwgLine> lines, double minHeightMm = 600)
        {
            var xs = lines.Where(l => Math.Abs(l.A.X - l.B.X) < 2 && Math.Abs(l.A.Y - l.B.Y) >= minHeightMm).Select(l => (l.A.X + l.B.X) / 2).OrderBy(x => x).ToList();
            return Merge(xs, 20);
        }

        private static List<double> Merge(List<double> sorted, double tol)
        {
            var merged = new List<double>();
            foreach (var x in sorted)
                if (merged.Count == 0 || x - merged[merged.Count - 1] > tol) merged.Add(x);
            return merged;
        }

        /// <summary>Distance from (u, z) to the nearest section line running within ParallelDeg of direction (du, dz), or null past SearchMm.</summary>
        public static double? Nearest(double u, double z, double du, double dz, IList<SecSeg> lines, double searchMm = SearchMm)
        {
            double dl = Math.Sqrt(du * du + dz * dz);
            if (dl < 1e-9) return null;
            du /= dl; dz /= dl;
            double cosTol = Math.Cos(ParallelDeg * Math.PI / 180);
            double best = double.MaxValue;
            foreach (var s in lines)
            {
                double L = s.Length;
                if (L < 20) continue;
                double su = (s.U1 - s.U0) / L, sz = (s.Z1 - s.Z0) / L;
                if (Math.Abs(su * du + sz * dz) < cosTol) continue;
                double along = (u - s.U0) * su + (z - s.Z0) * sz;
                if (along < -20 || along > L + 20) continue;
                double perp = Math.Abs((u - s.U0) * -sz + (z - s.Z0) * su);
                if (perp < best) best = perp;
            }
            return best <= searchMm ? best : (double?)null;
        }

        /// <summary>The section against the model: per element and in total, issues worst first.</summary>
        public static (SectionScore score, List<FidIssue> issues) Measure(SectionInput input, FidelityGate g)
        {
            g = g ?? new FidelityGate();
            var s = new SectionScore { Section = input.Name, Sources = input.Sources.ToList(), Elements = input.Elements.Count };
            var issues = new List<FidIssue>();
            var lines = input.Lines.Where(l => l.Length >= 20).ToList();
            double total = 0, inTol = 0;

            // the tops of floors and roofs, for telling a parapet from an ordinary wall
            var decks = input.Elements.Where(e => e.Kind == "floor" || e.Kind == "roof").ToList();

            foreach (var e in input.Elements)
            {
                if (e.Cut.Count == 0) continue;
                var item = new SecElementScore { Id = e.Id, Kind = e.Kind, Name = e.Name };
                // ---- profile ----
                double len = 0, drawn = 0, ok = 0; var devs = new List<double>();
                foreach (var c in e.Cut)
                {
                    double L = c.Length;
                    if (L < 30) continue;
                    len += L;
                    double usable = L - 2 * EndSkipMm;
                    int n = usable <= 0 ? 1 : (int)Math.Floor(usable / SampleStepMm) + 1;
                    int hit = 0, good = 0;
                    for (int i = 0; i < n; i++)
                    {
                        double t = n == 1 ? L / 2 : EndSkipMm + usable * i / (n - 1);
                        double u = c.U0 + (c.U1 - c.U0) * t / L, z = c.Z0 + (c.Z1 - c.Z0) * t / L;
                        var d = Nearest(u, z, c.U1 - c.U0, c.Z1 - c.Z0, lines);
                        if (d == null) continue;
                        hit++; devs.Add(d.Value);
                        if (d.Value <= g.SectionTolMm) good++;
                    }
                    drawn += L * hit / n;
                    ok += L * good / n;
                }
                item.CutLengthM = Math.Round(len / 1000, 2);
                item.DrawnPct = len > 0 ? Math.Round(100 * drawn / len, 1) : 0;
                item.Verified = len > 0 && drawn >= len * 0.3 && devs.Count > 0;
                if (item.Verified)
                {
                    devs.Sort();
                    item.MedianMm = Math.Round(devs[devs.Count / 2], 1);
                    item.WithinTolPct = Math.Round(100 * ok / drawn, 1);
                    total += drawn; inTol += ok;
                    s.Verified++;
                    if (item.WithinTolPct < g.SectionWithinPct && item.MedianMm > g.SectionTolMm)
                        issues.Add(new FidIssue
                        {
                            Kind = "section_profile", Level = input.Name, Id = e.Id, DevMm = item.MedianMm,
                            Against = $"section lines: {item.WithinTolPct:0}% of the {e.Kind}'s cut within {g.SectionTolMm:0} mm",
                            At = new[] { Math.Round((e.UMin + e.UMax) / 2), Math.Round((e.ZMin + e.ZMax) / 2) },
                            Fix = $"dwg_section with image true to see it; the {e.Kind} is drawn elsewhere on the section",
                            Severity = item.MedianMm / g.SectionTolMm,
                        });
                }

                // ---- per kind ----
                switch (e.Kind)
                {
                    case "floor":
                    case "ceiling":
                        // a ceiling's soffit is scored with the rest of the profile; survey_ceilings measures and moves ceilings
                        Slab(e, lines, g, item, issues, input.Name, s);
                        break;
                    case "wall":
                        Wall(e, lines, decks, g, item, issues, input.Name, s);
                        break;
                    case "roof":
                        Roof(e, lines, g, item, issues, input.Name, s);
                        break;
                    case "stair":
                        Stair(e, lines, g, item, issues, input.Name, s);
                        break;
                }
                s.Items.Add(item);
            }
            s.CutLengthM = Math.Round(total / 1000, 1);
            s.WithinTolPct = total > 0 ? Math.Round(100 * inTol / total, 1) : 0;

            // ---- section lines nothing in the model follows ----
            var edges = input.Elements.SelectMany(e => e.Cut).Where(c => c.Length >= 30).ToList();
            if (edges.Count > 0)
            {
                double u0 = edges.Min(c => c.UMin) - 300, u1 = edges.Max(c => c.UMax) + 300, z1 = edges.Max(c => c.ZMax) + 3000;
                foreach (var l in lines.Where(l => l.Length >= 1000 && !l.IsVertical() && l.UMin >= u0 && l.UMax <= u1 && l.ZMax <= z1))
                {
                    double mu = (l.U0 + l.U1) / 2, mz = (l.Z0 + l.Z1) / 2;
                    // the line's middle and quarter points: none near a model edge
                    int near = 0;
                    foreach (double t in new[] { 0.25, 0.5, 0.75 })
                    {
                        double u = l.U0 + (l.U1 - l.U0) * t, z = l.Z0 + (l.Z1 - l.Z0) * t;
                        if (Nearest(u, z, l.U1 - l.U0, l.Z1 - l.Z0, edges, 100) != null) near++;
                    }
                    if (near > 0) continue;
                    s.Unmatched++; s.UnmatchedM += l.Length / 1000;
                    issues.Add(new FidIssue
                    {
                        Kind = "section_unmatched", Level = input.Name, DevMm = Math.Round(l.Length),
                        Against = $"a {l.Length / 1000:0.0} m {(l.IsHorizontal() ? "level" : $"{l.PitchDeg:0}° sloping")} line on {l.Layer ?? "the section"} that no model element follows",
                        At = new[] { Math.Round(mu), Math.Round(mz) }, Line = new[] { Math.Round(l.U0), Math.Round(l.Z0), Math.Round(l.U1), Math.Round(l.Z1) },
                        Fix = l.IsHorizontal() ? "a slab, ceiling or roof the model lacks (create_floor / create_roof), or detail seen beyond the cut: check with image true"
                                               : $"a {l.PitchDeg:0}° roof slope the model lacks (create_roof with slopes_deg), or detail beyond the cut: check with image true",
                        Severity = 0.5 + l.Length / 5000,
                    });
                }
                s.UnmatchedM = Math.Round(s.UnmatchedM, 1);
            }

            // ---- gate ----
            if (s.Verified == 0) s.Unverified.Add("profile");
            else if (s.WithinTolPct < g.SectionWithinPct) s.FailedOn.Add("profile");
            if (s.TopsOver > 0) s.FailedOn.Add("wall tops");
            if (s.SlabsOver > 0) s.FailedOn.Add("slabs");
            if (s.RoofsOver > 0) s.FailedOn.Add("roofs");
            if (s.StairsOver > 0) s.FailedOn.Add("stairs");
            s.Pass = s.FailedOn.Count == 0;
            int measured = Math.Max(1, s.Items.Count(i => i.TopDevMm != null || i.RidgeDevMm != null || i.SurveyRisers != null || i.ThicknessDevMm != null));
            int over = s.TopsOver + s.SlabsOver + s.RoofsOver + s.StairsOver;
            s.Score = Math.Round(0.6 * (s.Verified == 0 ? 1 : s.WithinTolPct / 100) + 0.4 * Math.Max(0, 1 - (double)over / measured), 3);
            issues = issues.OrderByDescending(i => i.Severity).ToList();
            s.IssueCount = issues.Count;
            return (s, issues);
        }

        // the section's level line nearest z over the span u0..u1 (overlapping at least a third of it, or 300 mm)
        private static double? LevelLine(IList<SecSeg> lines, double u0, double u1, double z, double search, Func<double, bool> accept = null)
        {
            double need = Math.Min(300, (u1 - u0) / 3);
            double? best = null;
            foreach (var l in lines)
            {
                if (!l.IsHorizontal() || l.Length < 100) continue;
                double ov = Math.Min(l.UMax, u1) - Math.Max(l.UMin, u0);
                if (ov < need) continue;
                double lz = (l.Z0 + l.Z1) / 2;
                if (Math.Abs(lz - z) > search || (accept != null && !accept(lz))) continue;
                if (best == null || Math.Abs(lz - z) < Math.Abs(best.Value - z)) best = lz;
            }
            return best;
        }

        private static void Slab(SecElement e, IList<SecSeg> lines, FidelityGate g, SecElementScore item, List<FidIssue> issues, string sec, SectionScore s)
        {
            var flat = e.Cut.Where(c => c.IsHorizontal() && c.Length >= 100).ToList();
            if (flat.Count == 0) return;
            double top = flat.Max(c => c.Z0), bottom = flat.Min(c => c.Z0), u0 = e.UMin, u1 = e.UMax;
            var lTop = e.Kind == "ceiling" ? null : LevelLine(lines, u0, u1, top, HeightSearchMm);
            var lBot = LevelLine(lines, u0, u1, bottom, HeightSearchMm, z => lTop == null || z < lTop.Value - 20);
            bool over = false;
            if (lTop is double t)
            {
                item.SurveyTopZ = Math.Round(t); item.TopDevMm = Math.Round(t - top);
                if (Math.Abs(t - top) > g.HeightMm)
                {
                    over = true;
                    issues.Add(new FidIssue
                    {
                        Kind = "slab_top", Level = sec, Id = e.Id, DevMm = Math.Round(t - top), Against = $"section: top of slab at {t:0} mm (model {top:0})",
                        At = new[] { Math.Round((u0 + u1) / 2), Math.Round(top) },
                        Fix = $"set_parameters id {e.Id} \"Height Offset From Level\" {Signed(t - top)} mm (or survey_section_autofix)", Severity = Math.Abs(t - top) / g.HeightMm,
                    });
                }
            }
            if (lBot is double b)
            {
                item.SurveyBottomZ = Math.Round(b); item.BottomDevMm = Math.Round(b - bottom);
            }
            if (e.Kind == "floor" && lTop is double t2 && lBot is double b2 && top - bottom > 20)
            {
                double dT = (t2 - b2) - (top - bottom);
                item.ThicknessDevMm = Math.Round(dT);
                if (Math.Abs(dT) > g.SlabThicknessMm)
                {
                    over = true;
                    issues.Add(new FidIssue
                    {
                        Kind = "slab_thickness", Level = sec, Id = e.Id, DevMm = Math.Round(dT), Against = $"section: slab {t2 - b2:0} mm thick (model {top - bottom:0})",
                        At = new[] { Math.Round((u0 + u1) / 2), Math.Round((top + bottom) / 2) },
                        Fix = $"create_floor replace_id {e.Id} thickness_mm {t2 - b2:0} with the same outline (or change_type to a {t2 - b2:0} mm floor type)", Severity = Math.Abs(dT) / g.SlabThicknessMm,
                    });
                }
            }
            if (over) s.SlabsOver++;
        }

        private static void Wall(SecElement e, IList<SecSeg> lines, List<SecElement> decks, FidelityGate g, SecElementScore item, List<FidIssue> issues, string sec, SectionScore s)
        {
            double u0 = e.UMin, u1 = e.UMax, top = e.ZMax, bottom = e.ZMin;
            if (u1 - u0 < 30 || top - bottom < 300) return;
            // the survey's top of this wall: a level line across it (a coping) near the top, else the tops of the
            // vertical lines on its faces that run up past its middle
            double mid = (top + bottom) / 2;
            var faces = lines.Where(l => l.IsVertical() && l.Length >= 300 && l.ZMax > mid && ((Math.Abs(l.U0 - u0) <= 30) || (Math.Abs(l.U0 - u1) <= 30))).ToList();
            double? surveyTop = null;
            if (faces.Count > 0)
            {
                double faceTop = faces.Max(f => f.ZMax);
                var coping = lines.Where(l => l.IsHorizontal() && l.UMin <= u0 + 20 && l.UMax >= u1 - 20 && Math.Abs(l.Z0 - faceTop) <= 150).Select(l => (double?)l.Z0).OrderByDescending(z => z).FirstOrDefault();
                surveyTop = coping ?? faceTop;
            }
            if (surveyTop == null) return;
            double t = surveyTop.Value;
            // too far off to be the same wall top: the face lines belong to something else (a chimney, the next storey)
            if (Math.Abs(t - top) > 4000) return;
            // a parapet: it stands up past the floors and roofs beside it
            double deckTop = decks.Where(d => d.UMax >= u0 - 600 && d.UMin <= u1 + 600 && d.ZMax <= Math.Max(t, top) + 50).Select(d => d.ZMax).DefaultIfEmpty(double.MinValue).Max();
            item.Parapet = deckTop > double.MinValue && Math.Max(t, top) > deckTop + 300 && bottom < deckTop + 50 + 300;
            item.SurveyTopZ = Math.Round(t); item.TopDevMm = Math.Round(t - top);
            if (Math.Abs(t - top) <= g.HeightMm) return;
            s.TopsOver++;
            string what = item.Parapet ? "parapet" : "wall";
            issues.Add(new FidIssue
            {
                Kind = item.Parapet ? "parapet_top" : "wall_top", Level = sec, Id = e.Id, DevMm = Math.Round(t - top),
                Against = $"section: {what} top at {t:0} mm (model {top:0})" + (t - top > 300 && item.Parapet ? ": the parapet is missing or too low" : ""),
                At = new[] { Math.Round((u0 + u1) / 2), Math.Round(top) },
                Fix = $"set_wall_extents wall_ids [{e.Id}] top_offset_mm (current {Signed(t - top)} mm), or survey_section_autofix",
                Severity = Math.Abs(t - top) / g.HeightMm,
            });
        }

        private static void Roof(SecElement e, IList<SecSeg> lines, FidelityGate g, SecElementScore item, List<FidIssue> issues, string sec, SectionScore s)
        {
            // the roof's upper edges: of the cut outline, the edges with nothing of the roof above them
            var cut = e.Cut.Where(c => !c.IsVertical() && c.Length >= 100).ToList();
            if (cut.Count == 0) return;
            var upper = cut.Where(c =>
            {
                double um = (c.U0 + c.U1) / 2, zm = (c.Z0 + c.Z1) / 2;
                return !cut.Any(o => o != c && o.ZAt(um) is double oz && oz > zm + 20);
            }).ToList();
            if (upper.Count == 0) return;
            bool over = false;
            // ridge: the highest point of the upper edges; eaves: the lowest end of them
            var pts = upper.SelectMany(c => new[] { (u: c.U0, z: c.Z0), (u: c.U1, z: c.Z1) }).ToList();
            var ridge = pts.OrderByDescending(p => p.z).First();
            var eaves = pts.OrderBy(p => p.z).First();
            // the section's roof surface over u: the highest line there within 600 mm (the roof's underside and the
            // ceiling under it are drawn below its top)
            double? SurveyZ(double u, double z) => lines.Where(l => !l.IsVertical()).Select(l => l.ZAt(u)).Where(v => v != null && Math.Abs(v.Value - z) <= 600)
                                                        .OrderByDescending(v => v.Value).FirstOrDefault();
            if (SurveyZ(ridge.u, ridge.z) is double rz)
            {
                item.RidgeDevMm = Math.Round(rz - ridge.z); item.SurveyTopZ = Math.Round(rz);
                if (Math.Abs(rz - ridge.z) > g.HeightMm)
                {
                    over = true;
                    issues.Add(new FidIssue
                    {
                        Kind = "roof_ridge", Level = sec, Id = e.Id, DevMm = Math.Round(rz - ridge.z), Against = $"section: roof top at {rz:0} mm (model {ridge.z:0})",
                        At = new[] { Math.Round(ridge.u), Math.Round(ridge.z) },
                        Fix = "if the eaves agree, the pitch is wrong (create_roof slopes_deg); if both are off by the same, survey_section_autofix moves the roof", Severity = Math.Abs(rz - ridge.z) / g.HeightMm,
                    });
                }
            }
            if (Math.Abs(eaves.z - ridge.z) > 50 && SurveyZ(eaves.u, eaves.z) is double ez)
            {
                item.EavesDevMm = Math.Round(ez - eaves.z); item.SurveyBottomZ = Math.Round(ez);
                if (Math.Abs(ez - eaves.z) > g.HeightMm)
                {
                    over = true;
                    issues.Add(new FidIssue
                    {
                        Kind = "roof_eaves", Level = sec, Id = e.Id, DevMm = Math.Round(ez - eaves.z), Against = $"section: eaves at {ez:0} mm (model {eaves.z:0})",
                        At = new[] { Math.Round(eaves.u), Math.Round(eaves.z) },
                        Fix = $"set_parameters id {e.Id} \"Base Offset From Level\" {Signed(ez - eaves.z)} mm (or survey_section_autofix)", Severity = Math.Abs(ez - eaves.z) / g.HeightMm,
                    });
                }
            }
            // pitch: the longest sloping upper edge against the nearest section line within 15 degrees of it
            var slope = upper.Where(c => c.PitchDeg >= 3).OrderByDescending(c => c.Length).FirstOrDefault();
            var flatTop = upper.OrderByDescending(c => c.Length).First();
            if (slope != null)
            {
                double um = (slope.U0 + slope.U1) / 2, zm = (slope.Z0 + slope.Z1) / 2;
                var match = lines.Where(l => !l.IsVertical() && l.Length >= 300 && Math.Abs(l.PitchDeg - slope.PitchDeg) <= 15 && Math.Sign(l.Z1 - l.Z0) * Math.Sign(l.U1 - l.U0) == Math.Sign(slope.Z1 - slope.Z0) * Math.Sign(slope.U1 - slope.U0))
                                 .Select(l => (l, z: l.ZAt(um))).Where(x => x.z != null && Math.Abs(x.z.Value - zm) <= 600).OrderBy(x => Math.Abs(x.z.Value - zm)).Select(x => x.l).FirstOrDefault();
                if (match != null)
                {
                    item.SurveyPitchDeg = Math.Round(match.PitchDeg, 1);
                    item.PitchDevDeg = Math.Round(match.PitchDeg - slope.PitchDeg, 1);
                    if (Math.Abs(item.PitchDevDeg.Value) > g.PitchDeg)
                    {
                        over = true;
                        issues.Add(new FidIssue
                        {
                            Kind = "roof_pitch", Level = sec, Id = e.Id, DevMm = Math.Round(Math.Abs(item.PitchDevDeg.Value) * 100) / 100,
                            Against = $"section: pitch {match.PitchDeg:0.0}° (model {slope.PitchDeg:0.0}°)", At = new[] { Math.Round(um), Math.Round(zm) },
                            Fix = $"create_roof with the same outline, slopes_deg {match.PitchDeg:0.0} on the edges this section crosses square, then delete roof {e.Id}",
                            Severity = Math.Abs(item.PitchDevDeg.Value) / g.PitchDeg,
                        });
                    }
                }
            }
            else
            {
                // a flat roof in the model: does the section draw it pitched? A sloping line at least 1 m long over the roof's span, rising from near its top
                double u0 = e.UMin, u1 = e.UMax, z = flatTop.Z0;
                var pitched = lines.Where(l => !l.IsVertical() && l.PitchDeg >= 10 && l.PitchDeg <= 75 && l.Length >= 1000 && l.UMin >= u0 - 300 && l.UMax <= u1 + 300 && Math.Abs(l.ZMin - z) <= 600)
                                   .OrderByDescending(l => l.Length).FirstOrDefault();
                if (pitched != null)
                {
                    over = true;
                    item.SurveyPitchDeg = Math.Round(pitched.PitchDeg, 1); item.PitchDevDeg = Math.Round(pitched.PitchDeg, 1);
                    issues.Add(new FidIssue
                    {
                        Kind = "roof_form", Level = sec, Id = e.Id, DevMm = Math.Round(pitched.ZMax - z),
                        Against = $"section: a {pitched.PitchDeg:0}° slope rising {pitched.ZMax - pitched.ZMin:0} mm to {pitched.ZMax:0}; the model roof is flat at {z:0}",
                        At = new[] { Math.Round((pitched.U0 + pitched.U1) / 2), Math.Round((pitched.Z0 + pitched.Z1) / 2) },
                        Line = new[] { Math.Round(pitched.U0), Math.Round(pitched.Z0), Math.Round(pitched.U1), Math.Round(pitched.Z1) },
                        Fix = $"create_roof with roof {e.Id}'s outline, offset at the eaves ({pitched.ZMin:0} mm model) and slopes_deg {pitched.PitchDeg:0.0} on the edges the slope falls to; then delete roof {e.Id}",
                        Severity = 2 + (pitched.ZMax - z) / 1000,
                    });
                }
            }
            if (over) s.RoofsOver++;
        }

        private static void Stair(SecElement e, IList<SecSeg> lines, FidelityGate g, SecElementScore item, List<FidIssue> issues, string sec, SectionScore s)
        {
            var (count, riser) = Risers(lines, e.UMin, e.UMax, e.ZMin, e.ZMax);
            if (count < 3) return;
            item.SurveyRisers = count; item.SurveyRiserMm = Math.Round(riser, 1);
            bool over = false;
            if (e.RiserMm is double mr && Math.Abs(riser - mr) > g.RiserMm)
            {
                over = true;
                issues.Add(new FidIssue
                {
                    Kind = "stair_riser", Level = sec, Id = e.Id, DevMm = Math.Round(riser - mr, 1), Against = $"section: {count} risers of {riser:0.0} mm (model {e.Risers} of {mr:0.0} mm)",
                    At = new[] { Math.Round((e.UMin + e.UMax) / 2), Math.Round((e.ZMin + e.ZMax) / 2) },
                    Fix = $"set_parameters id {e.Id} \"Desired Number of Risers\" to match the total rise / {riser:0.0} mm (Survey to Revit's survey_build stairs rebuilds from the plan)", Severity = Math.Abs(riser - mr) / g.RiserMm,
                });
            }
            else if (e.Risers is int mn && Math.Abs(mn - count) >= 2 && e.UMax - e.UMin > 1500)
            {
                // a count off by one is usually a riser at the landing the cut misses: only two or more is reported
                over = true;
                issues.Add(new FidIssue
                {
                    Kind = "stair_risers", Level = sec, Id = e.Id, DevMm = count - mn, Against = $"section: {count} risers where the cut crosses the stair (model {mn})",
                    At = new[] { Math.Round((e.UMin + e.UMax) / 2), Math.Round((e.ZMin + e.ZMax) / 2) },
                    Fix = "check the flight the section cuts against the plan (the cut may cross one flight only); set_parameters \"Desired Number of Risers\"", Severity = 1 + Math.Abs(mn - count) / 4.0,
                });
            }
            if (over) s.StairsOver++;
        }

        /// <summary>
        /// The risers a section draws over a stair's span: vertical lines 80 to 250 mm tall, a step apart, each with
        /// a level line (the tread) leaving its top. Count and median height.
        /// </summary>
        public static (int count, double riserMm) Risers(IList<SecSeg> lines, double u0, double u1, double z0, double z1)
        {
            var risers = lines.Where(l => l.IsVertical(5) && l.Length >= 80 && l.Length <= 250 && l.UMin >= u0 - 50 && l.UMax <= u1 + 50 && l.ZMin >= z0 - 60 && l.ZMax <= z1 + 60)
                              .Where(r => lines.Any(t => t.IsHorizontal(5) && t.Length >= 150 && t.Length <= 600 && Math.Abs(t.Z0 - r.ZMax) <= 10 && (Math.Abs(t.UMin - r.U0) <= 15 || Math.Abs(t.UMax - r.U0) <= 15)))
                              .OrderBy(r => r.ZMin).ToList();
            // one riser per height step (a riser drawn as two lines, or both stringer edges, counts once)
            var kept = new List<SecSeg>();
            foreach (var r in risers)
                if (!kept.Any(k => Math.Abs(k.ZMin - r.ZMin) <= 20 && Math.Abs(k.U0 - r.U0) <= 400)) kept.Add(r);
            if (kept.Count == 0) return (0, 0);
            var h = kept.Select(k => k.Length).OrderBy(x => x).ToList();
            return (kept.Count, h[h.Count / 2]);
        }

        private static string Signed(double v) => v.ToString("+0;-0;0", CultureInfo.InvariantCulture);

        /// <summary>Picks the sheet x range of one section among several side by side: from halfway to the title on its left to halfway to the one on its right.</summary>
        public static (double x0, double x1) SheetRange(IList<(string name, double x)> titles, string name)
        {
            var sorted = titles.OrderBy(t => t.x).ToList();
            int i = sorted.FindIndex(t => string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw new ArgumentException($"No section titled {name} on the sheet. Titles: {string.Join(", ", sorted.Select(t => t.name))}.");
            double lo = i == 0 ? double.MinValue : (sorted[i - 1].x + sorted[i].x) / 2;
            double hi = i == sorted.Count - 1 ? double.MaxValue : (sorted[i].x + sorted[i + 1].x) / 2;
            return (lo, hi);
        }

        private static readonly System.Text.RegularExpressions.Regex TitleRx = new System.Text.RegularExpressions.Regex(@"^\s*section[\s\-_]*([A-Z0-9]{1,3})\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        private static readonly System.Text.RegularExpressions.Regex PairRx = new System.Text.RegularExpressions.Regex(@"\b([A-Z0-9])\s*-\s*\1\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Section titles on a sheet ("Section A", "SECTION-B", "Section 1", "Sections A-A & B-B"), as Survey to Revit's SectionReader reads them.</summary>
        public static List<(string name, double x, double y)> Titles(IEnumerable<DwgText> texts)
        {
            var result = new List<(string, double, double)>();
            foreach (var t in texts)
            {
                var v = t.Value ?? "";
                var m = TitleRx.Match(v);
                if (m.Success) result.Add((m.Groups[1].Value.ToUpperInvariant(), t.At.X, t.At.Y));
                else if (v.IndexOf("section", StringComparison.OrdinalIgnoreCase) >= 0)
                    foreach (System.Text.RegularExpressions.Match p in PairRx.Matches(v)) result.Add((p.Groups[1].Value.ToUpperInvariant(), t.At.X, t.At.Y));
            }
            return result.GroupBy(r => r.Item1).Select(g => g.First()).ToList();
        }

        /// <summary>Section and model cut drawn over each other as a PNG: section lines dark, model cut red, issues ringed.</summary>
        public static byte[] Overlay(SectionInput input, IEnumerable<FidIssue> issues, int widthPx = 1600)
        {
            var img = new LineImage(widthPx);
            foreach (var l in input.Lines) img.Add(l.U0, l.Z0, l.U1, l.Z1, LineImage.Ink);
            foreach (var e in input.Elements) foreach (var c in e.Cut) img.Add(c.U0, c.Z0, c.U1, c.Z1, LineImage.Red, 2);
            foreach (var i in issues ?? Enumerable.Empty<FidIssue>())
                if (i.At != null && i.At.Length >= 2) img.Ring(i.At[0], i.At[1], 300, LineImage.Blue);
            return img.Png();
        }
    }
}
