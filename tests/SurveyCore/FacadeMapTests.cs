// 1.16.0: the facade detail map on a drawn neoclassical facade.
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class FacadeMapTests
    {
        static DwgLine L(double u0, double z0, double u1, double z1, string layer = "EF_25_10-Wall_Ext") => new DwgLine { A = new Pt(u0, z0), B = new Pt(u1, z1), Layer = layer };
        static IEnumerable<DwgLine> Rect(double u0, double z0, double u1, double z1, string layer = "EF_25_10-Wall_Ext")
        { yield return L(u0, z0, u1, z0, layer); yield return L(u1, z0, u1, z1, layer); yield return L(u1, z1, u0, z1, layer); yield return L(u0, z1, u0, z0, layer); }

        static IEnumerable<DwgLine> Window(double u0)
        {
            double u1 = u0 + 1200, c = u0 + 600;
            foreach (var l in Rect(u0, 4000, u1, 6000, "Ss_25_30_95-Window")) yield return l;
            yield return L(u0, 5000, u1, 5000, "Ss_25_30_95-Window");
            // moulded architrave: two steps, 50 and 150 out
            foreach (var o in new[] { 50.0, 150 }) { yield return L(u0 - o, 4000, u0 - o, 6000 + o); yield return L(u1 + o, 4000, u1 + o, 6000 + o); yield return L(u0 - o, 6000 + o, u1 + o, 6000 + o); }
            // sill with horns
            yield return L(u0 - 200, 3950, u1 + 200, 3950); yield return L(u0 - 200, 3900, u1 + 200, 3900);
            // hood
            yield return L(u0 - 250, 6300, u1 + 250, 6300); yield return L(u0 - 250, 6400, u1 + 250, 6400);
            // consoles under the hood ends
            foreach (var l in Rect(u0 - 240, 5900, u0 - 160, 6300)) yield return l;
            foreach (var l in Rect(u1 + 160, 5900, u1 + 240, 6300)) yield return l;
            // triangular pediment
            yield return L(u0 - 250, 6450, c, 6800); yield return L(c, 6800, u1 + 250, 6450);
            // keystone
            yield return L(c - 50, 6000, c - 70, 6250); yield return L(c + 50, 6000, c + 70, 6250); yield return L(c - 70, 6250, c + 70, 6250);
        }

        public static void Run()
        {
            System.Console.WriteLine("FacadeMap");
            var lines = new List<DwgLine>();
            foreach (var z in new[] { 15000.0, 15080, 15200 }) lines.Add(L(0, z, 10000, z));   // cornice
            lines.Add(L(0, 9000, 10000, 9000));                                                 // string course
            lines.Add(L(0, 300, 10000, 300));                                                   // plinth
            foreach (var z in new[] { 600.0, 1000, 1400, 1800, 2200 }) lines.Add(L(0, z, 10000, z)); // rustication
            lines.AddRange(Window(2000)); lines.AddRange(Window(6200));
            lines.Add(L(4500, 3000, 4500, 8500)); lines.Add(L(4900, 3000, 4900, 8500));          // pilaster
            for (int i = 0; i < 10; i++) lines.Add(L(10000 - (i % 2 == 0 ? 600 : 300), 3000 + 300 * i, 10000, 3000 + 300 * i)); // quoins
            for (int k = 0; k < 5; k++) lines.AddRange(Rect(5100 + 200 * k, 9100, 5200 + 200 * k, 9700)); // balusters
            lines.Add(L(5050, 9800, 6000, 9800));                                               // rail
            for (int k = 0; k < 8; k++) lines.AddRange(Rect(1000 + 1000 * k, 14800, 1150 + 1000 * k, 15000)); // modillions
            lines.Add(L(0, 20000, 10000, 20000, "Zz_20-Text"));                                // ignored

            var m = FacadeMap.Read(lines, 10000);
            FacadeItem One(string kind) => m.Items.FirstOrDefault(i => i.Kind == kind);
            T.Check(m.Windows.Count == 2, $"facade map: two windows found ({m.Windows.Count})");
            var cor = One("cornice");
            T.Check(cor != null && T.Near(cor.Z0, 15000, 1) && T.Near(cor.Z1, 15200, 1) && cor.LineHeights.Count == 3, $"facade map: the cornice with its three lines ({cor?.Z0}-{cor?.Z1})");
            T.Check(One("string course") is FacadeItem sc && T.Near(sc.Z0, 9000, 1), "facade map: the string course at 9000 (" + string.Join("; ", m.Items.Where(i => i.Runs != null).Select(i => i.Kind + " " + i.Z0 + " lines " + i.Lines + " u " + i.U0 + "-" + i.U1)) + ")");
            T.Check(One("plinth") != null, "facade map: the plinth");
            var rus = One("rustication");
            T.Check(rus != null && rus.Count == 5 && T.Near(rus.Size["pitch"], 400, 1), $"facade map: rustication, 5 joints at 400 ({rus?.Count}, {(rus == null ? 0 : rus.Size["pitch"])})");
            var arch = m.Items.Where(i => i.Kind == "architrave (moulded)").ToList();
            T.Check(arch.Count == 2 && T.Near(arch[0].Size["width"], 150, 1) && arch[0].Size["steps"] == 2, $"facade map: moulded architraves 150 wide in two steps ({arch.Count})");
            var sill = One("sill");
            T.Check(sill != null && T.Near(sill.Size["horns"], 200, 1), $"facade map: sill with 200 horns ({(sill == null ? 0 : sill.Size["horns"])})");
            T.Check(m.Items.Count(i => i.Kind == "hood (window cornice)") == 2, "facade map: a hood over each window");
            T.Check(m.Items.Count(i => i.Kind == "console") == 4, $"facade map: two consoles under each hood ({m.Items.Count(i => i.Kind == "console")})");
            var ped = One("pediment (triangular)");
            T.Check(ped != null && T.Near(ped.Size["rise"], 350, 1), $"facade map: triangular pediment rising 350 ({(ped == null ? 0 : ped.Size["rise"])})");
            var key = One("keystone");
            T.Check(key != null && T.Near(key.Size["width"], 140, 1) && T.Near(key.Size["height"], 250, 1), $"facade map: keystone 140 x 250 ({(key == null ? "none" : key.Size["width"] + "x" + key.Size["height"])})");
            var pil = One("pilaster");
            T.Check(pil != null && T.Near(pil.Size["width"], 400, 1), $"facade map: pilaster 400 wide ({(pil == null ? 0 : pil.Size["width"])})");
            var q = One("quoins");
            T.Check(q != null && T.Near(q.Size["long"], 600, 1) && T.Near(q.Size["short"], 300, 1), "facade map: quoins 600/300 at the right corner");
            var bal = One("balustrade");
            T.Check(bal != null && bal.Count == 5 && T.Near(bal.Size["pitch"], 200, 1), $"facade map: balustrade of 5 at 200 ({bal?.Count})");
            var mod = One("modillions / brackets under the cornice");
            T.Check(mod != null && mod.Count == 8 && T.Near(mod.Size["pitch"], 1000, 1), $"facade map: 8 modillions at 1000 ({mod?.Count})");
            var archType = m.Types.FirstOrDefault(t => t.Kind == "architrave (moulded)");
            T.Check(archType != null && archType.Count == 2, "facade map: the two alike surrounds are one type");
            T.Check(m.LayerUse["Zz_20-Text"].StartsWith("ignored") && m.LayerUse["Ss_25_30_95-Window"] == "windows", "facade map: layers sorted into windows, detail and ignored");
            T.Check(!m.Items.Any(i => i.Kind == "unclassified"), $"facade map: nothing left unexplained ({string.Join("; ", m.Items.Where(i => i.Kind == "unclassified").Select(i => $"{i.U0:0},{i.Z0:0} {i.Size["width"]}x{i.Size["height"]} {i.Note}"))})");
        }
    }
}
