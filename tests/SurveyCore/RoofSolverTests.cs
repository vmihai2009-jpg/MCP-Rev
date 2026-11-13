// 1.36.0: roof structure from faces (RoofSolver).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class RoofSolverTests
    {
        public static void Run()
        {
            Console.WriteLine("RoofSolver");
            // a gable roof 10 m long (x), 8 m deep (y), ridge along y = 4000 at 13 m, eaves at 10 m (37 degrees)
            var south = new RoofFace { Id = 0, Ring = new List<Pt> { new Pt(0, 0), new Pt(10000, 0), new Pt(10000, 4000), new Pt(0, 4000) }, Plane = new PlaneZ { A = 0, B = 0.75, C = 10000 } };
            var north = new RoofFace { Id = 1, Ring = new List<Pt> { new Pt(0, 4000), new Pt(10000, 4000), new Pt(10000, 8000), new Pt(0, 8000) }, Plane = new PlaneZ { A = 0, B = -0.75, C = 16000 } };
            var e = RoofSolver.Edges(new[] { south, north });
            T.Check(e.Count(x => x.Kind == "ridge") == 1 && e.First(x => x.Kind == "ridge").GapMm == 0, "roof: the shared level top edge is a ridge");
            T.Check(e.Count(x => x.Kind == "eave") == 2 && e.Count(x => x.Kind == "verge") == 4, "roof: low level edges are eaves, sloping ends are verges");
            T.Check(Math.Abs(south.PitchDeg - 36.9) < 0.1 && south.FallsTowardDeg == -90, "roof: pitch and fall direction");
            var ev = RoofSolver.Eave(south);
            T.Check(ev.Edge == 0 && ev.BaseZ == 10000 && Math.Abs(ev.Slope - 0.75) < 1e-9 && ev.OffPlaneMm == 0, $"roof: the eave springs a native slope with no error ({ev.Edge}, {ev.BaseZ}, {ev.Slope}, {ev.OffPlaneMm})");
            // a hip: two faces meeting along a sloping line, both falling away
            // a hip: two faces meeting along the sloping line y = x, each rising towards it
            var f1 = new RoofFace { Id = 0, Ring = new List<Pt> { new Pt(0, 0), new Pt(4000, 4000), new Pt(0, 4000) }, Plane = new PlaneZ { A = 0.75, B = 0, C = 10000 } };
            var f2 = new RoofFace { Id = 1, Ring = new List<Pt> { new Pt(0, 0), new Pt(4000, 0), new Pt(4000, 4000) }, Plane = new PlaneZ { A = 0, B = 0.75, C = 10000 } };
            var h = RoofSolver.Edges(new[] { f1, f2 });
            T.Check(h.Any(x => x.B >= 0 && x.Kind == "hip"), $"roof: a sloping shared edge both faces fall away from is a hip ({string.Join(",", h.Select(x => x.Kind))})");
            var step = new RoofFace { Id = 1, Ring = north.Ring, Plane = new PlaneZ { A = 0, B = -0.75, C = 15000 } };
            T.Check(RoofSolver.Edges(new[] { south, step }).Any(x => x.Kind == "step" && x.GapMm == 1000), "roof: faces 1 m apart at their shared edge are a step");
        }
    }
}
