// Survey to Revit's reading (survey_reading, schema survey-reading/1) loaded into the project, and the 1:1 score's
// checks that need it: windows read on the plan but not modelled, every label style, room names, ceiling notes.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class ReadingTests
    {
        const double E = 530000000, N = 180000000;

        // a 10 x 6 m room, walls 200 thick on the lines, as Survey to Revit writes it (model mm)
        static JsonObject Reading(double rotationDeg = 0) => JsonNode.Parse(@"{
          ""schema"": ""survey-reading/1"", ""folder"": ""C:/survey"", ""layerFile"": null,
          ""frame"": { ""baseEastMm"": " + E + @", ""baseNorthMm"": " + N + @", ""rotationDeg"": " + rotationDeg.ToString(System.Globalization.CultureInfo.InvariantCulture) + @" },
          ""levels"": [ {
            ""code"": ""00"", ""name"": ""Ground Floor"", ""elevationMm"": 26470, ""ceilingMm"": 2600, ""ceilingFromSurvey"": true, ""plan"": ""C:/survey/GF.dwg"",
            ""wallFaces"": [ [0,-100,10000,-100], [0,100,10000,100], [10100,0,10100,6000], [9900,0,9900,6000],
                             [10000,6100,0,6100], [10000,5900,0,5900], [-100,6000,-100,0], [100,6000,100,0] ],
            ""wallFaceArcs"": [ [5000,3000,1000,0,90] ],
            ""windowLines"": [ [4500,-100,4500,100], [5500,-100,5500,100] ],
            ""doorLines"": [],
            ""walls"": [ { ""line"": [0,0,10000,0], ""thicknessMm"": 200, ""confidence"": ""High"" } ],
            ""windows"": [ { ""x"": 5000, ""y"": 0, ""widthMm"": 1000, ""sillMm"": 940, ""headMm"": 2400, ""heightsFromSurvey"": true, ""wall"": 0 },
                           { ""x"": 2000, ""y"": 6000, ""widthMm"": 900, ""sillMm"": 900, ""headMm"": 2100, ""heightsFromSurvey"": false, ""wall"": 2 } ],
            ""doors"": [],
            ""heights"": [ { ""x"": 5000, ""y"": 500, ""kind"": ""Sill"", ""mm"": 900 }, { ""x"": 5000, ""y"": 600, ""kind"": ""OpeningHeight"", ""mm"": 1500 } ],
            ""spotHeights"": [ { ""x"": 3000, ""y"": 3000, ""mm"": 26470, ""text"": ""+26.47"" }, { ""x"": 6000, ""y"": 3000, ""mm"": 26470, ""text"": ""+26.47"" } ],
            ""rooms"": [ { ""x"": 2500, ""y"": 3000, ""name"": ""Kitchen"" }, { ""x"": 7500, ""y"": 3000, ""name"": ""Dining Room"" } ],
            ""ceilingNotes"": [ { ""x"": 2500, ""y"": 2500, ""mm"": 2600 } ],
            ""stairs"": [ { ""risers"": 14, ""footprint"": [[0,0],[1000,0],[1000,3000]] } ]
          } ],
          ""sections"": [ { ""name"": ""A"", ""cut"": [0,3000,10000,3000], ""heightsMm"": [26470, 29600], ""file"": ""S.dwg"", ""note"": ""ok"" } ]
        }").AsObject();

        public static void Run()
        {
            Console.WriteLine("SurveyReading");
            var r = SurveyReading.Parse(Reading(), E, N, 0);
            var gf = r.Level("Ground Floor");
            T.Check(gf != null && r.Level("00") == gf && gf.WallFaces.Count == 8 && gf.Openings.Count == 2 && gf.Rooms.Count == 2 && gf.Stairs[0].Risers == 14, "the reading's floor, by name or code, with its faces, windows, rooms and stairs");
            T.Check(r.FrameNote.StartsWith("same frame") && T.Near(gf.WallFaces[0].A.Y, -100) && T.Near(gf.WallFaceArcs[0][2], 1000), "a project Build from Survey set up: nothing moves");
            T.Check(gf.Heights.Count == 2 && gf.Heights[1].Kind == 'O' && r.Sections[0].Cut.HasValue && r.Sections[0].HeightsMm.Count == 2, "height notes by kind; sections with their cut");

            var shifted = SurveyReading.Parse(Reading(), E + 1000, N - 500, 0);
            T.Check(!shifted.FrameNote.StartsWith("same") && T.Near(shifted.Level("00").WallFaces[0].A.X, -1000) && T.Near(shifted.Level("00").WallFaces[0].A.Y, 400), "a project with its own coordinates: the reading moves onto them");
            var turned = SurveyReading.Parse(Reading(10), E, N, 0);
            var w = turned.Level("00").Openings[0].At; // (5000, 0) in a frame turned 10 deg
            T.Check(T.Near(w.X, 5000 * Math.Cos(10 * Math.PI / 180), 0.01) && T.Near(w.Y, 5000 * Math.Sin(10 * Math.PI / 180), 0.01) && T.Near(turned.TurnDeg, 10, 1e-9), "and turns with it");
            bool refused = false;
            try { SurveyReading.Parse(JsonNode.Parse("{\"schema\":\"survey-reading/2\"}").AsObject(), 0, 0, 0); } catch (System.IO.InvalidDataException) { refused = true; }
            T.Check(refused, "a newer schema is refused with a reason, not misread");

            // sill + sill-to-head ("FS 0.90" / "SH 1.50" on older London surveys): the head is their sum
            var lab = Fidelity.LabelsFor(new Pt(5000, 0), gf.Heights);
            T.Check(lab.sill == 900 && lab.head == 2400, "a sill and a sill-to-head height give the head");

            // the score against the reading: the window on the first wall is modelled with a 940 sill (label: 900);
            // the one on the far wall is missing; Kitchen is a room, Dining Room is not; the ceiling is 2550 (note 2600)
            var walls = new List<FitWall>();
            double[][] room = { new double[] { 0, 0, 10000, 0 }, new double[] { 10000, 0, 10000, 6000 }, new double[] { 10000, 6000, 0, 6000 }, new double[] { 0, 6000, 0, 0 } };
            for (int i = 0; i < 4; i++) walls.Add(new FitWall { Id = 100 + i, A = new Pt(room[i][0], room[i][1]), B = new Pt(room[i][2], room[i][3]), ThicknessMm = 200 });
            var input = new FidelityInput
            {
                Level = "Ground Floor", LevelZ = 0, DatumMm = 26470, Walls = walls, WallFaces = gf.WallFaces, OpeningLines = gf.WindowLines,
                Heights = gf.Heights, PlanOpenings = gf.Openings, SpotHeights = gf.SpotHeights,
                Rooms = new List<RoomCheck> { new RoomCheck { At = new Pt(2500, 3000), Survey = "Kitchen", RoomId = 9, ModelName = "KITCHEN" }, new RoomCheck { At = new Pt(7500, 3000), Survey = "Dining Room" } },
                Ceilings = new List<CeilingCheck> { new CeilingCheck { At = new Pt(2500, 2500), SurveyMm = 2600, Id = 11, ModelMm = 2550 } },
            };
            input.Inserts.Add(new FidInsert { Id = 7, Category = "Windows", HostWallId = 100, At = new Pt(5000, 0), W = 1000, Sill = 940, Head = 2400 });
            var (s, issues) = Fidelity.Measure(input, new FidelityGate());
            T.Check(s.Openings.LabelOver == 1 && issues.Any(i => i.Kind == "opening" && i.Id == 7 && T.Near(i.DevMm, 40, 1)), "the sill is measured against Survey to Revit's label reading");
            T.Check(s.Openings.MissingOnPlan == 1 && s.Openings.Missing == 1 && issues.Any(i => i.Kind == "opening_missing" && i.At[0] == 2000) && s.FailedOn.Contains("missing openings"), "a window read on the plan but not modelled is missing");
            T.Check(s.Rooms.Checked == 2 && s.Rooms.Missing == 1 && s.Rooms.Misnamed == 0 && s.FailedOn.Contains("rooms"), "rooms: names match ignoring case; one with no room fails");
            T.Check(s.Ceilings.Over == 1 && s.Ceilings.MaxMm == 50 && issues.Any(i => i.Kind == "ceiling" && i.Id == 11) && s.FailedOn.Contains("ceilings"), "a ceiling 50 mm under its note fails");
            T.Check(s.Walls.Checked == 4 && T.Near(s.Walls.WithinTolPct, 100) && s.LevelCheck.Checked && s.LevelCheck.DevMm == 0, "walls and level against the reading's faces and spot heights");

            // without Survey to Revit the hub's own S/H text reading still works
            var old = new FidelityInput { Level = "L", Walls = walls, WallFaces = gf.WallFaces };
            old.Labels.Add(new DwgText { Value = "S 0.94", At = new Pt(0, 500) });
            T.Check(Fidelity.LabelsFor(new Pt(0, 0), old).sill == 940 && Fidelity.LabelsOf(old).Count == 1, "the hub's own labels when there is no reading");
        }
    }
}
