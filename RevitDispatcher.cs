using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// Revit's API is single-threaded. The HTTP server runs on background threads, so every
    /// tool call is queued here and executed on Revit's main thread via an ExternalEvent.
    /// </summary>
    internal sealed class RevitDispatcher : IExternalEventHandler
    {
        private readonly ConcurrentQueue<(Func<UIApplication, object> Work, TaskCompletionSource<object> Tcs)> _queue = new();
        private readonly ExternalEvent _event;

        public RevitDispatcher()
        {
            _event = ExternalEvent.Create(this);
        }

        public Task<object> InvokeAsync(Func<UIApplication, object> work)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue((work, tcs));
            _event.Raise();
            return tcs.Task;
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var item))
            {
                try { item.Tcs.TrySetResult(item.Work(app)); }
                catch (Exception ex) { item.Tcs.TrySetException(ex); }
            }
        }

        public string GetName() => "Barr Gazetas MCP dispatcher";
    }
}
