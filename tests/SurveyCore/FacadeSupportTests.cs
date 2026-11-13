// 1.20.0: relief above its own stretch of wall goes onto the roof behind (FacadeRelief.TopProfile / AboveOwnWall).
using System;
using System.Collections.Generic;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class FacadeSupportTests
    {
        public static void Run()
        {
            Console.WriteLine("FacadeSupport");
            // a 20 m facade: a 15 m block on the left, a 12 m block on the right, a 500 mm gap between wall pieces at 9.75-10.25 m
            var tops = FacadeRelief.TopProfile(new List<(double, double, double)> { (0, 9750, 15000), (10250, 20000, 12000) }, 20000);
            T.Check(T.Near(tops.Over(2000, 4000), 15000, 1), "support: the left block's top");
            T.Check(T.Near(tops.Over(14000, 16000), 12000, 1), "support: the right block keeps its own, lower top");
            T.Check(tops.Over(9900, 10100) >= 12000, "support: a gap between wall pieces takes a neighbour's top");
            ReliefFace F(double u0, double u1, double z0, double z1) => new ReliefFace { U0 = u0, U1 = u1, Z0 = z0, Z1 = z1 };
            T.Check(!FacadeRelief.AboveOwnWall(F(2000, 3000, 13000, 14000), tops), "support: a face below the left top stays on the facade");
            T.Check(FacadeRelief.AboveOwnWall(F(15000, 16000, 13000, 14000), tops), "support: the same face over the right block is above its wall (a dormer / roof detail)");
            T.Check(!FacadeRelief.AboveOwnWall(F(15000, 16000, 12100, 12800), tops), "support: a parapet within the margin stays");
            T.Check(!FacadeRelief.AboveOwnWall(F(15000, 16000, 11000, 12500), tops), "support: a cornice that starts below the top stays");
        }
    }
}
