// Revit-free. 1.19.0: are the facade's horizontal courses at the elevation's heights? The courses are the lines the
// elevation draws along the facade (cornices, string courses, plinths, from FacadeMap's bands) and its rows of windows
// (sills and heads drawn level). Along each course, every 100 mm, the nearest horizontal model edge is found; stretches
// where the model runs off the drawn height are reported with their offset. A stretch a storey out, or a part of the
// facade where every course is out by the same amount, is named for what it likely is (wrong level / wrong height fit).
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public class CourseStretch
    {
        public double U0, U1, DzMm;
        /// <summary>ok | off | missing</summary>
        public string State;
        public List<long> Elements = new List<long>();
        public bool StoreyOffset;
    }

    public class CourseResult
    {
        public string Item, Kind;
        public double Z, Carried, LengthMm;
        public List<CourseStretch> Stretches = new List<CourseStretch>();
    }

    public class RowOpening
    {
        public long Element;
        public double DrawnSill, DrawnHead, ModelSill, ModelHead, U0, U1;
    }

    public class CourseCheckResult
    {
        public List<CourseResult> Courses = new List<CourseResult>();
        /// <summary>Parts of the facade where two or more courses are out by the same amount: a height fit or a level, not one element.</summary>
        public List<(double u0, double u1, double dz, int courses)> CommonOffsets = new List<(double, double, double, int)>();
        public List<RowOpening> RowsOff = new List<RowOpening>();
        public double Carried;
    }

    public static class FacadeCourses
    {
        /// <param name="courses">Course heights and their runs along the facade (from FacadeMap bands), with an id and kind.</param>
        /// <param name="model">Model edges on the facade plane (FacadeMatch frame).</param>
        /// <param name="drawnOpenings">Openings the elevation draws (u0, u1, z0, z1).</param>
        /// <param name="modelOpenings">The model's windows and doors (element, u0, u1, z0, z1).</param>
        public static CourseCheckResult Check(IList<(string id, string kind, double z, List<(double u0, double u1)> runs)> courses,
            IList<FacadeEdge> model, IList<(double u0, double u1, double z0, double z1)> drawnOpenings,
            IList<(long id, double u0, double u1, double z0, double z1)> modelOpenings, double tolMm = 20, double reachMm = 4800, double stepMm = 100)
        {
            var r = new CourseCheckResult();
            var horiz = model.Where(e => e.Len >= 50 && Math.Abs(e.Z1 - e.Z0) <= Math.Max(2, 0.02 * e.Len) && e.Kind != "wall" && e.Kind != "floor")
                .Select(e => (lo: Math.Min(e.U0, e.U1), hi: Math.Max(e.U0, e.U1), z: (e.Z0 + e.Z1) / 2, e.Element)).OrderBy(e => e.lo).ToList();
            // a strip index along u
            const double strip = 500;
            var byStrip = new Dictionary<long, List<int>>();
            for (int i = 0; i < horiz.Count; i++)
                for (long s = (long)Math.Floor(horiz[i].lo / strip); s <= (long)Math.Floor(horiz[i].hi / strip); s++)
                { if (!byStrip.TryGetValue(s, out var l)) byStrip[s] = l = new List<int>(); l.Add(i); }

            double totLen = 0, totOk = 0;
            var courseCands = new List<List<(double u, List<double> dzs)>>();
            foreach (var c in courses)
            {
                var cr = new CourseResult { Item = c.id, Kind = c.kind, Z = c.z };
                var samples = new List<(double u, string state, double dz, long el)>();
                var cands = new List<(double u, List<double> dzs)>();
                foreach (var (u0, u1) in c.runs)
                    for (double u = u0 + stepMm / 2; u < u1; u += stepMm)
                    {
                        (double dz, long el) best = (double.NaN, 0);
                        var all = new List<double>();
                        if (byStrip.TryGetValue((long)Math.Floor(u / strip), out var cand))
                            foreach (int i in cand)
                            {
                                var h = horiz[i];
                                if (u < h.lo || u > h.hi) continue;
                                double dz = h.z - c.z;
                                if (Math.Abs(dz) > reachMm) continue;
                                all.Add(dz);
                                if (double.IsNaN(best.dz) || Math.Abs(dz) < Math.Abs(best.dz)) best = (dz, h.Element);
                            }
                        string st = double.IsNaN(best.dz) ? "missing" : Math.Abs(best.dz) <= tolMm ? "ok" : "off";
                        if (st != "ok") cands.Add((u, all));
                        samples.Add((u, st, best.dz, best.el));
                    }
                cr.LengthMm = samples.Count * stepMm;
                cr.Carried = samples.Count == 0 ? 0 : (double)samples.Count(s => s.state == "ok") / samples.Count;
                totLen += cr.LengthMm; totOk += samples.Count(s => s.state == "ok") * stepMm;
                // stretches: runs of one state (off: one offset, within 3 tolerances)
                CourseStretch cur = null;
                foreach (var s in samples)
                {
                    bool same = cur != null && cur.State == s.state && s.u - cur.U1 <= stepMm * 1.5
                                && (s.state != "off" || Math.Abs(s.dz - cur.DzMm) <= 3 * tolMm);
                    if (!same)
                    {
                        if (cur != null) cr.Stretches.Add(cur);
                        cur = new CourseStretch { U0 = s.u - stepMm / 2, U1 = s.u + stepMm / 2, State = s.state, DzMm = s.state == "missing" ? 0 : s.dz };
                    }
                    else cur.U1 = s.u + stepMm / 2;
                    if (s.el != 0 && s.state != "missing" && !cur.Elements.Contains(s.el) && cur.Elements.Count < 10) cur.Elements.Add(s.el);
                }
                if (cur != null) cr.Stretches.Add(cur);
                foreach (var st in cr.Stretches.Where(x => x.State == "off")) st.StoreyOffset = Math.Abs(st.DzMm) >= 2400 && Math.Abs(st.DzMm) <= 4800;
                // short off/missing blips (< 300 mm) are joints and returns, not faults
                cr.Stretches = cr.Stretches.Where(x => x.State == "ok" || x.U1 - x.U0 >= 300).ToList();
                r.Courses.Add(cr);
                courseCands.Add(cands);
            }
            r.Carried = totLen > 0 ? totOk / totLen : 0;

            // the same offset over the same stretch on two or more courses: the part of the facade is out, not a detail. Where a
            // part is shifted the nearest edge to one course can be another course's: so every edge within reach votes, binned.
            double bin = Math.Max(10, 2 * tolMm);
            var votes = new Dictionary<long, Dictionary<int, List<double>>>();   // dz bin -> course -> u samples
            for (int k = 0; k < courseCands.Count; k++)
                foreach (var (u, dzs) in courseCands[k])
                    foreach (var b in dzs.Where(d => Math.Abs(d) > tolMm).Select(d => (long)Math.Round(d / bin)).Distinct())
                    {
                        if (!votes.TryGetValue(b, out var per)) votes[b] = per = new Dictionary<int, List<double>>();
                        if (!per.TryGetValue(k, out var us)) per[k] = us = new List<double>();
                        us.Add(u);
                    }
            var taken = new List<(double u0, double u1, double dz)>();
            foreach (var kv in votes.Where(v => v.Value.Count >= 2).OrderByDescending(v => v.Value.Sum(x => x.Value.Count)))
            {
                // the stretch every voting course shares
                var spans = kv.Value.Where(x => x.Value.Count * stepMm >= 500).Select(x => (lo: x.Value.Min() - stepMm / 2, hi: x.Value.Max() + stepMm / 2)).ToList();
                if (spans.Count < 2) continue;
                double lo = spans.OrderByDescending(x => x.lo).Skip(1).First().lo, hi = spans.OrderBy(x => x.hi).Skip(1).First().hi;   // shared by at least two
                if (hi - lo < 500) continue;
                double dz = kv.Key * bin;
                if (taken.Any(t => Math.Abs(t.dz - dz) <= bin * 1.5 && Math.Min(t.u1, hi) - Math.Max(t.u0, lo) > 0)) continue;
                taken.Add((lo, hi, dz));
                r.CommonOffsets.Add((Math.Round(lo), Math.Round(hi), Math.Round(dz), spans.Count));
            }

            // window rows: the model's sill and head against the opening the elevation draws
            foreach (var m in modelOpenings)
            {
                var d = drawnOpenings.Where(o => Math.Min(o.u1, m.u1) - Math.Max(o.u0, m.u0) >= 0.5 * Math.Min(o.u1 - o.u0, m.u1 - m.u0)
                                                 && Math.Min(o.z1, m.z1) - Math.Max(o.z0, m.z0) > 0)
                    .OrderByDescending(o => Math.Min(o.z1, m.z1) - Math.Max(o.z0, m.z0)).FirstOrDefault();
                if (d.u1 - d.u0 <= 0) continue;
                if (Math.Abs(m.z0 - d.z0) > tolMm || Math.Abs(m.z1 - d.z1) > tolMm)
                    r.RowsOff.Add(new RowOpening { Element = m.id, DrawnSill = d.z0, DrawnHead = d.z1, ModelSill = m.z0, ModelHead = m.z1, U0 = m.u0, U1 = m.u1 });
            }
            return r;
        }
    }
}
