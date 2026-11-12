using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// While a tool runs, Revit shows no dialog that waits for a click: message boxes and task dialogs are answered
    /// (OK / Close), warnings are dismissed, and errors no transaction of ours handles (e.g. a family that fails to
    /// open for editing) roll back instead of opening Revit's error window. What was answered goes to Notes.
    /// Transactions run through Tx.Run keep their own handling (unjoin, never delete).
    /// </summary>
    internal sealed class Popups : IDisposable
    {
        [ThreadStatic] private static Popups _current;
        public static List<string> Notes => _current?._notes;

        private readonly UIApplication _app;
        private readonly List<string> _notes = new List<string>();
        private readonly Popups _outer;

        public static IDisposable Scope(UIApplication app) => app == null || _current != null ? (IDisposable)new Nothing() : new Popups(app);

        private Popups(UIApplication app)
        {
            _app = app; _outer = _current; _current = this;
            app.DialogBoxShowing += OnDialog;
            app.Application.FailuresProcessing += OnFailures;
        }

        private void OnDialog(object s, DialogBoxShowingEventArgs e) => ReferenceTools.Answer(e, _notes);

        private void OnFailures(object s, FailuresProcessingEventArgs e)
        {
            var fa = e.GetFailuresAccessor();
            bool error = false;
            foreach (var m in fa.GetFailureMessages())
            {
                if (m.GetSeverity() == FailureSeverity.Warning) { fa.DeleteWarning(m); continue; }
                error = true;
                _notes.Add("Revit error, rolled back: " + m.GetDescriptionText());
            }
            if (error) e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
        }

        public void Dispose()
        {
            try { _app.DialogBoxShowing -= OnDialog; } catch { }
            try { _app.Application.FailuresProcessing -= OnFailures; } catch { }
            _current = _outer;
        }

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
