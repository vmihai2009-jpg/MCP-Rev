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
2. KEY AND SCORE 0
   facade_list (the facades and their ids), then elevation_index with the elevation DWGs (it matches each
   elevation to its facade; check the matches it reports). survey_fidelity on every floor: this is the
   baseline. Report its numbers.
3. AUTOFIX, per failing floor (one call each; a job on big floors)
   survey_autofix with levels [the floor]: walls fitted to the DWG faces, windows given their labelled sills
   and heads and their plan widths, windows the elevations draw placed, window-column gaps filled. It scores
   the floor again and returns only what still fails. dry_run first if unsure.
4. YOUR QUEUE: only the stillFailing items, worst first (each says what it was measured against and how to fix)
   wall_face / wall_thickness: cad_lines + wall_info, then set_wall_line / change_type / split_wall (a wall
   thicker at piers: the wall at the window plane, piers with model_extrusion). wall_missing: create_walls
   with the DWG line and thickness after checking it with cad_lines. opening: dwg_read near it for its labels,
   set_insert. opening_missing: place_inserts. level: set the level's Elevation, then re-score. Blocked
   windows and solid wall stay solid. survey_review_tick what Survey to Revit listed. save_model per floor.
5. FLOORS, ROOFS, OPENINGS
   Slab edges from the plan outline (create_floor with replace_id); stair and lift voids (create_opening);
   roofs from the roof plan and sections (create_roof, a slope per edge for mansards). Levels from spot
   heights (dwg_read with contains '+' or the level layer) and the sections.
6. FACADE
   dwg_elevation with facade = the id and the index's sheet_x_range, include_lines: the elevation's lines on
   the facade plane in model mm. model_sweep for cornices, string courses, copings and plinths along the
   facade line; model_extrusion for piers, pilasters, quoins and window surrounds.
7. FINAL SCORE
   survey_fidelity on every floor must pass. Report per floor: the numbers before and after, what is left for
   a person (with element ids), and every value you could not read from the drawings." + "\n" + Sections3D;

        /// <summary>1.10.0: the 3D step, kept as its own block so the workflow above can change round it.</summary>
        public const string Sections3D = @"SECTIONS, HEIGHTS AND CURVES (3D), before the final score
   section_index (cut lines and where each section sits), then survey_section: every element the section
   plane cuts against the section sheet: slab tops and thickness, wall and parapet tops, roof ridge, eaves
   and pitch, stair risers. survey_section_autofix for the height fixes that are certain (dry_run first),
   then the rest by hand: roof_form / roof_pitch with create_roof (slopes_deg from the issue) or
   roof_from_survey; slab_thickness with create_floor replace_id; section_unmatched lines are a slab, roof
   slope or parapet the model lacks, or detail beyond the cut: look before building. Curved walls are in
   survey_fidelity (wall_face_curved) and survey_autofix re-curves them.
   Look as well as measure: survey_section with image true draws the section with the model cut in red;
   check_view (a facade, a section or some elements) then view_image shows the model itself. Every
   section must pass with the floors.";

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
