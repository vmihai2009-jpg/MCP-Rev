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

6. **Everything you create must be editable** (Mihai, twice). Native Revit only: Profile families on wall sweeps,
   roofs as roofs, windows/surrounds/pilasters as loadable families (plain extrusions or sweeps you can open and
   edit, a Material parameter), types for sizes. **No DirectShapes in a model handed over.** Since 1.14.6 the facade
   tools are editable by default: `facade_relief` = a surround family per window design + a family per storey,
   `facade_profile` = profile families + wall sweeps + an extrusion roof for the mansard, `facade_columns` = a family
   per pilaster/pier. `as: shapes` exists only for checking. Older tools still making DirectShapes (`roof_from_survey`,
   `model_sweep`, `model_extrusion`, `facade_details`): don't use them on a model to hand over until converted.

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

**0. `facade_detail_map facade: F.. image: true` first (1.16.0, changes nothing).** It names what the elevation
draws (bands, rustication, architraves, sills, hoods, pediments, keystones, consoles, pilasters, quoins,
balustrades, modillions, leftovers), groups repeats into types (one family per type), and says how to build each and
where its depth comes from. With `reference: <name>` it lists, per item, how the hand model built it and how far it
projects; with `reference: active`, what our model has built there (items with nothing are still to do). Method and
what each detail looks like in the DWG: `docs/FACADE-DETAIL.md`.

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

**c. Editable bands (1.14.5): `facade_profile` builds wall sweeps by default.** Each continuous band's surveyed
profile becomes a Profile family (`reports\profiles\BG profile F.. z.rfa`, from Metric Profile.rft), a wall sweep type
"BG F.. z" (duplicated from any wall sweep type; **the profile must be set on the type**, `WALL_SWEEP_PROFILE_PARAM`,
the one passed with a new sweep is not kept) and native wall sweeps on the facade's walls at the band's height above
each wall's base. Placement is tried both sides, flipped or not, offset to the facade line, and the one that runs
from the facade line out to the profile's projection kept (walls are drawn either way round). Only **continuous**
bands become sweeps (drawn along >= 50% and <= 1 m tall, or >= 90%): a wall sweep runs a whole wall, so local pieces
at the section's bay (pediments, balconies) would spread along it. `facade_relief` then leaves out its long band
faces there (pediments, dentils, consoles at those heights stay).

**d. Vertical pieces from the plan (1.14.4, `facade_columns`).** What the storey plan draws in front of the facade
(here fluted pilasters, drawn with their flutes as small arcs) is the shaft's exact section: extruded between the
heights the elevation draws its edges. Lessons: ignore the wall-face lines within the closing tolerance (the model's
face can sit 27 mm off the line); measure edges at the piece's front face (its outline includes the wall nibs).
Still DirectShape: to become a loadable column family (next).

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

### Construction techniques read from the reference (family_detail on its families), to reproduce from the survey
| Element | How the reference builds it | How we build it from the survey alone |
|---|---|---|
| Giant column (`EF_25_10-Wall_Ext_Detail_01`) | Blend for the 9.8 m tapered shaft (entasis), Revolutions for the round base and capital, Extrusions for flutes/details | **Revolve the elevation's silhouette** about the column's axis (the axis from the plan's circle; a round thing's outline in elevation is its profile): gives base mouldings, entasis, capital bell. Flutes as grooves round the shaft. |
| Capital (`..._66`) | Revolution (bell) + small Sweeps (volutes) | Revolved silhouette; volutes stay relief (elevation faces) |
| Window surround / architrave (`..._63`, `..._53`) | Sweeps of a moulding profile along the opening's outline (path 2.7 m = a surround's perimeter), Extrusions for consoles and panels | **Sweep a stepped moulding** along the outline the elevation draws round the window (concentric lines = the fasciae; steps outward to the plan's nib depth) |
| Pediment (`..._34`) | Extrusions + a Sweep along the raking/segmental line | Sweep the cornice moulding along the pediment's outline from the elevation |
| Cornices / string courses | In-place sweeps along the facades ("External cornice 1-15") | `facade_profile` (section profile, elevation runs); continue round corners with mitres |
| Mansard, roofs | Basic roofs (`EF_30_10-Roof_H60` x47) | `facade_profile` mansard skin; flat roofs from the roof plan (`roof_from_survey`) |
| Balconies, balustrades | Railings (`Pr_25_30_36-Railing_Type_31/34`, `Baluster_Square`), round balusters | Railings along the balcony fronts read from the plan/elevation (to do) |
| Dormers | Dormer roofs + walls | Box from the elevation's dormer outline back into the mansard slope, its roof from the pediment line (to do) |

## Known gaps (next)
- Dormers: fronts only (relief); cheeks and roofs back to the mansard not built yet.
- Sills, surrounds and pediments are relief extrusions; rationalise into swept profiles with true depths (the
  sections and the reference model show how).
- Wall fixes (`survey_autofix openings: false`) crashed Revit once in the 1.12.0 fallback; reported, not run since.
