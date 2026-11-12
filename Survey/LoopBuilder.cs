// Ported from the mcp-servers-for-revit survey pipeline (survey-pipeline.patch, base 86cf705). Revit-free.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public class LoopResult
    {
        public List<List<Pt>> Loops = new List<List<Pt>>();
        public int DanglingSegments;
        public int ComplexComponents;
        public List<string> Notes = new List<string>();
    }

    /// <summary>Joins loose line work into closed loops (e.g. building outlines drawn as separate LINEs).</summary>
    public static class LoopBuilder
    {
        public static LoopResult Build(IList<Seg> segs, double tolMm)
        {
            var res = new LoopResult();
            if (segs.Count == 0) return res;
            double tol = Math.Max(tolMm, 0.001);

            // 1) node ids by snapping endpoints
            var nodes = new List<Pt>();
            var grid = new Dictionary<GridKey, List<int>>();
            Func<Pt, int> nodeOf = p =>
            {
                long cx = (long)Math.Floor(p.X / tol), cy = (long)Math.Floor(p.Y / tol);
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        List<int> b;
                        if (!grid.TryGetValue(new GridKey(cx + dx, cy + dy), out b)) continue;
                        foreach (int id in b) if (nodes[id].Dist2D(p) <= tol) return id;
                    }
                nodes.Add(p);
                int nid = nodes.Count - 1;
                var k = new GridKey(cx, cy);
                List<int> bb; if (!grid.TryGetValue(k, out bb)) { bb = new List<int>(); grid[k] = bb; }
                bb.Add(nid);
                return nid;
            };

            var edges = new List<int[]>();
            var seen = new HashSet<GridKey>();
            foreach (var s in segs)
            {
                int a = nodeOf(s.A), b = nodeOf(s.B);
                if (a == b) continue;
                var key = a < b ? new GridKey(a, b) : new GridKey(b, a);
                if (!seen.Add(key)) continue; // duplicate edge
                edges.Add(new[] { a, b });
            }

            var adj = new Dictionary<int, List<int>>(); // node -> edge ids
            for (int e = 0; e < edges.Count; e++)
                foreach (int n in edges[e])
                {
                    List<int> l; if (!adj.TryGetValue(n, out l)) { l = new List<int>(); adj[n] = l; }
                    l.Add(e);
                }

            // 2) prune dangling edges (degree-1 nodes) iteratively
            var removed = new bool[edges.Count];
            var deg = adj.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
            var queue = new Queue<int>(deg.Where(kv => kv.Value == 1).Select(kv => kv.Key));
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                if (deg[n] != 1) continue;
                foreach (int e in adj[n])
                {
                    if (removed[e]) continue;
                    removed[e] = true; res.DanglingSegments++;
                    int other = edges[e][0] == n ? edges[e][1] : edges[e][0];
                    deg[n]--; deg[other]--;
                    if (deg[other] == 1) queue.Enqueue(other);
                }
            }

            // 3) walk components
            var visited = new bool[edges.Count];
            for (int start = 0; start < edges.Count; start++)
            {
                if (removed[start] || visited[start]) continue;
                // collect connected component of remaining edges
                var comp = new List<int>(); var stack = new Stack<int>(); stack.Push(start); visited[start] = true;
                while (stack.Count > 0)
                {
                    int e = stack.Pop(); comp.Add(e);
                    foreach (int n in edges[e])
                        foreach (int e2 in adj[n])
                            if (!removed[e2] && !visited[e2]) { visited[e2] = true; stack.Push(e2); }
                }
                bool simple = comp.All(e => edges[e].All(n => adj[n].Count(x => !removed[x]) == 2));
                if (!simple) { res.ComplexComponents++; res.Notes.Add("Component with " + comp.Count + " segments has junctions (T/X); not auto-joined."); continue; }

                // walk the ring
                var ring = new List<Pt>();
                int curEdge = comp[0]; int curNode = edges[curEdge][0];
                ring.Add(nodes[curNode]);
                int guard = comp.Count + 2;
                var used = new HashSet<int>();
                while (guard-- > 0 && used.Add(curEdge))
                {
                    int next = edges[curEdge][0] == curNode ? edges[curEdge][1] : edges[curEdge][0];
                    curNode = next;
                    int nextEdge = adj[curNode].First(x => !removed[x] && x != curEdge);
                    if (!used.Contains(nextEdge)) ring.Add(nodes[curNode]);
                    curEdge = nextEdge;
                }
                if (ring.Count >= 3) res.Loops.Add(ring);
            }
            return res;
        }
    }
}
