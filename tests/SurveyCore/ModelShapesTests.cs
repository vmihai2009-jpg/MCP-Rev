// 1.47.0: outlines as lines and arcs, normalised profiles, detail kinds and facade grouping (Model Library phase 2).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;
using Seg = BarrGazetas.RevitMcp.Survey.ModelShapes.Seg;

namespace SurveyCoreTests
{
    internal static class ModelShapesTests
    {
        public static void Run()
        {
            Console.WriteLine("ModelShapes");
            // a 150 high, 90 deep cornice-like profile at an offset: a fillet, a quarter-round of radius 60, a top line
            var prof = new List<Seg>
            {
                Seg.Line(1000, 2000, 1000, 2150), Seg.Line(1000, 2150, 1090, 2150), Seg.Line(1090, 2150, 1090, 2120),
                Seg.Arc(1090, 2120, 1030, 2060, 1030, 2120, 60, 1072.4, 2077.6), Seg.Line(1030, 2060, 1030, 2000), Seg.Line(1030, 2000, 1000, 2000),
            };
            var m = ModelShapes.Measure(prof);
            T.Check(m.widthMm == 90 && m.heightMm == 150 && m.lines == 5 && m.arcs == 1, "measure: box and counts");
            T.Check(T.Near(m.depthRatio, 0.6), "depth ratio = width / height");
            var n = ModelShapes.Normalise(prof);
            T.Check(n.Count == 6 && n.All(s => s.Points().All(p => p[0] >= 0 && p[1] >= 0 && p[1] <= 1)), "normalised to height 1 from the lower left");
            var arc = n.Single(s => s.k == "arc");
            T.Check(T.Near(arc.r.Value, 0.4) && T.Near(arc.c[0], 0.2) && T.Near(arc.c[1], 0.8), "the arc stays an arc, centre and radius scaled");
            var twice = ModelShapes.Normalise(prof.Select(s => new Seg { k = s.k, a = s.a.Select(v => v * 2).ToArray(), b = s.b.Select(v => v * 2).ToArray(), c = s.c?.Select(v => v * 2).ToArray(), m = s.m?.Select(v => v * 2).ToArray(), r = s.r * 2 }).ToList());
            T.Check(twice.Zip(n, (x, y) => T.Near(x.a[0], y.a[0]) && T.Near(x.b[1], y.b[1])).All(b => b), "the same moulding at twice the size normalises the same");
            T.Check(ModelShapes.Measure(new List<Seg>()) == null && ModelShapes.Normalise(new List<Seg>()).Count == 0, "no outline");
            T.Check(ModelShapes.Round(new[] { Seg.Line(1.234, 5.678, 0, 0) }).First().a[1] == 5.7, "outlines go out to 0.1 mm");

            T.Check(ModelShapes.DetailKind("BG Main Cornice", 400, 20000, 2000) == ("cornice", true), "a name word wins over the shape");
            T.Check(ModelShapes.DetailKind("Window Surround Type A", 2000, 1200, 3000) == ("architrave", true), "surround = architrave");
            T.Check(ModelShapes.DetailKind("Corbel 01", 300, 200, 4000) == ("console", true), "corbel = console");
            T.Check(ModelShapes.DetailKind("Fluted Pilaster", 9000, 600, 0) == ("pilaster", true), "pilaster by name");
            T.Check(ModelShapes.DetailKind("Generic sweep", 300, 18000, 200) == ("cornice", false), "long and low at the top: cornice (guessed)");
            T.Check(ModelShapes.DetailKind("Generic sweep", 250, 18000, 6000) == ("string course", false), "long and low lower down: string course");
            T.Check(ModelShapes.DetailKind("Model 3", 8000, 700, 500) == ("pilaster", false), "tall and narrow: pilaster");
            T.Check(ModelShapes.DetailKind("Model 4", 300, 250, 4000) == ("ornament", false), "small: ornament");
            T.Check(ModelShapes.DetailKind("Model 5", 1500, 1500, 4000) == ("other", false), "otherwise other");

            var g = ModelShapes.FacadeGroups(new[] { (90.0, 10000.0), (91.5, 10200.0), (90.0, 14000.0), (-90.0, 0.0), (359.0, 500.0), (1.0, 600.0) });
            T.Check(g[0] == g[1] && g[0] != g[2] && g[3] != g[0] && g[4] == g[5], "facades: same facing and plane together; across 0/360 too");
            T.Check(ModelShapes.Facing(90) == "N" && ModelShapes.Facing(-90) == "S" && ModelShapes.Facing(0) == "E" && ModelShapes.Facing(179) == "W", "facing words");
        }
    }
}
