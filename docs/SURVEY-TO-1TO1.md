# Survey to a 1:1 Revit model: the method that works

How to take a measured survey (plans, sections, elevations as DWG) to a Revit model that matches it 1:1, including a
high-LOD facade, with the hub's MCP tools. Written from 7 Swallow Place / 251-259 Regent Street (Plowman Craven survey,
2026-11-12), where this went from a raw Survey to Revit build to a facade that reads like the survey elevation. Follow
it in order on the next survey; results and the settings that worked are logged in `docs/SURVEY-RESULTS.md`.

The operating guide for every tool is Part 1 of `COORDINATION.md`. This file is the **order of work and the lessons**.

## Rules that are not optional

1. **Everything is Existing.** A survey model is the existing building. Since 1.14.3 every write tool puts what it adds
   into the survey's phase ("Existing", else the phase most walls are in) by itself (`PhaseKeeper`). On a model built
   before, run `phase_fix` (dry run, then `dry_run: false`). Never leave survey geometry in "New Construction": every
   phased view and schedule will be wrong.
2. **Save after every change.** `save_model` after each tool that changed the model. Revit can crash (it did, losing an
   afternoon); nothing is kept that isn't saved.
3. **Cross-check against all elevations.** A plan or a section shows one cut; the elevations show the whole facade. Any
   height, width or detail read from one drawing is checked against the elevations before it is built (the tools do
   this: `survey_autofix` takes sill/head from the elevation, `window_look` reads windows from it, `facade_profile`
   sweeps a section's band only where the elevation draws it).
4. **Learn from a reference model, never copy it.** A hand-made model of the same building (opened with
   `reference_open`) shows how things are built (families, sweeps, profiles, depths). Use it to decide the technique;
   take every position and size from the survey DWGs. Write what you learnt in this file.
5. **Look, then measure.** `check_view` + `view_image` (facade elevation and a 3D box) before and after each step;
   compare with the survey elevation rendered from the DWG.

## Order of work

### 1. Read and key the drawings
`mcp_status` -> `survey_analyze` (Survey to Revit) -> `survey_build` (or open the build) -> `facade_list` ->
`elevation_index` (check every elevation matched, jambs matched >= 6) -> `section_index`. Without the indexes nothing
below can cross-check.

### 2. Windows to the elevation (sizes)
`survey_autofix walls: false` (openings only). Lessons:
- The plan's S/H labels are measured **inside** (window board, internal head); the elevation draws the outside. The
  elevation decides sill and head (1.9.16).
- An elevation's reveal is the outermost wall-layer line **within 120 mm** of the glass. Lines further out are the
  stone surround (here 156-197 mm out); taking them read ~30 windows 300 mm too wide (1.12.2).
- Two jambs make one opening only when the window's lines run across between them (else it's a pier between two
  windows) (1.12.1).
- A change over 600 mm is trusted only when the drawn opening is also the window's width (within 150 mm).
- Survey to Revit's `Windows_Sgl_Plain` can't take some sizes (wide > ~4.9 m, 765x2942...): don't fight it, step 3
  replaces the family.

### 3. Windows that look like the elevation
`window_family action: build load: true` once (generates the parametric **BG Window**: frame, bottom rail, meeting
rail, 1-6 panes across, 1-3 high per sash, glazing bars, glass, inset from the wall's outside face; flex-tested), then
`window_look` (dry run, then `dry_run: false`). Each window becomes a **type** of the one family, values read from the
panes the elevation draws. Only a different design (arched heads) gets its own family. Lessons:
- Width from the plan where the glass fits in it; the elevation's width (and a slide to its centre) where the glass is
  wider than the model's window; the drawn height where the sill agrees.
- Windows a wall can't take (too short) are skipped and named, one transaction each.

### 4. Floors that step
`survey_floor_steps` preview (pictures), then `apply: true, min_spots: 2`. Single-spot steps are listed, not built.
Lessons (1.14.1): every spot counts for the slab height; rooms only shape the steps when they hold >= 75% of the spots;
each spot belongs to the slab whose top is nearest it; a slab moves only on >= 3 spots (>= 5 for > 300 mm).

### 5. The facade: relief from the elevation, profiles from the sections
This is what makes the model look like the survey. Do it per facade that has an elevation.

**a. `facade_relief facade: F..`** (dry run, then build). The elevation draws stonework as loose overlapping lines
(columns, flutes, capitals, bases, pilasters, surrounds, pediments, consoles, balustrades, dormers). Split where they
cross they give faces exactly as the drawing reads; each face is pushed out from the **model's own outer wall face**
(the outermost over every storey it spans, so columns aren't buried), by:
- the storey plan's projection in front of the facade, **only for faces that cross the plan's cut height** (level +
  1 m): columns, pilasters, piers. (Using it for every face pushed everything above a balcony out to the balcony.)
- else by shape: bands 120, tall strips 80, small pieces 50, panels 30 mm.
- left out: the plain wall (faces > 4 m2), the glass, anything above the facade's wall top + 1.8 m (roof, chimneys;
  dormers up to 1.8 m above the wall top are kept).

**b. `facade_profile facade: F..`** (dry run, then build). The survey section that crosses the facade gives the true
profile of cornices, bands, balustrade base and the mansard. The section's outermost line at every 10 mm is taken
relative to the **section's own wall face** (the longest vertical run near the facade line; its fit along the cut can
be out by a reveal), and each part is swept along the facade **only where the elevation draws long horizontal lines
within its height** (a section cuts one bay: a pediment or balcony there must not run the whole facade). Below ground
and parts more than 1.3 m proud are left out. From the facade's wall top up, the outline with a 250 mm skin is the
mansard (Roofs category). A facade with no elevation is refused.

### 6. Score and log
`survey_fidelity` per floor, `survey_section image: true`, then add a row to `docs/SURVEY-RESULTS.md`.

## How the hand-made reference model was built (7 Swallow Place, "MCP Check")
Learnt with `reference_open`, `reference_inventory`, `family_detail`, `element_geometry`:
- Cornices and string courses: in-place sweeps along the facades (profile per band).
- Pilasters, surrounds, consoles: `EF_25_10-Wall_Ext_Detail_NN` families, fixed extrusions per shape with width/height/
  depth parameters.
- Shopfront columns: in-place "column detail".
- Windows: `Ss_25_30_95` sash families (Inset, H_Mullion, V_Mullion), plus many `Windows_Sgl_Plain`.
- Roof: basic roofs, dormer roofs, `EF_30_10-Roof_Chimney` pots.
Our equivalents: profiles swept (`facade_profile`, `model_sweep`), relief from the elevation (`facade_relief`),
parametric BG Window types, mansard from the section. Revit's API can't make in-place families; DirectShapes stand in.

## Known gaps (next)
- Dormers: fronts only (relief); cheeks and roofs back to the mansard not built yet.
- Sills, surrounds and pediments are relief extrusions; rationalise into swept profiles with true depths (the
  sections and the reference model show how).
- Wall fixes (`survey_autofix openings: false`) crashed Revit once in the 1.12.0 fallback; reported, not run since.
