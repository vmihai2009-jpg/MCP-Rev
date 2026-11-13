# Facade checks: finding blocked windows, wrong heights and stray detail without looking at pictures

Why this exists: on 2026-11-13 Mihai found two faults in a 3D view of 7 Swallow Place (F29, after `facade_relief`,
`facade_profile`, `facade_columns`, `window_look`): **windows covered by solid stone panels**, and **detail at the
top right sitting at the wrong height**. Both were found by eye. The tools must find faults like these themselves, on
every run, as numbers with element ids, by comparing the model with the survey DWGs.

The principle: **every modelled facade element must match lines in the survey elevation, and every elevation line must
be matched by the model.** A model edge with no drawn line nearby is wrong (spurious, shifted or blocking); a drawn
line with no model edge is a gap. Everything below is a special case of that, made specific enough to name the cause
and the fix.

## 0. Every check works on any project

Mihai's rule: everything the MCP does must transfer to every survey, not just 7 Swallow Place. So the checks and fixes:
- read only what any survey has: the model, the facades from `facade_list`, the elevations and sections from the
  indexes, layer roles from Survey to Revit's layer rules (and the job's `layers.txt`), never hard-coded layer or
  family names;
- take tolerances and limits as arguments with defaults stated in mm, and report the values they used;
- recognise our own generated elements by their Comments tag (`MCP|<tool>|...`), not by family names;
- are tested on drawn test facades that are not this building, and say in their answer when a facade had too little
  in its drawings to check (no elevation, no level marks) rather than guessing.
Swallow Place is the first test case, not the design target.

## 1. What went wrong in the picture, and why (from the code, 1.14.6-1.17.0)

### A. Windows blocked by relief panels
`facade_relief` turns the elevation's stonework lines into faces and extrudes each one out of the wall. It leaves out
"the glass" by skipping a face whose **centre** falls inside a **model window's** box (`Survey/FacadeRelief.cs`,
`Build`: `windows.Any(w => cu > w.u0 + 20 && ...)`; the boxes come from the model's windows in
`SurveyFacadeReliefTools.cs`, stage "windows"). The elevation draws each opening's reveal on a wall layer, so the
opening itself is a closed face of about 1-3 m2, under the 4 m2 "plain wall" limit. It survives as a 30 mm "panel"
in front of the glass whenever:
1. the model has **no window there** (101 windows were drawn only on the elevations; 1.16.0 places them now, but
   relief built before that kept the panels), or
2. the model window is **narrower, lower or shifted** so the drawn opening's centre falls outside it (the window-size
   fixes ran after or alongside the relief), or
3. the opening is **split** into several faces by a transom, a sash line or an arch drawn on a wall layer: each part's
   centre is tested on its own, and parts near the edges escape, or
4. a surround face **overlaps** the opening's edge: only centres are tested, so a face that is half over the glass is
   kept whole.

The model window is the wrong mask: the **drawn opening** is the truth, and nothing may sit in front of it.

### B. Heights wrong at the top right
Three things in the current code can put detail at the wrong height or in the wrong plane there; which one it is needs
the run asked for in COORDINATION.md (ask A2):
1. **Dormers and set-back storeys built on the facade plane.** Faces up to 1.8 m above the facade's highest wall top
   are kept as "dormers", and every face is pushed out from the wall face at its own height. A dormer or an attic
   storey stands back in the mansard, so its front, pediment and balustrade end up hanging on the facade plane, in
   front of and below where they are. `wallTop` is the highest wall along the whole facade, so on a facade whose blocks
   have different heights (here two buildings, 7 Swallow Place and 251-259 Regent Street, side by side) the lower
   block's roof-level detail is kept too.
2. **One height fit for the whole elevation.** The elevation's heights come from its level marks, fitted once per
   drawing. If the right-hand part of the drawing belongs to another drawing on the sheet, or to another facade run
   (Elevation 4, the canted corner, was left out of the index because it spans several runs), its lines get this
   facade's fit and land at the wrong place.
3. **Storey families on the wrong level.** Relief is grouped into one family per storey and placed on the level found
   for the group's middle height (`LevelAt`). Faces of a lower block grouped with a higher storey are right only if
   the level and the offsets agree; nothing checks it after placing.

None of these is reported today: the tools say how many faces they made, not whether those faces sit where the
elevation draws them.

## 2. The checks to build (read-only; fixes go through the building tools)

All four answer in the same shape: per facade, a score, then issues with `kind`, element ids, the map item they
relate to (`facade_detail_map` ids), the offset in mm (u along, z height, v depth), and a `fix` line naming the tool to
run. They write `facade-check-<F>.json` in the reports folder and, with `image: true`, one overlay picture.

### 2.1 `opening_clear_check` (Error A) — built, 1.18.0
- **In:** facade (or all), tolerance (default 10 mm), image.
- **How:** for every window and door on the facade, the clear opening (the family's width x height at its sill, in the
  glass plane). Cast a grid of rays (default 8 x 8) from 2 m outside straight in (Revit `ReferenceIntersector` in a 3D
  view). The first thing hit must be the window itself (or nothing). A ray that hits a Generic Model, a wall sweep,
  a DirectShape or a wall first is blocked. Also compare each model opening with the **drawn** opening on the elevation
  (`FacadeMap` window boxes and reveals): missing window, narrower/shorter/shifted by more than 30 mm.
