// 1.19.0: courses at the elevation's heights (FacadeCourses).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class FacadeCourseTests
    {
        public static void Run()
        {
            Console.WriteLine("FacadeCourses");
            var runs = new List<(double, double)> { (0, 12000) };
            var courses = new List<(string, string, double, List<(double, double)>)>
            {
                ("B1", "string course", 6000, runs), ("B2", "cornice", 12000, runs), ("B3", "string course", 9500, runs),
            };
            var model = new List<FacadeEdge>
            {
                new FacadeEdge(0, 6005, 12000, 6005, 1, null, "detail"),        // ok
                new FacadeEdge(0, 12000, 8000, 12000, 2, null, "detail"),       // cornice ok to 8 m...
                new FacadeEdge(8000, 9000, 12000, 9000, 3, null, "detail"),     // ...then a storey low (the right block) on the cornice course
                new FacadeEdge(0, 9500, 8000, 9500, 4, null, "detail"),         // B3 ok to 8 m
                new FacadeEdge(8000, 6500, 12000, 6500, 5, null, "detail"),     // B3 a storey low on the right too
                new FacadeEdge(0, 0, 12000, 0, 9, null, "wall"),                // walls never carry a course
            };
            var drawnOpen = new List<(double, double, double, double)> { (1000, 2000, 1000, 2500), (4000, 5000, 1000, 2500) };
            var modelOpen = new List<(long, double, double, double, double)> { (20, 1000, 2000, 1000, 2500), (21, 4000, 5000, 1100, 2500) };
            var r = FacadeCourses.Check(courses, model, drawnOpen, modelOpen, 20);
            var b1 = r.Courses.First(c => c.Item == "B1"); var b2 = r.Courses.First(c => c.Item == "B2");
            T.Check(b1.Carried > 0.99, $"courses: a string course 5 mm off is carried ({b1.Carried:0.00})");
            var off = b2.Stretches.FirstOrDefault(s => s.State == "off");
            T.Check(off != null && T.Near(off.DzMm, -3000, 1) && off.StoreyOffset && off.U0 >= 7900 && off.Elements.Contains(3), $"courses: the cornice's right part is a storey low ({off?.DzMm} {off?.U0}-{off?.U1})");
            T.Check(T.Near(b2.Carried, 8000.0 / 12000, 0.02), $"courses: cornice carried on two thirds ({b2.Carried:0.00})");
            T.Check(r.CommonOffsets.Any(c => T.Near(c.dz, -3000, 1) && c.courses >= 2 && c.u0 >= 7900), $"courses: the right part is out on two courses: one cause ({string.Join(";", r.CommonOffsets)})");
            T.Check(r.RowsOff.Count == 1 && r.RowsOff[0].Element == 21 && T.Near(r.RowsOff[0].ModelSill - r.RowsOff[0].DrawnSill, 100, 1), "courses: the window 100 mm high on its drawn sill is listed");
        }
    }
}
