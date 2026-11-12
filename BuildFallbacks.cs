using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace BarrGazetas.RevitMcp
{
    /// <summary>Revit rolled a transaction back: the failure texts and the elements they name, so a caller can try another way.</summary>
    internal sealed class RevitRefusedException : InvalidOperationException
    {
        public RevitRefusedException(string message, IEnumerable<string> texts, IEnumerable<long> ids) : base(message)
        {
            Texts = texts.ToList();
            FailingIds = ids.Distinct().ToList();
        }

        public IReadOnlyList<string> Texts { get; }
        public IReadOnlyList<long> FailingIds { get; }
        public bool Says(string s) => Texts.Any(t => t.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) || Message.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>How a window or door gets its size when Revit will not make a new type of that size.</summary>
    internal enum TypeFallback
    {
        /// <summary>A type of the size asked for: an existing one or a new copy (the normal way).</summary>
        Make,
        /// <summary>Another family of the same category whose width and height are instance parameters.</summary>
        PerInstanceFamily,
        /// <summary>The nearest existing type, only if it is within <see cref="BuildTools.NearestTypeToleranceMm"/> each way.</summary>
        NearestType,
        /// <summary>Reshaping only: leave the size alone, still apply the sill and the slide.</summary>
        KeepSize,
    }

    /// <summary>One try at a window/door change: the fallback in force, and what it did (for the report).</summary>
    internal sealed class InsertCtx
    {
        public TypeFallback Mode;
        public string MadeType;
        public string Note;
    }

    /// <summary>A fallback that cannot apply here (no suitable family or type): go on to the next one.</summary>
    internal sealed class FallbackUnavailableException : InvalidOperationException
    {
        public FallbackUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// 1.12.0: one element that Revit will not build is reported in a line and the others go on. Each change below runs in
    /// its own transaction; when Revit refuses it, the failure is read and the change is tried again another way:
    /// windows/doors whose new type Revit can't make (another family sized per instance, the nearest type, or the size
    /// left alone), walls that come out "reversed" (joins stopped at the ends concerned), doors and windows Revit can't
    /// cut out of a moved wall (kept where they were along it), ceilings whose sketch is locked to a moved wall (remade
    /// with the same outline, unlocked), and windows that will not cut where they are placed (slid to fit, or a clear
    /// reason). Nothing is ever deleted to make a change go through, except a locked ceiling replaced by its copy.
    /// </summary>
    internal static partial class BuildTools
    {
        internal const double NearestTypeToleranceMm = 300;

        /// <summary>The family generated from the survey (window_family_from_dwg, sized per instance): first choice when a type can't be made.</summary>
        internal const string GeneratedWindowFamily = "BG Survey Window";

        // ================================================================ windows and doors: type fallback

        /// <summary>
        /// Runs a window/door change in its own transaction. If Revit refuses because a new type of that size can't be
        /// made, tries again with another family sized per instance, then the nearest existing type, then (reshape only)
        /// without the size change. Returns Revit's notes, the fallback used first.
        /// </summary>
        internal static List<string> RunInsertChange(Document doc, string name, Action<InsertCtx> body, bool canKeepSize)
        {
            var modes = canKeepSize
                ? new[] { TypeFallback.Make, TypeFallback.PerInstanceFamily, TypeFallback.NearestType, TypeFallback.KeepSize }
                : new[] { TypeFallback.Make, TypeFallback.PerInstanceFamily, TypeFallback.NearestType };
            Exception first = null;
            string madeType = null;
            var tried = new List<string>();
            foreach (var mode in modes)
            {
                if (mode != TypeFallback.Make && first == null) break;
                var ctx = new InsertCtx { Mode = mode };
                try
                {
                    var notes = Tx.Run(doc, name, () => body(ctx));
                    if (mode != TypeFallback.Make)
                        notes.Insert(0, $"Fallback: {ctx.Note ?? mode.ToString()}. (First try, with a new type {madeType}, refused: {Brief(first)})");
                    return notes;
                }
                catch (FallbackUnavailableException fu) { tried.Add(fu.Message); }
                catch (Exception ex) when (mode == TypeFallback.Make && ctx.MadeType != null)
                {
                    first = ex; madeType = ctx.MadeType; // a new type was made in this try: it may be what Revit refused
                }
                catch (Exception ex) when (mode != TypeFallback.Make) { tried.Add($"{mode}: {Brief(ex)}"); }
            }
            if (first == null) throw new InvalidOperationException("Nothing was changed: " + string.Join("; ", tried));
            throw new InvalidOperationException($"Nothing was changed for this window/door. Revit refused it (a new type {madeType} was made in that try): {Brief(first)} " +
                $"Fallbacks tried: {string.Join("; ", tried)}. Load a family that takes this size (or one sized per instance), then run again.", first);
        }

        private static string Brief(Exception ex)
        {
            if (ex is RevitRefusedException r && r.Texts.Count > 0) return string.Join(" ", r.Texts.Distinct().Select(t => $"\"{t}\""));
            return ex?.Message;
        }

        /// <summary>SizedSymbol when the normal way failed: per <paramref name="ctx"/>'s mode.</summary>
        private static FamilySymbol FallbackSymbol(Document doc, FamilySymbol baseSym, double wMm, double hMm, InsertCtx ctx)
        {
            if (ctx.Mode == TypeFallback.KeepSize)
            {
                var (bw, bh) = SymbolSize(baseSym);
                ctx.Note = $"size left at {bw:0}x{bh:0} (Revit can't make a {wMm:0}x{hMm:0} type of {baseSym.FamilyName}); sill and position still applied";
                return baseSym;
            }
            var all = new FilteredElementCollector(doc).OfCategoryId(baseSym.Category.Id).WhereElementIsElementType().OfType<FamilySymbol>().ToList();
            if (ctx.Mode == TypeFallback.PerInstanceFamily)
            {
                // 1.16.0: the parametric "BG Window" (window_family), when loaded, takes any size as a type of its own
                if (baseSym.Category.Id.Value == (long)BuiltInCategory.OST_Windows && baseSym.FamilyName != WindowFamilyBuilder.FamilyName
                    && BgWindowSized(doc, wMm, hMm) is FamilySymbol bg)
                {
                    ctx.MadeType = bg.Name;
                    ctx.Note = $"{bg.FamilyName}: {bg.Name} used (Revit can't make a {wMm:0}x{hMm:0} type of {baseSym.FamilyName}; plain panes: window_look gives it the elevation's)";
                    return bg;
                }
                var use = new FilteredElementCollector(doc).OfCategoryId(baseSym.Category.Id).WhereElementIsNotElementType().OfType<FamilyInstance>()
                    .GroupBy(f => f.Symbol.Family.Id).ToDictionary(g => g.Key, g => g.Count());
                var pick = all.Where(s => s.Family.Id != baseSym.Family.Id && !s.Family.IsInPlace
                                          && SizeParam(s, WidthParams, "Width") == null && SizeParam(s, HeightParams, "Height") == null)
                    .OrderBy(s => s.FamilyName.StartsWith(GeneratedWindowFamily, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenByDescending(s => use.TryGetValue(s.Family.Id, out var n) ? n : 0).FirstOrDefault()
                    ?? throw new FallbackUnavailableException("no other family in the model takes its size per instance");
                ctx.Note = $"{pick.FamilyName}: {pick.Name} used, sized per instance to {wMm:0}x{hMm:0} (Revit can't make that type of {baseSym.FamilyName})";
                return pick;
            }
            // nearest existing type: this family first, then any of the category
            var sized = all.Select(s => (s, sz: SymbolSize(s))).Where(x => x.sz.w != null && x.sz.h != null)
                .Select(x => (x.s, dw: x.sz.w.Value - wMm, dh: x.sz.h.Value - hMm)).ToList();
            var near = sized.Where(x => Math.Abs(x.dw) <= NearestTypeToleranceMm && Math.Abs(x.dh) <= NearestTypeToleranceMm)
                .OrderBy(x => x.s.Family.Id == baseSym.Family.Id ? 0 : 1).ThenBy(x => Math.Abs(x.dw) + Math.Abs(x.dh)).ToList();
            if (near.Count == 0) throw new FallbackUnavailableException($"no existing type within {NearestTypeToleranceMm:0} mm of {wMm:0}x{hMm:0}");
            var n0 = near[0];
            ctx.Note = $"nearest existing type {n0.s.FamilyName}: {n0.s.Name} used, {n0.dw:+0;-0;0} mm wide and {n0.dh:+0;-0;0} mm high of the {wMm:0}x{hMm:0} asked (Revit can't make that type of {baseSym.FamilyName})";
            return n0.s;
        }

        /// <summary>A plain "BG Window" type of this size (made if missing), or null when the family is not loaded.</summary>
        private static FamilySymbol BgWindowSized(Document doc, double wMm, double hMm)
        {
            var fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == WindowFamilyBuilder.FamilyName);
            var syms = fam?.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>().ToList();
            if (syms == null || syms.Count == 0) return null;
            string name = $"{wMm:0}x{hMm:0} plain";
            var sym = syms.FirstOrDefault(s => s.Name == name);
            if (sym == null)
            {
                sym = (FamilySymbol)syms[0].Duplicate(name);
                foreach (var (p, v) in new[] { ("Width", wMm / MmPerFoot), ("Height", hMm / MmPerFoot) })
                    if (sym.LookupParameter(p) is Parameter prm && !prm.IsReadOnly) prm.Set(v);
                foreach (var (p, v) in new[] { ("Panes Across", 1), ("Panes High Bottom", 1), ("Panes High Top", 1), ("Meeting Rail", 0) })
                    if (sym.LookupParameter(p) is Parameter prm && !prm.IsReadOnly && prm.StorageType == StorageType.Integer) prm.Set(v);
            }
            if (!sym.IsActive) sym.Activate();
            return sym;
        }

        /// <summary>With a per-instance fallback family, the size must really have taken; otherwise this fallback does not apply.</summary>
        private static void CheckFallbackSize(Document doc, FamilyInstance fi, double wMm, double hMm, InsertCtx ctx)
        {
            if (ctx?.Mode != TypeFallback.PerInstanceFamily) return;
            doc.Regenerate();
            var (fw, fh) = InsertSize(doc, fi);
            if (fw == null || fh == null || Math.Abs(fw.Value - wMm) > 5 || Math.Abs(fh.Value - hMm) > 5)
                throw new FallbackUnavailableException($"{fi.Symbol.FamilyName} did not take {wMm:0}x{hMm:0} per instance");
        }

        /// <summary>
        /// place_inserts for one item in its own transaction, with the type fallbacks; if Revit can't cut the window out of
        /// the wall where asked, it is slid along to fit inside the wall, or a line says why it can't go there.
        /// </summary>
        internal static object PlaceInsertSafely(Document doc, JsonObject item, string name, out List<string> notes)
        {
            object made = null;
            try
            {
                notes = RunInsertChange(doc, name, ctx => made = PlaceInsert(doc, item, ctx), false);
                return made;
            }
            catch (Exception ex) when (CutRefused(ex))
            {
                var (moved, note) = FitAlongWall(doc, item, ex);
                notes = RunInsertChange(doc, name, ctx => made = PlaceInsert(doc, moved, ctx), false);
                notes.Insert(0, "Fallback: " + note);
                return made;
            }
        }

        private static bool CutRefused(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e.Message.IndexOf("Can't cut", StringComparison.OrdinalIgnoreCase) >= 0 || e.Message.IndexOf("Cannot cut", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Where a window that Revit can't cut out of its wall could go: inside the wall's length, clear of the others; else why not.</summary>
        private static (JsonObject item, string note) FitAlongWall(Document doc, JsonObject item, Exception why)
        {
            const double margin = 100; // mm clear of the wall's ends (the joins' clean-up)
            double x = Dbl(item, "x") ?? 0, y = Dbl(item, "y") ?? 0;
            Wall wall = Long(item, "wall_id") is long wid ? doc.GetElement(new ElementId(wid)) as Wall : null;
            if (wall == null && Str(item, "level") is string ln)
            {
                try { wall = NearestWall(doc, FindLevel(doc, ln), new XYZ(x / MmPerFoot, y / MmPerFoot, 0), 1500 / MmPerFoot); } catch { /* reported below */ }
            }
            string first = Brief(why.InnerException ?? why);
            if (wall == null || !(wall.Location is LocationCurve lc) || !(lc.Curve is Line line))
                throw new InvalidOperationException($"Revit can't cut it out of the wall ({first}) and the wall is not a straight one to fit it along.", why);

            var a = line.GetEndPoint(0); var dir = line.Direction;
            double L = line.Length * MmPerFoot;
            double t = (new XYZ(x / MmPerFoot, y / MmPerFoot, a.Z) - a).DotProduct(dir) * MmPerFoot;
            double w = Dbl(item, "width_mm") ?? 900, h = Dbl(item, "height_mm") ?? 1500;
            bool door = (Str(item, "category") ?? "").StartsWith("door", StringComparison.OrdinalIgnoreCase);
            double sill = Dbl(item, "sill_mm") ?? (door ? 0 : 900);
            string where = $"wall {wall.Id.Value} ({L:0} mm long, {wall.Width * MmPerFoot:0} mm thick)";

            if (w + 2 * margin > L) throw new InvalidOperationException($"Not placed: a {w:0} mm wide opening can't fit in {where}. Revit said {first}");
            var bb = wall.get_BoundingBox(null);
            var baseLevel = doc.GetElement(wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId()) as Level;
            double baseZ = (baseLevel?.Elevation ?? 0) + (wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0);
            if (bb != null)
            {
                double wallH = (bb.Max.Z - baseZ) * MmPerFoot;
                if (sill + h > wallH + 1) throw new InvalidOperationException($"Not placed: the opening's head ({sill + h:0} mm above the wall's base) is above the top of {where}, {wallH:0} mm high. Revit said {first}");
            }

            double tFit = Math.Max(w / 2 + margin, Math.Min(L - w / 2 - margin, t));
            foreach (var e in Hosted(doc, wall))
            {
                if (!(e is FamilyInstance fi) || !(fi.Location is LocationPoint lp)) continue;
                double tc = (lp.Point - a).DotProduct(dir) * MmPerFoot;
                double cw = InsertSize(doc, fi).w ?? SymbolSize(fi.Symbol).w ?? 900;
                double s0 = (fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0) * MmPerFoot;
                double s1 = (fi.get_Parameter(BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM)?.AsDouble() ?? 0) * MmPerFoot;
                bool overlapsUpDown = s1 <= s0 || (sill < s1 && sill + h > s0);
                if (overlapsUpDown && Math.Abs(tFit - tc) < (w + cw) / 2 + 20)
                    throw new InvalidOperationException($"Not placed: it would overlap {fi.Category?.Name?.TrimEnd('s').ToLowerInvariant()} {fi.Id.Value} ({cw:0} wide at {tc:0} mm along) in {where}. Revit said {first}");
            }
            if (Math.Abs(tFit - t) < 1) throw new InvalidOperationException($"Not placed: Revit can't cut it out of {where} at {t:0} mm along: {first} Check the wall's profile and joins there.");
            var p = a + dir * (tFit / MmPerFoot);
            var moved = (JsonObject)item.DeepClone();
            moved["x"] = Math.Round(p.X * MmPerFoot);
            moved["y"] = Math.Round(p.Y * MmPerFoot);
            moved["wall_id"] = wall.Id.Value;
            return (moved, $"slid {tFit - t:+0;-0} mm along {where} so it fits inside it (Revit could not cut it out where asked: {first})");
        }

        // ================================================================ walls: a new line, safely

        /// <summary>
        /// Gives a wall a new line in its own transaction (<paramref name="before"/> runs first, e.g. a type change),
        /// keeping its direction (its outside face). If Revit refuses, tries again: ceilings sketched on the wall remade
        /// unlocked with the same outline; "reversed"/"can't make wall": joins stopped at the wall's ends and at the
        /// neighbours' ends that meet it; doors/windows that can't be cut out: kept where they were along the wall.
        /// Throws with every reason if no way works; the wall is then left as it was.
        /// </summary>
        internal static List<string> SetWallLineSafely(Document doc, Wall wall, Line newLine, string name, Action before = null)
        {
            var old = ((LocationCurve)wall.Location).Curve;
            if ((old.GetEndPoint(1) - old.GetEndPoint(0)).DotProduct(newLine.GetEndPoint(1) - newLine.GetEndPoint(0)) < 0)
                newLine = Line.CreateBound(newLine.GetEndPoint(1), newLine.GetEndPoint(0));
            long id = wall.Id.Value;

            var remake = new HashSet<long>();
            var stopJoins = new List<(long wall, int end)>();
            bool keepInserts = false;
            var tried = new List<string>();
            RevitRefusedException first = null;
            // 1.14.7: at most 3 tries (the first and two fallbacks); Revit died once after a fast run of refused tries
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var fixes = new List<string>();
                try
                {
                    var notes = Tx.Run(doc, name, () =>
                    {
                        before?.Invoke();
                        foreach (var cid in remake)
                            if (doc.GetElement(new ElementId(cid)) is Ceiling c)
                                fixes.Add($"ceiling {cid} remade as {RemakeCeilingUnlocked(doc, c)} with the same outline, no longer locked to wall {id}'s face (its sketch broke when the wall moved)");
                        foreach (var (wid, end) in stopJoins)
                            if (doc.GetElement(new ElementId(wid)) is Wall w && WallUtils.IsWallJoinAllowedAtEnd(w, end))
                            {
                                WallUtils.DisallowWallJoinAtEnd(w, end);
                                fixes.Add($"join stopped at wall {wid}'s {(end == 0 ? "start" : "end")} (set_wall_joins allow=true rejoins it)");
                            }
                        // the joins settle before the wall moves (moving a wall whose joins were just changed, unregenerated, is a crash suspect)
                        if (stopJoins.Count > 0) doc.Regenerate();
                        var kept = keepInserts ? InsertPoints(doc, wall) : null;
                        SetLineKeepingOpenings(doc, wall, newLine);
                        if (kept != null) fixes.AddRange(PutInsertsBack(doc, wall, kept));
                    });
                    if (attempt > 0) notes.InsertRange(0, fixes.Select(f => "Fallback: " + f));
                    return notes;
                }
                catch (RevitRefusedException ex)
                {
                    first ??= ex;
                    tried.Add(Brief(ex));
                    bool more = false;
                    // a ceiling whose sketch is locked to the wall
                    if (ex.Says("ceiling") || ex.Says("sketch"))
                    {
                        var found = CeilingsNamed(doc, ex.FailingIds);
                        if (found.Count == 0) found = CeilingsAlong(doc, wall);
                        foreach (var c in found) more |= remake.Add(c);
                    }
                    // a wall (this one or a neighbour) turned inside out by its joins
                    if ((ex.Says("reversed") || ex.Says("make wall")) && stopJoins.Count == 0)
                    {
                        stopJoins.Add((id, 0)); stopJoins.Add((id, 1));
                        foreach (var fid in ex.FailingIds.Where(f => f != id))
                            if (doc.GetElement(new ElementId(fid)) is Wall n && n.Location is LocationCurve nlc)
                                for (int end = 0; end < 2; end++)
                                    if (Near(nlc.Curve.GetEndPoint(end), old, newLine, wall.Width / 2 + 600 / MmPerFoot)) stopJoins.Add((fid, end));
                        more = true;
                    }
                    // a door or window that can't be cut out of the moved wall
                    if ((ex.Says("can't cut") || ex.Says("cannot cut")) && !keepInserts) { keepInserts = true; more = true; }
                    if (!more) break;
                }
            }
            throw new InvalidOperationException($"Wall {id} left where it was: {string.Join(" Then: ", tried.Distinct())}" +
                (tried.Count > 1 ? " (tried: ceilings remade unlocked, joins stopped, doors/windows kept in place, as they applied)." : ""), first);
        }

        private static bool Near(XYZ p, Curve a, Curve b, double tol)
        {
            double d(Curve c) { var r = c.Project(new XYZ(p.X, p.Y, c.GetEndPoint(0).Z)); return r == null ? double.MaxValue : new XYZ(r.XYZPoint.X - p.X, r.XYZPoint.Y - p.Y, 0).GetLength(); }
            return d(a) <= tol || d(b) <= tol;
        }

        private static List<(ElementId id, XYZ at, double w)> InsertPoints(Document doc, Wall wall) =>
            Hosted(doc, wall).OfType<FamilyInstance>().Where(f => f.Location is LocationPoint)
                .Select(f => (f.Id, ((LocationPoint)f.Location).Point, InsertSize(doc, f).w ?? SymbolSize(f.Symbol).w ?? 900)).ToList();

        /// <summary>After the wall moved: each door/window back to where it was along the wall (projected), inside the wall's length.</summary>
        private static List<string> PutInsertsBack(Document doc, Wall wall, List<(ElementId id, XYZ at, double w)> kept)
        {
            var said = new List<string>();
            doc.Regenerate();
            if (!(((LocationCurve)wall.Location).Curve is Line line)) return said;
            var a = line.GetEndPoint(0); var dir = line.Direction; double L = line.Length * MmPerFoot;
            foreach (var (iid, at, w) in kept)
            {
                if (!(doc.GetElement(iid) is FamilyInstance fi) || !(fi.Location is LocationPoint lp)) continue;
                double t = (new XYZ(at.X, at.Y, a.Z) - a).DotProduct(dir) * MmPerFoot;
                double tFit = Math.Max(w / 2 + 50, Math.Min(L - w / 2 - 50, t));
                var target = a + dir * (tFit / MmPerFoot);
                var move = new XYZ(target.X - lp.Point.X, target.Y - lp.Point.Y, 0);
                if (move.GetLength() * MmPerFoot > 1) ElementTransformUtils.MoveElement(doc, fi.Id, move);
                said.Add($"{fi.Category?.Name?.TrimEnd('s').ToLowerInvariant()} {iid.Value} kept where it was along wall {wall.Id.Value}" + (Math.Abs(tFit - t) > 1 ? $" (slid {tFit - t:+0;-0} mm to stay inside it)" : ""));
            }
            return said;
        }

        private static List<long> CeilingsNamed(Document doc, IEnumerable<long> ids)
        {
            var found = new List<long>();
            foreach (var i in ids)
            {
                var e = doc.GetElement(new ElementId(i));
                if (e is Ceiling) found.Add(i);
                else if (e is Sketch s && doc.GetElement(s.OwnerId) is Ceiling oc) found.Add(oc.Id.Value);
            }
            return found.Distinct().ToList();
        }

        /// <summary>Ceilings with a sketch line along the wall (within half its thickness + 50 mm, over most of its length).</summary>
        private static List<long> CeilingsAlong(Document doc, Wall wall)
        {
            var found = new List<long>();
            if (!(((LocationCurve)wall.Location).Curve is Line wl)) return found;
            double tol = wall.Width / 2 + 50 / MmPerFoot;
            var bb = wall.get_BoundingBox(null);
            foreach (var c in new FilteredElementCollector(doc).OfClass(typeof(Ceiling)).Cast<Ceiling>())
            {
                var cb = c.get_BoundingBox(null);
                if (bb != null && cb != null && (cb.Max.Z < bb.Min.Z - 1 || cb.Min.Z > bb.Max.Z + 1)) continue;
                if (!(doc.GetElement(c.SketchId) is Sketch sk)) continue;
                bool along = false;
                foreach (CurveArray arr in sk.Profile)
                    foreach (Curve cv in arr)
                        if (Near(cv.GetEndPoint(0), wl, wl, tol) && Near(cv.GetEndPoint(1), wl, wl, tol)) along = true;
                if (along) found.Add(c.Id.Value);
            }
            return found;
        }

        /// <summary>A copy of the ceiling from its current outline (no locks to walls), same type, level, offset, comments and phases; the original deleted.</summary>
        internal static long RemakeCeilingUnlocked(Document doc, Ceiling c)
        {
            var sk = doc.GetElement(c.SketchId) as Sketch ?? throw new InvalidOperationException($"ceiling {c.Id.Value} has no sketch to copy");
            var loops = new List<CurveLoop>();
            foreach (CurveArray arr in sk.Profile)
                loops.Add(CurveLoop.Create(Chain(arr.Cast<Curve>().ToList())));
            var nc = Ceiling.Create(doc, loops, c.GetTypeId(), c.LevelId);
            foreach (var bip in new[] { BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, BuiltInParameter.ALL_MODEL_MARK, BuiltInParameter.PHASE_CREATED, BuiltInParameter.PHASE_DEMOLISHED })
            {
                var from = c.get_Parameter(bip); var to = nc.get_Parameter(bip);
                if (from == null || to == null || to.IsReadOnly || !from.HasValue) continue;
                try
                {
                    switch (from.StorageType)
                    {
                        case StorageType.Double: to.Set(from.AsDouble()); break;
                        case StorageType.String: to.Set(from.AsString() ?? ""); break;
                        case StorageType.ElementId: to.Set(from.AsElementId()); break;
                        case StorageType.Integer: to.Set(from.AsInteger()); break;
                    }
                }
                catch { /* a value the copy can't take: keep its default */ }
            }
            doc.Delete(c.Id);
            return nc.Id.Value;
        }

        /// <summary>Curves of one sketch loop put head to tail (sketch profiles need not be in order or direction).</summary>
        internal static List<Curve> Chain(List<Curve> curves)
        {
            const double tol = 1e-4;
            var left = new List<Curve>(curves);
            if (left.Count == 0) return left;
            var chain = new List<Curve> { left[0] };
            left.RemoveAt(0);
            while (left.Count > 0)
            {
                var end = chain[chain.Count - 1].GetEndPoint(1);
                int k = left.FindIndex(c => c.GetEndPoint(0).IsAlmostEqualTo(end, tol) || c.GetEndPoint(1).IsAlmostEqualTo(end, tol));
                if (k < 0) k = Enumerable.Range(0, left.Count).OrderBy(i => Math.Min(left[i].GetEndPoint(0).DistanceTo(end), left[i].GetEndPoint(1).DistanceTo(end))).First();
                var next = left[k];
                left.RemoveAt(k);
                if (next.GetEndPoint(0).DistanceTo(end) > next.GetEndPoint(1).DistanceTo(end)) next = next.CreateReversed();
                chain.Add(next);
            }
            return chain;
        }
    }
}
