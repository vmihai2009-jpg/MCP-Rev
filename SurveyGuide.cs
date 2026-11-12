namespace BarrGazetas.RevitMcp
{
    /// <summary>The ready-made instructions (MCP prompts) for the survey work.</summary>
    internal static class SurveyGuide
    {
        public const string OneToOne = @"Take the survey DWGs to a Revit model that matches them 1:1. This is the one workflow for the survey: Survey
to Revit reads the DWGs and builds the base; the hub then measures every floor against that same reading
(survey_fidelity) and fixes it (survey_autofix) until it passes. Measure, never guess: every position,
thickness and height comes from a DWG line, label, section or elevation. Where the drawings do not say, leave
it and list it for a person.

Rules
- Work on a copy of the project. save_model after each floor.
- Never touch any other Revit window: only this connection's model.
- No dialogs: every tool resolves Revit's warnings itself. If a fix is refused, try once another way, then list it.
- Each change is one undo step. Ask before deleting or rebuilding, and before survey_build / survey_layer_rules
  with confirm.
- Keep answers short; work floor by floor, lowest first, and report counts before and after.
- One reader: the survey is read by Survey to Revit (its layer roles, the survey's layers.txt, every sill/head
  label style it knows). Do not use dwg_build_model for a survey Survey to Revit can read.

1. READ THE SURVEY
   mcp_status, survey_status (no project: ask for one). Ask for the survey DWG folder if you do not have it.
   survey_analyze. Drawings skipped, floors with no height, or few walls on a floor: survey_layers with
   only_unassigned; if busy layers have role Other, propose a layers.txt (survey_layer_rules) and ask before
   writing it. Analyse again. Say in a few lines: floors and heights, walls (how many low confidence), windows,
   doors, rooms, sections, the checks.
2. BUILD THE BASE
   survey_build without confirm shows the plan; build after the user says yes (confirm: true). Report warnings
   and what Revit refused. survey_coordinates: the survey point should carry the survey's coordinates.
3. KEY AND SCORE 0
   facade_list (the facades and their ids), then elevation_index with the elevation DWGs (check the matches it
   reports). survey_fidelity on every floor: the baseline. It reads the plans through Survey to Revit
   (survey_reading, done for you; its answer says which reader was used) and measures walls, thickness,
   openings (plan jambs, labels, elevations, and windows/doors read on the plan but missing), room names,
   ceiling notes and the level. A hand-made model of the building linked in: add against_reference (its name)
   for Survey to Revit's right/off/missed/invented count in the same answer. Report the numbers.
4. AUTOFIX, per failing floor (one call each; a job on big floors)
   survey_autofix with levels [the floor]: walls fitted to the plan's faces, windows given their labelled sills
   and heads and their plan widths, windows the plan or the elevations show placed, window-column gaps filled.
   It scores the floor again and returns only what still fails (forReview: what it would not do on its own).
   dry_run first if unsure.
5. YOUR QUEUE: survey_review (Survey to Revit's doubts) and the stillFailing items, worst first
   survey_review item: survey_review_context, decide, fix (survey_wall for a missed wall), survey_review_tick with
   a note. Many of one kind on a floor is a layer or reading problem: fix the cause (layers.txt) and rebuild that
   floor (survey_build with levels) rather than patching by hand.
   wall_face / wall_thickness: cad_lines + wall_info, then set_wall_line / change_type / split_wall (a wall
   thicker at piers: the wall at the window plane, piers with model_extrusion). wall_missing: survey_wall or
   create_walls with the DWG line and thickness after checking it with cad_lines. opening: dwg_read near it for
   its labels, set_insert. opening_missing: place_inserts (doors: pick the family and swing). room_missing /
   room_name: a room (room separation lines if the walls do not close it) / its Name. ceiling / ceiling_missing:
   the ceiling's height offset, or a ceiling. level: the level's Elevation, then re-score. Blocked windows and
   solid wall stay solid. save_model per floor.
6. FLOORS, CEILINGS, STAIRS, ROOFS
   survey_floor_levels (slabs against the spot heights), section_index then survey_ceilings (ceilings against the
   sections), survey_stairs, plan_columns, roof_from_survey (roofs from the roof plan and sections; create_roof
   with a slope per edge for mansards), window_detail (window depth in the wall). Slab edges from the plan outline
   (create_floor with replace_id); stair and lift voids (create_opening).
7. FACADE
   facade_details for cornices, string courses, copings and plinths from the sections and elevations;
   dwg_elevation with facade = the id and the index's sheet_x_range, include_lines, for anything else (model_sweep,
   model_extrusion, place_family for pilasters, surrounds, quoins).
8. FINAL SCORE
   survey_fidelity on every floor must pass. Report per floor: the numbers before and after, what is left for
   a person (with element ids), and every value you could not read from the drawings.";

        public const string ConfigGuide = @"BUILD FROM A LINKED DWG BY LAYER RULES
1. survey_coordinates: check base points; keep the model near the internal origin.
2. Link the DWG (Insert > Link CAD; by shared coordinates or centre-to-centre; correct units).
3. dwg_analyze: the layers, and a SUGGESTED config when you pass none.
4. Edit the config: one rule per group of layers; ignore the ones not to model.
5. dwg_build_model (dry_run true, the default): a real run that is rolled back; fix every failure it reports.
6. dwg_build_model (dry_run false): builds. Safe to re-run: only what is missing is added.
7. survey_qa_check: missing, moved, wrong size, duplicated or stale against the DWG.
8. DWG changed? Run 6 again (add_missing), or mode rebuild to start again; then 7.

ACTIONS
 wall_double_line : pairs of parallel face lines -> one wall on the centre line, thickness measured (minThicknessMm..maxThicknessMm,
                    at least minOverlapMm alongside each other). Wall types are found or made per thickness.
 wall_centerline  : every segment is a wall of thicknessMm.
 floor            : closed polylines, plus loose lines that close a loop (joinLooseLines). Bow-ties and tiny areas are skipped and reported.
 topo_points / topo_contours : one Toposolid per rule, thinned to maxPoints; flat-Z and Z-typo warnings.
 grid             : lines of 0.5 m or more.
 point_family     : POINT entities and blocks -> family instances (familyName/typeName, or familyMap per block name).
 ignore           : left out on purpose (not reported as unmapped).

THINGS IT TELLS YOU RATHER THAN GUESSING
 FAR_FROM_ORIGIN, EXTENTS_TOO_LARGE, DUPLICATES, UNMAPPED_LAYER, UNPAIRED_WALL_LINES, DANGLING_LINES, COMPLEX_JUNCTIONS,
 SELF_INTERSECTING, TOPO_FLAT, TOPO_Z_OUTLIERS, TOPO_THINNED, NO_FAMILY_FOR_POINTS.

LIMITS
 - Arcs and splines become short straight pieces (curved walls are approximated).
 - Walls and floors ignore DWG Z and sit on their level; topography and grids keep real coordinates.
 - Text is not in Revit's CAD link: read spot heights and labels with dwg_read.
 - Elements are tagged in Comments (""DWG-MCP|...""): leave it, or re-runs and QA will not recognise them.

EXAMPLE CONFIG
{
  ""tolerances"": { ""snapMm"": 5, ""minSegmentMm"": 10, ""closeGapMm"": 50, ""collinearDeg"": 0.25 },
  ""levels"": [ { ""name"": ""Level 0"", ""elevationMm"": 0 } ],
  ""defaultLevel"": ""Level 0"",
  ""rules"": [
    { ""name"": ""ignore_annotation"", ""match"": [""*TEXT*"", ""*DIM*"", ""*HATCH*"", ""DEFPOINTS""], ""action"": ""ignore"", ""priority"": 100 },
    { ""name"": ""walls"", ""match"": [""A-WALL*""], ""exclude"": [""*DEMO*""], ""action"": ""wall_double_line"", ""heightMm"": 2800, ""minThicknessMm"": 80, ""maxThicknessMm"": 500 },
    { ""name"": ""partitions"", ""match"": [""A-PART""], ""action"": ""wall_centerline"", ""thicknessMm"": 100, ""heightMm"": 2800 },
    { ""name"": ""slab"", ""match"": [""A-SLAB*"", ""*FOOTPRINT*""], ""action"": ""floor"", ""typeName"": ""Generic 150mm"", ""joinLooseLines"": true },
    { ""name"": ""terrain"", ""match"": [""C-TOPO-SPOT""], ""action"": ""topo_points"", ""topoCellMm"": 500, ""maxPoints"": 30000 },
    { ""name"": ""grids"", ""match"": [""S-GRID""], ""action"": ""grid"" },
    { ""name"": ""site_objects"", ""match"": [""L-TREE""], ""action"": ""point_family"", ""familyMap"": [ { ""block"": ""TREE*"", ""familyName"": ""M_Tree"", ""typeName"": ""Deciduous"" } ] }
  ]
}";
    }
}
