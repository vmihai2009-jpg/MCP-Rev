// 1.14.3: a measured survey is the existing building, so everything the tools make belongs in the survey's phase
// ("Existing"). Revit gives an element made through the API the project's latest phase ("New Construction"), which
// put the facade relief, the floor steps and anything else the tools made in the proposed scheme. Every write tool
// call now runs inside PhaseKeeper.Track: the elements Revit reports as added are put in the survey's phase after it.
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace BarrGazetas.RevitMcp
{
    internal static class PhaseKeeper
    {
        /// <summary>The survey's phase: one called "Existing", else the one most walls were created in, else the first.</summary>
        internal static Phase SurveyPhase(Document doc)
        {
            var phases = doc.Phases.Cast<Phase>().ToList();
            if (phases.Count == 0) return null;
            var named = phases.FirstOrDefault(p => p.Name.Equals("Existing", StringComparison.OrdinalIgnoreCase));
            if (named != null) return named;
            var byWalls = new FilteredElementCollector(doc).OfClass(typeof(Wall))
                .Select(w => w.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId())
                .Where(id => id != null && id != ElementId.InvalidElementId)
                .GroupBy(id => id.Value).OrderByDescending(g => g.Count()).Select(g => doc.GetElement(new ElementId(g.Key)) as Phase).FirstOrDefault();
            return byWalls ?? phases[0];
        }

        /// <summary>The building's own categories (not sketches, materials, cameras, links or annotation).</summary>
        internal static readonly HashSet<long> Building = new HashSet<long>(new[]
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Windows,
            BuiltInCategory.OST_Doors, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_Stairs, BuiltInCategory.OST_StairsRailing,
            BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_CurtainWallPanels,
            BuiltInCategory.OST_CurtainWallMullions, BuiltInCategory.OST_Toposolid, BuiltInCategory.OST_Ramps, BuiltInCategory.OST_Casework,
            BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_Parts, BuiltInCategory.OST_Site,
        }.Select(c => (long)c));

        internal static bool IsBuilding(Element e) => e?.Category != null && Building.Contains(e.Category.Id.Value);

        /// <summary>Puts these elements in the survey's phase (those that have a phase and aren't in it). Returns how many moved.</summary>
        internal static int Apply(Document doc, IEnumerable<ElementId> ids, List<string> notes = null)
        {
            var target = SurveyPhase(doc);
            if (target == null) return 0;
            var todo = ids.Select(id => doc.GetElement(id)).Where(e => e != null && !(e is ElementType) && IsBuilding(e))
                .Select(e => (e, p: e.get_Parameter(BuiltInParameter.PHASE_CREATED)))
                .Where(x => x.p != null && !x.p.IsReadOnly && x.p.AsElementId() != target.Id).ToList();
            if (todo.Count == 0) return 0;
            int moved = 0;
            var n = BuildTools.Tx.Run(doc, $"MCP: into the {target.Name} phase", () =>
            {
                foreach (var (e, p) in todo)
                    try { p.Set(target.Id); moved++; } catch { }
            });
            notes?.AddRange(n);
            return moved;
        }

        /// <summary>Watches a tool call: what it adds goes into the survey's phase when the call ends.</summary>
        internal static IDisposable Track(UIApplication app) => new Scope(app);

        private sealed class Scope : IDisposable
        {
            private readonly UIApplication _app;
            private readonly Dictionary<Document, HashSet<ElementId>> _added = new Dictionary<Document, HashSet<ElementId>>();

            public Scope(UIApplication app)
            {
                _app = app;
                _app.Application.DocumentChanged += Changed;
            }

            private void Changed(object sender, DocumentChangedEventArgs e)
            {
                var doc = e.GetDocument();
                if (doc == null || doc.IsFamilyDocument) return;
                if (!_added.TryGetValue(doc, out var set)) _added[doc] = set = new HashSet<ElementId>();
                foreach (var id in e.GetAddedElementIds()) set.Add(id);
            }

            public void Dispose()
            {
                _app.Application.DocumentChanged -= Changed;
                foreach (var kv in _added)
                    try { if (kv.Value.Count > 0 && kv.Key.IsValidObject && !kv.Key.IsReadOnly) Apply(kv.Key, kv.Value); }
                    catch { /* never fail the tool call for this */ }
            }
        }
    }
}
