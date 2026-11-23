// 1.36.16: fixes from the Mount Row runs (sheet names, label ownership, plain wall openings).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class HubFixTests
    {
        public static void Run()
        {
            Console.WriteLine("Hub fixes (1.36.16)");
            string root = Path.Combine(Path.GetTempPath(), "bgmcp-paths-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "_prep"));
            Directory.CreateDirectory(Path.Combine(root, "Elevations", "Sheets"));
            string sheet = Path.Combine(root, "Elevations", "Sheets", "29463B-9 (SECTIONS & ELEVATIONS).dwg");
            File.WriteAllText(sheet, "");
            try
            {
                T.Check(DrawingPaths.Resolve("29463B-9 (SECTIONS & ELEVATIONS)", new[] { Path.Combine(root, "_prep"), root }) == sheet,
                    "a sheet named without folder or .dwg is found under the survey folder");
                T.Check(DrawingPaths.Resolve(@"C:\old place\29463b-9 (sections & elevations).DWG", new[] { root }) == sheet, "a moved path is found by its name, case ignored");
                T.Check(DrawingPaths.Resolve(sheet, null) == Path.GetFullPath(sheet), "an existing path stays as it is");
                T.Check(DrawingPaths.Resolve("Not There", new[] { root }) == null && DrawingPaths.Resolve(null, new[] { root }) == null, "nothing found gives null");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            // two windows 1.5 m apart on one wall; one sill label beside the second only. The first must not be measured against it
            var gate = new FidelityGate();
            var f = new FidelityInput { Level = "L" };
            f.Inserts.Add(new FidInsert { Id = 1, Category = "Windows", HostWallId = 9, At = new Pt(0, 0), W = 900, Sill = 900, Head = 2100 });
            f.Inserts.Add(new FidInsert { Id = 2, Category = "Windows", HostWallId = 9, At = new Pt(1500, 0), W = 900, Sill = 600, Head = 2100 });
            f.Heights.Add(new HeightLabel { At = new Pt(1400, 300), Kind = 'S', Mm = 600 });
            var (s, issues) = Fidelity.Measure(f, gate);
            T.Check(s.Openings.LabelChecked == 1 && s.Openings.LabelOver == 0 && !issues.Any(i => i.Kind == "opening" && i.Id == 1),
                "a sill label is held only against the window nearest it");

            // a plan door where the model has a plain wall opening: not missing
            var g = new FidelityInput { Level = "L" };
            g.PlanOpenings.Add(new PlanOpening { At = new Pt(3000, 0), W = 900, Door = true, Head = 2100 });
            g.Inserts.Add(new FidInsert { Id = 5, Category = Fidelity.WallOpeningCategory, HostWallId = 9, At = new Pt(3010, 0), W = 900, Sill = 0, Head = 2100 });
            var (s2, _) = Fidelity.Measure(g, gate);
            T.Check(s2.Openings.Missing == 0, "a plain wall opening where the plan has a door counts as the door being there");
            g.Inserts.Clear();
            var (s3, _) = Fidelity.Measure(g, gate);
            T.Check(s3.Openings.Missing == 1, "with nothing there the door is missing");
        }
    }
}