- **Out:** per window: share of the opening blocked, the blocking element ids and family names, the blocked box; per
  drawn opening with no model window: its box. Score = openings fully clear / openings drawn.
- **Fix it points to:** rebuild that facade's relief after the opening-mask fix below; `place_inserts` /
  `survey_autofix` for missing or wrong windows.

### 2.2 `facade_course_check` (Error B, horizontal)
- **In:** facade, tolerance (default 20 mm).
- **How:** the courses are the elevation's bands, read by `facade_detail_map` (cornice, string courses, plinth, sill
  and head lines of each storey's window row). For each course, the model elements that carry it along the facade
  (wall sweeps "BG F.. z", relief families, profile pieces, window sills and heads in that row): the height of their
  horizontal edges against the course, run by run along u.
- **Out:** per course: the share of its length the model carries within tolerance, and every stretch that is off, with
  its Δz and element ids. A stretch off by about a storey height (2.5-4.5 m) is labelled "storey offset" (wrong level or
  wrong height fit); a whole facade part off by the same Δz is labelled "fit" (the elevation's height fit is wrong for
  that part). Windows in one drawn row whose sill or head differs from the row by more than tolerance, when the
  elevation draws them level, are listed too.

### 2.3 `facade_correspondence` (both, and everything else: PLAN.md F1 `facade_score`) — built, 1.18.0
- **In:** facade, tolerance (default 20 mm), image.
- **How:** the model's visible edges near the facade (solids of walls, windows, doors, Generic Models, wall sweeps,
  columns, roofs within 1.5 m in front of and 3 m behind the facade line) projected onto the facade plane in the same
  u/z frame as the elevation; the elevation's lines in that frame (window layers and stonework layers). Each is
  matched to the other within the tolerance, direction aware.
- **Out:**
  - **coverage**: share of the elevation's line length with a model edge within tolerance (gaps);
  - **precision**: share of the model's edge length with an elevation line within tolerance (spurious detail);
  - per element: unmatched length and the **best shift** (the median offset to the nearest elevation lines). An
    element whose edges all match after one shift is "shifted" (Δu, Δz given: a storey-sized Δz is the top-right fault);
    one that matches nothing within 300 mm is "spurious";
  - per `facade_detail_map` item: covered or not (an item with no model edge is a gap);
  - image: elevation in grey, matched model edges green, unmatched model edges red, uncovered elevation lines blue.
- **Target:** coverage ≥ 90% and precision ≥ 90% per facade (PLAN.md goal).

### 2.4 `detail_support_check` (detail in the wrong plane) — built, 1.20.0
- **In:** facade.
- **How:** every detail piece (relief family solids, wall sweeps, columns) must sit on something: a short ray from
  its back face inward must hit a wall or roof within 50 mm, and its front must not be behind the wall face (buried).
- **Out:** "floating" pieces (a dormer front hung on the facade plane in front of a set-back mansard; a balustrade
  in front of nothing) with the gap in mm; "buried" pieces with their depth inside the wall.

### 2.5 One call: `facade_check`
Runs 2.1-2.4 for a facade and returns one line per check plus the worst 20 issues; writes the JSON and the picture.
**Every facade build step ends with it** (COORDINATION.md 1.5 e step 5), and its numbers go into the run results, so
nobody has to look at screenshots to know what is wrong.

## 3. Fixes to the building tools these checks call for

1. **Opening mask in `facade_relief`** (Error A; done in 1.18.0, `FacadeRelief.OutsideOpenings`): the mask is the union of the drawn openings (window-layer clusters
   and their reveals from the elevation, `FacadeMap` window boxes) and the model's windows, grown by 10 mm. A face is
   dropped if most of it is inside the mask, and **clipped** to outside the mask if it overlaps it; never extruded over
   an opening. Surround faces keep their outer part.
2. **Set-back detail** (Error B.1; done in 1.20.0, `FacadeRelief.TopProfile`/`AboveOwnWall`): faces above a block's own wall top (per u stretch, not the facade's highest) are
   not placed on the facade plane: they are listed as "roof-level detail" for the dormer/mansard step, which places
   them on the roof surface behind (the plane is found by casting inward to the mansard roof).
3. **Height fit per drawing part** (Error B.2): `elevation_index` and the relief use the level marks nearest each part
   of the drawing; a part whose fit disagrees with the rest by more than 50 mm is reported, not used.
4. Every write stays in the survey's phase (`PhaseKeeper`), as all write tools do since 1.14.3.

## 4. Order of building (projects side)

1. `facade_correspondence` core (Revit-free matching in `Survey/FacadeMatch.cs`, tests) + the Revit edge reader;
   it is the general one, and it measures everything else.
2. `opening_clear_check` and the opening-mask fix in `facade_relief` (Error A is the most visible).
3. `facade_course_check` (reuses `FacadeMap` bands and the matcher).
4. `detail_support_check`, then the set-back fix.
5. `facade_check` wrapper; add it to `survey_run`.
