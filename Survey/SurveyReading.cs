// Revit-free. Survey to Revit's own reading of the survey DWGs (its survey_reading tool writes it to a file, schema
// "survey-reading/1"), loaded for the 1:1 score and autofix. The hub then measures against the same layer roles
// (built-in rules plus the survey's layers.txt) and the same label formats as the build, instead of reading the
// DWGs again with its own layer names. Everything is turned into this project's internal mm on load.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>A height written on a plan, above its floor: 'S' sill, 'H' head, 'O' sill-to-head, 'D' door head, 'C' ceiling.</summary>
    public class HeightLabel
    {
        public Pt At;
        public char Kind;
        public double Mm;
    }

    /// <summary>A window or door Survey to Revit read on a plan: where, how wide, and its heights above the floor.</summary>
    public class PlanOpening
    {
        public Pt At;
        public double W;
        public double? Sill, Head;
        public bool Door;
        public bool HeightsFromSurvey;
    }

    public class ReadingWall
    {
        public Pt A, B;
        public double ThicknessMm;
        public string Confidence;
        /// <summary>Set for a curved wall: centre, radius, start and end angle (degrees, counter-clockwise).</summary>
        public double[] Arc;
        public bool Curtain;
    }

    public class ReadingStair
    {
        public int Risers;
        public List<Pt> Footprint = new List<Pt>();
    }

    public class ReadingSection
    {
        public string Name, File, Note;
        public Seg? Cut;
        public List<double> HeightsMm = new List<double>();
    }

    /// <summary>One floor of the reading, in this project's internal mm (heights as the file gives them).</summary>
    public class ReadingLevel
    {
        public string Code, Name, Plan;
        /// <summary>Floor level above survey datum (mm); NaN when the survey gives none.</summary>
        public double ElevationMm = double.NaN;
        public double CeilingMm = double.NaN;
        public bool CeilingFromSurvey;
        public List<Seg> WallFaces = new List<Seg>(), WindowLines = new List<Seg>(), DoorLines = new List<Seg>();
        /// <summary>Wall face arcs as drawn: centre x, y, radius, start and end angle (degrees).</summary>
        public List<double[]> WallFaceArcs = new List<double[]>();
        public List<ReadingWall> Walls = new List<ReadingWall>();
        public List<PlanOpening> Openings = new List<PlanOpening>();
        public List<HeightLabel> Heights = new List<HeightLabel>();
        public List<(Pt at, double aodMm)> SpotHeights = new List<(Pt, double)>();
        public List<(Pt at, string name)> Rooms = new List<(Pt, string)>();
        public List<(Pt at, double mm)> CeilingNotes = new List<(Pt, double)>();
        public List<ReadingStair> Stairs = new List<ReadingStair>();
        public List<Pt> Outline;
    }

    public class SurveyReading
    {
        public const string Schema = "survey-reading/1";
        public string Path, Folder, Engine, LayerFile, Made;
        /// <summary>Survey to Revit's frame: survey base point (grid mm) and project north (degrees).</summary>
        public double BaseEastMm, BaseNorthMm, RotationDeg;
        public List<ReadingLevel> Levels = new List<ReadingLevel>();
        public List<ReadingSection> Sections = new List<ReadingSection>();
        public List<string> Notes = new List<string>();
        /// <summary>How the reading was put into this project ("same frame", or the shift and turn applied).</summary>
        public string FrameNote;

        /// <summary>The floor of this name or code, or null.</summary>
        public ReadingLevel Level(string nameOrCode) =>
            Levels.FirstOrDefault(l => string.Equals(l.Name, nameOrCode, StringComparison.OrdinalIgnoreCase))
            ?? Levels.FirstOrDefault(l => string.Equals(l.Code, nameOrCode, StringComparison.OrdinalIgnoreCase));

        private static readonly Dictionary<string, (DateTime when, string json)> Cache = new Dictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Reads the file and puts it into the project whose shared coordinates are given (the project position at
        /// internal 0: east, north in mm, angle in radians, as Revit's ProjectPosition). A project Build from Survey
        /// set up has the same frame, so nothing moves; any other project gets the reading moved through the survey
        /// grid onto its own coordinates.
        /// </summary>
        public static SurveyReading Load(string path, double ppEastMm, double ppNorthMm, double ppAngle)
        {
            string text;
            lock (Cache)
            {
                var when = File.GetLastWriteTimeUtc(path);
                if (!Cache.TryGetValue(path, out var c) || c.when != when) { c = (when, File.ReadAllText(path)); Cache[path] = c; }
                text = c.json;
            }
            var r = Parse(JsonNode.Parse(text) as JsonObject, ppEastMm, ppNorthMm, ppAngle);
            r.Path = path;
            return r;
        }

        public static SurveyReading Parse(JsonObject root, double ppEastMm, double ppNorthMm, double ppAngle)
        {
            if (root == null) throw new InvalidDataException("The survey reading is empty.");
            string schema = Str(root["schema"]);
            if (schema == null || !schema.StartsWith("survey-reading/", StringComparison.Ordinal))
                throw new InvalidDataException("Not a Survey to Revit reading (no schema survey-reading/N).");
            if (schema != Schema && int.TryParse(schema.Substring(15), out int v) && v > 1)
                throw new InvalidDataException($"The reading is {schema}; this BG MCP reads {Schema}. Update BG MCP.");

            var r = new SurveyReading
            {
                Folder = Str(root["folder"]), Engine = Str(root["engine"]), LayerFile = Str(root["layerFile"]), Made = Str(root["made"]),
                BaseEastMm = Num(root["frame"]?["baseEastMm"]) ?? 0, BaseNorthMm = Num(root["frame"]?["baseNorthMm"]) ?? 0, RotationDeg = Num(root["frame"]?["rotationDeg"]) ?? 0,
            };
            var map = r.ToProject(ppEastMm, ppNorthMm, ppAngle, out r.FrameNote);
            Pt P(double x, double y) => map(new Pt(x, y));
            Pt? PointOf(JsonNode n) => Num(n?["x"]) is double x && Num(n?["y"]) is double y ? P(x, y) : (Pt?)null;
            List<Seg> Segs(JsonNode n) => (n as JsonArray)?.OfType<JsonArray>().Where(a => a.Count >= 4)
                .Select(a => new Seg(P(Num(a[0]) ?? 0, Num(a[1]) ?? 0), P(Num(a[2]) ?? 0, Num(a[3]) ?? 0), 0)).ToList() ?? new List<Seg>();
            List<Pt> Poly(JsonNode n) => (n as JsonArray)?.OfType<JsonArray>().Where(a => a.Count >= 2).Select(a => P(Num(a[0]) ?? 0, Num(a[1]) ?? 0)).ToList();
            double turnDeg = r.TurnDeg;
            double[] ArcOf(JsonNode n)
            {
                if (!(n is JsonArray a) || a.Count < 5) return null;
                var c = P(Num(a[0]) ?? 0, Num(a[1]) ?? 0);
                return new[] { c.X, c.Y, Num(a[2]) ?? 0, (Num(a[3]) ?? 0) + turnDeg, (Num(a[4]) ?? 0) + turnDeg };
            }

            foreach (var l in (root["levels"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var lv = new ReadingLevel
                {
                    Code = Str(l["code"]), Name = Str(l["name"]), Plan = Str(l["plan"]),
                    ElevationMm = Num(l["elevationMm"]) ?? double.NaN, CeilingMm = Num(l["ceilingMm"]) ?? double.NaN,
                    CeilingFromSurvey = l["ceilingFromSurvey"] is JsonValue cf && cf.ToString() == "true",
                    WallFaces = Segs(l["wallFaces"]), WindowLines = Segs(l["windowLines"]), DoorLines = Segs(l["doorLines"]),
                    Outline = Poly(l["outline"]),
                };
                foreach (var a in (l["wallFaceArcs"] as JsonArray) ?? new JsonArray()) if (ArcOf(a) is double[] arc) lv.WallFaceArcs.Add(arc);
                foreach (var w in (l["walls"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                {
                    var line = Segs(new JsonArray(w["line"]?.DeepClone()));
                    if (line.Count == 0) continue;
                    lv.Walls.Add(new ReadingWall
                    {
                        A = line[0].A, B = line[0].B, ThicknessMm = Num(w["thicknessMm"]) ?? 0, Confidence = Str(w["confidence"]),
                        Arc = ArcOf(w["arc"]), Curtain = w["curtain"] is JsonValue cv && cv.ToString() == "true",
                    });
                }
                void Openings(JsonNode n, bool door)
                {
                    foreach (var o in (n as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    {
                        if (!(PointOf(o) is Pt at)) continue;
                        var op = new PlanOpening { At = at, W = Num(o["widthMm"]) ?? 0, Door = door };
                        if (door) { op.Sill = 0; op.Head = Num(o["heightMm"]); op.HeightsFromSurvey = Bool(o["heightFromSurvey"]); }
                        else { op.Sill = Num(o["sillMm"]); op.Head = Num(o["headMm"]); op.HeightsFromSurvey = Bool(o["heightsFromSurvey"]); }
                        lv.Openings.Add(op);
                    }
                }
                Openings(l["windows"], false);
                Openings(l["doors"], true);
                foreach (var h in (l["heights"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                {
                    char kind = KindOf(Str(h["kind"]));
                    if (kind != '?' && PointOf(h) is Pt at && Num(h["mm"]) is double mm) lv.Heights.Add(new HeightLabel { At = at, Kind = kind, Mm = mm });
                }
                foreach (var s in (l["spotHeights"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    if (PointOf(s) is Pt at && Num(s["mm"]) is double mm) lv.SpotHeights.Add((at, mm));
                foreach (var s in (l["rooms"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    if (PointOf(s) is Pt at && Str(s["name"]) is string name) lv.Rooms.Add((at, name));
                foreach (var s in (l["ceilingNotes"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    if (PointOf(s) is Pt at && Num(s["mm"]) is double mm) lv.CeilingNotes.Add((at, mm));
                foreach (var s in (l["stairs"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    lv.Stairs.Add(new ReadingStair { Risers = (int)(Num(s["risers"]) ?? 0), Footprint = Poly(s["footprint"]) ?? new List<Pt>() });
                r.Levels.Add(lv);
            }
            foreach (var s in (root["sections"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var cut = Segs(new JsonArray(s["cut"]?.DeepClone()));
                r.Sections.Add(new ReadingSection
                {
                    Name = Str(s["name"]), File = Str(s["file"]), Note = Str(s["note"]), Cut = cut.Count > 0 ? cut[0] : (Seg?)null,
                    HeightsMm = (s["heightsMm"] as JsonArray)?.Select(Num).Where(x => x != null).Select(x => x.Value).ToList() ?? new List<double>(),
                });
            }
            foreach (var n in (root["notes"] as JsonArray) ?? new JsonArray()) if (Str(n) is string t) r.Notes.Add(t);
            return r;
        }

        /// <summary>The turn (degrees) from the reading's model axes to the project's internal axes.</summary>
        public double TurnDeg { get; private set; }

        /// <summary>
        /// Reading model mm -> project internal mm. Survey to Revit's model: survey = base + rotate(+rotation) * model.
        /// The project's: survey = position + rotate(angle) * internal (as facade_list's grid conversion). When both
        /// say the same (a project Build from Survey set up), it is the identity.
        /// </summary>
        public Func<Pt, Pt> ToProject(double ppEastMm, double ppNorthMm, double ppAngle, out string note)
        {
            double rot = RotationDeg * Math.PI / 180;
            bool samePlace = Math.Abs(ppEastMm - BaseEastMm) < 1 && Math.Abs(ppNorthMm - BaseNorthMm) < 1;
            if (samePlace && (Math.Abs(Norm(ppAngle - rot)) < 1e-4 || Math.Abs(Norm(ppAngle + rot)) < 1e-4))
            {
                TurnDeg = 0;
                note = "same frame as the project (Build from Survey set it up)";
                return p => p;
            }
            double cr = Math.Cos(rot), sr = Math.Sin(rot), ca = Math.Cos(ppAngle), sa = Math.Sin(ppAngle);
            TurnDeg = (rot - ppAngle) * 180 / Math.PI;
            note = string.Format(CultureInfo.InvariantCulture, "moved onto the project's coordinates through the survey grid ({0:0} mm east, {1:0} mm north, turned {2:0.###} deg)",
                BaseEastMm - ppEastMm, BaseNorthMm - ppNorthMm, TurnDeg);
            double be = BaseEastMm, bn = BaseNorthMm;
            return p =>
            {
                double e = be + p.X * cr - p.Y * sr - ppEastMm, n = bn + p.X * sr + p.Y * cr - ppNorthMm;
                return new Pt(e * ca + n * sa, -e * sa + n * ca, p.Z);
            };
        }

        private static double Norm(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }

        private static char KindOf(string kind)
        {
            switch (kind)
            {
                case "Sill": return 'S';
                case "Head": return 'H';
                case "OpeningHeight": return 'O';
                case "DoorHead": return 'D';
                case "Ceiling": return 'C';
                default: return '?';
            }
        }

        private static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue(out string s) && s.Length > 0 ? s : null;
        private static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue(out bool b) && b;
        private static double? Num(JsonNode n)
        {
            if (!(n is JsonValue v)) return null;
            if (v.TryGetValue(out double d)) return d;
            if (v.TryGetValue(out int i)) return i;
            if (v.TryGetValue(out long l)) return l;
            return double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : (double?)null;
        }
    }
}
