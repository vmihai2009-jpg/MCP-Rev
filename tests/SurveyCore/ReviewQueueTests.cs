// 1.32.0: review queue and provenance (ReviewQueue).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class ReviewQueueTests
    {
        public static void Run()
        {
            Console.WriteLine("ReviewQueue");
            var q = ReviewQueue.Rank(new[]
            {
                new ReviewEntry { Report = "facade", Kind = "shifted", Element = 7, Weight = 2000, Where = "Front" },
                new ReviewEntry { Report = "support", Kind = "floating detail", Element = 7, Weight = 4000, Where = "Front" },
                new ReviewEntry { Report = "facade", Kind = "spurious", Element = 9, Weight = 5000, Where = "Front" },
                new ReviewEntry { Report = "conflicts", Kind = "height conflict", Weight = 1000, Where = "Level 2" },
            });
            T.Check(q.Count == 3 && q[0].Element == 7 && q[0].Weight == 6000 && q[0].Reasons.Count == 2, "review: an element several checks name rises to the top");
            T.Check(q[0].Reasons[0].Kind == "floating detail", "review: its heaviest reason first");
            T.Check(q[2].Element == null && q[2].Rank == 3 && q[2].Confidence > q[0].Confidence, "review: lighter items lower, more confident");
            T.Check(ReviewQueue.Provenance("SurveyToRevit|L01|Wall").by == "Survey to Revit" && ReviewQueue.Provenance("SurveyToRevit|L01|Wall").what == "Wall", "provenance: Survey to Revit's tag");
            T.Check(ReviewQueue.Provenance("MCP|facade_types|Front|T1|box:1,2,3,4").by == "MCP facade_types", "provenance: the hub's tag names the tool");
            T.Check(ReviewQueue.Provenance("").by == "untagged" && ReviewQueue.Provenance("my note").by == "untagged", "provenance: no tag");
        }
    }
}
