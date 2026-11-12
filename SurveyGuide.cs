namespace BarrGazetas.RevitMcp
{
    /// <summary>The ready-made instructions (MCP prompts) for the survey work.</summary>
    internal static class SurveyGuide
    {
        public const string OneToOne = @"Take the survey DWGs to a Revit model that matches them 1:1. Survey to Revit builds the base; you then check
and correct every wall, window, door, floor and facade element against the DWGs. Measure, never guess: every
position, thickness and height comes from a DWG line, label or elevation. Where the drawings do not say, leave
it and list it for a person.

Rules
- Work on a copy of the project. save_model after each floor.
- Never touch any other Revit window: only this connection's model.
- No dialogs: every tool resolves Revit's warnings itself. If a fix is refused, try once another way, then list it.
- Each change is one undo step. Ask before deleting or rebuilding.
- Keep answers short; work floor by floor, lowest first, and report counts before and after.

1. BASE
   mcp_status, get_model_info. If nothing is built: survey_status, survey_analyze (the survey folder),
   survey_layers if floors look thin, then survey_build (preview, then confirm). survey_coordinates: the
   survey point should carry the survey's coordinates.
2. WALLS, per floor
   dwg_wall_fit for the floor: walls off the DWG faces, and walls the DWG has that the model lacks.
   dwg_wall_fit_apply (moderate ones). Then look at each 'review' one with cad_lines and wall_info, and fix
   with set_wall_line / change_type / split_wall (a wall that is thicker at piers: model the wall at the
   window plane, add the piers with model_extrusion). Missing walls: create_walls with the DWG line and
   thickness. dwg_wall_fit again: the count should drop.
3. WINDOWS AND DOORS, per floor
   survey_review for the floor's gaps. For each opening: cad_lines (glazing lines) for position and width,
   dwg_read near it for its labels ('S 0.94' sill, 'H 3.27' head: metres above the floor). place_inserts or
   set_insert. Blocked windows and solid wall stay solid. survey_review_tick with a note.
4. FLOORS, ROOFS, OPENINGS
   Slab edges from the plan outline (create_floor with replace_id); stair and lift voids (create_opening);
   roofs from the roof plan and sections (create_roof, a slope per edge for mansards). Levels from spot
   heights (dwg_read with contains '+' or the level layer) and the sections.
5. FACADE
   dwg_read with path = each elevation DWG, what = both: its lines give the heights of cornices, string
   courses, parapets, sills and window heads, in the drawing's coordinates (heights as drawn: usually metres
   above datum, so subtract the Ground Floor level). model_sweep for cornices, string courses, copings and
   plinths along the facade line; model_extrusion for piers, pilasters, quoins and window surrounds. Check
   each against the plan's facade line (cad_lines) so it sits on the wall face.
6. CHECK
   dwg_wall_fit and survey_review on every floor; get_warnings. Report per floor: what was fixed, what is
   left for a person (with element ids), and every value you could not read from the drawings.";

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
