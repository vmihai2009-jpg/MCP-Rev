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
- Each change is one undo step; a correction that needs several calls goes in one batch (one undo step, all
  undone if one fails). Ask before deleting or rebuilding, and before survey_build / survey_layer_rules
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
   survey_floor_levels (slabs against the spot heights), survey_floor_steps (stepped floors: preview, then apply),
   section_index then survey_ceilings (ceilings against the
   sections), survey_stairs, plan_columns, roof_from_survey (roofs from the roof plan and sections; create_roof
   with a slope per edge for mansards), window_detail (window depth in the wall). Slab edges from the plan outline
   (create_floor with replace_id); stair and lift voids (create_opening). Rooms the build left unnamed or missed:
   dwg_read for the label, then place_rooms at it. The ground: dwg_terrain on the site plan (dry run first: check
   the dropped levels), holes = the building footprint, then toposolid_subdivide for paving.
7. FACADE: call facade_guide first and follow it (the method, the sources each number comes from, photos,
   the order of tools, the checks and their targets). In short: facade_detail_map to name what the elevation
   draws, confirmed against site photos; facade_detail_pass; then facade_check and facade_depth_check on every
   facade. Editable only: families, profiles on wall sweeps, roofs.
8. FINAL SCORE
   survey_fidelity on every floor must pass. Report per floor: the numbers before and after, what is left for
   a person (with element ids), and every value you could not read from the drawings." + "\n" + Sections3D;

        /// <summary>1.11.0: the 3D step, kept as its own block so the workflow above can change round it.</summary>
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
 - Arcs and splines become short straight pieces in the plan reading; curve_check finds curves built as runs of short walls and curve_walls rebuilds them as one curved wall (1.21.0).
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

        /// <summary>1.40.0: the facade method, as one text Claude reads before any facade detail (tool facade_guide, prompt
        /// facade_one_to_one). Mihai: "write something in the plugin so it knows how to do this stuff the best way possible".
        /// Keep it in step with docs/FACADE-1TO1-PLAN.md and docs/FACADE-LESSONS.md.</summary>
        public const string Facade = @"FACADE DETAIL 1:1 TO THE SURVEY DWGs: THE METHOD
Goal: the modelled facade matches the survey's elevations face-on AND its sections in depth, every element
editable by an architect. Targets per facade: facade_check coverage 0.90+ and facade_depth_check depthPct 95+.
Works for any building: nothing here names a building, layer or facade.

A. WHERE EACH NUMBER COMES FROM (use the first source that shows it; say which one you used)
   Outline along and up (u, z), and the height of every moulding's edge: the ELEVATION. Each horizontal line
     in a cornice is a change of plane at that exact height.
   Projection and profile (how far out, what curve): the SECTIONS that cross the facade (all of them, not one:
     a section shows depth only at its own bay); then the RETURN ELEVATION at a corner (it draws the front's
     cornice and bands in silhouette); then the PLAN at its cut height (pilasters, piers, reveals, architrave
     steps beside openings); then the reference model if one is open.
   What a thing IS and where it applies (moulding or flat strip, real pilaster or panel joint, stone or slate,
     which fronts carry the mansard, balconies, dormers, cartouches): SITE PHOTOS or Street View.
   Last resort only: a default or classical proportion. Mark it guessed (Comments ...|depth:guessed) and list it
     for a person. Never take a size from a photo: perspective makes it wrong.

B. PHOTOS AND STREET VIEW (before building any facade detail)
   1. Ask the user for photos if there are none in the survey folder (photos\ beside the DWGs; never in a
      public repo). Useful set: each facade straight on; each corner from close by looking along the building
      (the cornice shows its outline); one close-up per window design, one from the side; the roofline; anything
      carved. Street View screenshots are fine: several positions per street, zoomed; taken by hand.
   2. Look at them (view the image files) beside facade_detail_map facade: F.. image: true.
   3. For each type the map lists, say which photo confirms it and what it really is. Map items the photos
      contradict (e.g. pairs of lines called pilasters that are joints) are not built. Things the photos show
      that the map missed are listed with their box on the elevation.
   4. Note the photos' date limits: shopfronts and signs change; where photo and DWG disagree on size or
      position the DWG wins, on what a thing is the photo wins.
   5. Record each decision (photo file, facade line in model mm, item box, what it is, where it applies) in the
      run's notes and in docs/FACADE-LESSONS.md if it teaches a rule, so the next run does not ask again.
      Identify a facade by its line, not its id: facade ids change when facade_list runs again.

C. THE ORDER OF WORK
   1. facade_list, elevation_index (every elevation on the sheet), section_index. survey_autofix openings.
   2. Photos (B). facade_detail_map per facade: plain brick or render fronts get no stone detail.
   3. facade_detail_pass dry_run: true, read its log, then run it.
   4. Per facade: facade_check (face-on) and facade_depth_check (depth). Look: facade_correspondence image: true
      and dwg_image on any bay that looks wrong; element_geometry when parameters and picture disagree.
   5. Fix by the issue's kind, then check again. Report coverage, precision and depthPct per facade; say
      ""unchecked"" where no section crosses a facade (depth unknown), never a guess.

D. HOW EACH DETAIL IS BUILT (editable: what an architect would draw)
   Cornices, string courses, plinths: a Profile family on native wall sweeps; the profile from the section's own
     lines with arcs kept as arcs, its heights checked against the elevation's lines. A cornice that changes
     along the facade is several sweeps, each with its own profile.
   Pilasters and columns: facade_columns, only for real shafts (tall, often fluted, confirmed by a photo); capital
     and base from the elevation's line heights.
   Window surrounds (architrave, sill with horns, hood, pediment, keystone): one family per window design placed
     at every window of it; the architrave a stepped profile swept round the opening, an arched head one arc.
   Repeated pieces (consoles, modillions, balusters, quoin blocks): facade_types, one family placed many times;
     side profile from a section or return elevation when one shows it.
   Rustication: facade_rustication (reveals). Mansard: mansard_roof; dormers sit on the slope, not the facade plane.
   Windows: window_look; reveal depth from the plan (window_detail), not a default.
   Relief (facade_relief) only for what nothing above covers, and never over an opening.
   Curves stay curves: one curved wall or sweep, never a run of short pieces (curve_check, curve_walls).
   Never DirectShapes in a model to hand over. Every write stays in the survey's phase.

E. WHAT HAS GONE WRONG BEFORE (docs/FACADE-LESSONS.md has the full list; add to it when you find a new one)
   facade_shift_fix can slide a window a whole bay on a repeating front: look at a moved element with dwg_image.
   Plain rear walls got carved-stone relief: classify the facade first. detail_support_check ""floating"" verdicts
   can be false: check with element_geometry before deleting. Type numbers from facade_detail_map change every run:
   don't name families by them. Face-on scores say nothing about depth: always run facade_depth_check.";
    }
}
