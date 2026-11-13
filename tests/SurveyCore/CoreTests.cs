// 1.35.0: vertical cores and slab openings (Cores).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class CoreTests
    {
        public static void Run()
        {
            Console.WriteLine("Cores");
            // a dog-leg stair: two flights side by side on each floor, the same on three floors; the top floor shows only the arriving flight
            var f0 = new List<Rect> { new Rect(0, 0, 1000, 3000), new Rect(1100, 0, 2100, 3000) };
            var f1 = new List<Rect> { new Rect(0, 0, 1000, 3000), new Rect(1100, 0, 2100, 3000) };
            var f2 = new List<Rect> { new Rect(0, 0, 1000, 3000) };
            var w = Cores.StairWells(new List<List<Rect>> { f0, f1, f2 });
            T.Check(w[0].Count == 0, "cores: the lowest slab is not opened");
            T.Check(w[1].Count == 1 && w[1][0].W == 2100 && w[1][0].H == 3000, "cores: the floor above gets one well over both flights");
            T.Check(w[2].Count == 1 && w[2][0].W == 2100, "cores: the top floor is open over the flights arriving from below");
            var lone = Cores.StairWells(new List<List<Rect>> { new List<Rect>(), new List<Rect> { new Rect(5000, 5000, 6000, 8000) } });
            T.Check(lone[1].Count == 0, "cores: a flight starting on a floor does not open that floor's slab");
            // a 1.6 x 1.8 m lift box drawn as four lines, and a room-sized box that is not a lift
            var segs = new List<(Pt, Pt)>
            {
                (new Pt(0, 0), new Pt(1600, 0)), (new Pt(1600, 0), new Pt(1600, 1800)), (new Pt(1600, 1800), new Pt(0, 1800)), (new Pt(0, 1800), new Pt(0, 0)),
                (new Pt(5000, 0), new Pt(11000, 0)), (new Pt(11000, 0), new Pt(11000, 6000)), (new Pt(11000, 6000), new Pt(5000, 6000)), (new Pt(5000, 6000), new Pt(5000, 0)),
            };
            var boxes = Cores.ClosedBoxes(segs);
            T.Check(boxes.Count == 1 && boxes[0].W == 1600 && boxes[0].H == 1800, "cores: a closed lift-sized box is a lift, a room-sized one is not");
            var sh = Cores.Shafts(new List<List<Rect>> { boxes, boxes, new List<Rect>() });
            T.Check(sh[1].Count == 1 && sh[2].Count == 0, "cores: a lift on two floors opens the upper slab; not past where it stops");
        }
    }
}
