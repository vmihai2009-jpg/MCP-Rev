# PLAN.md: the road to a 1:1 survey model

_A living plan. Last updated: 2026-11-12 (first version, after the first Revit run on 7 Swallow Place). Whoever finishes a step ticks it here in the same commit as the work._

**Goal.** From the surveyor's DWG plans, sections and elevations, Claude drives the MCP tools (MCP-Rev hub + Survey to Revit plugin) to produce a Revit model of the existing building that matches the survey 1:1, including windows as proper families and the neoclassical facade detail, quickly and without a person fixing it afterwards.

**What "1:1" means (the gate every floor and facade must pass).**

| Element | Pass when | Measured by |
|---|---|---|
| Wall faces | 95% of face length within 10 mm of the plan's face lines, none over 25 mm | `survey_fidelity` |
| Wall thickness | within 10 mm | `survey_fidelity` |
| Missing walls | 0 real walls missing | `survey_fidelity` |
| Openings | width, sill, head within 30 mm (elevation outside, plan jambs) | `survey_fidelity` |
| Missing openings | 0 | `survey_fidelity` (plan + elevations) |
| Levels | within 10 mm of the spot heights | `survey_fidelity` |
| Slabs, ceilings, roofs, stairs | within 20 mm on every section cut | `survey_section` |
| Facade detail | 90% of the elevation's lines within 20 mm of a model edge | **new: `facade_score` (F1 below)** |
| Windows | every window is a family type that matches its elevation drawing (pane layout, head shape) | **new: `window_types_from_survey` (W1)** |

---

## 1. Where we are (first Revit run, 2026-11-12, MCP-Rev 1.9.14-1.9.16)

0 of 10 floors pass. Scores 0.49-0.79. Walls within 10 mm: 18-54% per floor (best after autofix: Third 53.6%, Basement 38%). Levels: all within 10 mm (done). Issues: 794 wall_face, 252 wall_missing, 188 opening, 101 opening_missing, 93 wall_thickness.

**What those numbers really say** (from `runs/2026-11-12/fidelity-*.json`, read by this plan):

1. **Most wall-face misses are one systematic error, not 794 separate ones.** 459 of 794 are between 50 and 59 mm, 256 of them exactly 50 mm. 50 mm is half of Survey to Revit's default single-line wall thickness (`SingleLineWallThickness = 100`, `WallDetector.cs`): where the plan draws only one face of a wall, the plugin centres a 100 mm wall on that face line, so neither model face lands on the drawn face. 60 of the 93 thickness issues are also "model 100 mm" walls whose DWG face pair is 300-400 mm. **Fix the cause in the plugin once and roughly 60% of the wall-face issues go away.** (Hypothesis, strongly suggested by the data; confirm on the next run.)
2. **About half the "missing walls" are probably not walls.** 110 of 252 are "face pairs" more than 1 m apart and 235 are under 2 m long (e.g. "face pair 1158 mm" on the Ground Floor, "447 mm x 26.5 m" on the Fifth). The scorer pairs any two parallel lines; it must learn to ignore chimney breasts, stair edges, joinery, steps and two separate walls facing each other. Until it does, the score cannot reach 1:1 even on a perfect model.
3. **Opening misses are mostly heads read from elevations** (102 of 188), and many were windows paired with another storey's opening. The storey guard in 1.9.16 fixed the pairing; heads/sills should now converge with autofix.
4. **The 101 elevation-only windows** are real: the elevations draw windows the plans missed (or merged). They need placing (autofix does it from 1.9.16) and their types made (blocked by the "can't make type" error, see W0).
5. **Build errors** stop autofix finishing: window types at odd sizes, "Element is reversed", "Can't cut instance out of Wall", ceiling sketch broken by a moved wall. Being fixed in the thread *Fix model-build errors*.
6. **Nothing yet measures the facade detail or the window design**, so Claude cannot tell when the front facade is "right". That's why it is slow on the neoclassical facade: it has no target to aim at and no score to check against.

---

## 2. Gap by element type

| Element | Now | Target | Main blocker | Work (backlog ids) | Thread |
|---|---|---|---|---|---|
| Levels | all within 10 mm | 10 mm | none | done | - |
| Wall faces | 18-54% within 10 mm | 95% | single-line walls centred on the face (~60% of misses) | S1, S2, A1 | new: *Plan walls to 95%* |
| Wall thickness | 93 over | 0 | default 100 mm walls | S1 | same |
| Missing walls | 252 | 0 | scorer counts non-walls | V1, then S3 | same |
| Openings (size) | 188 over | 0 | heads from elevations; type creation fails | W0, A2 | *Fix model-build errors* |
| Missing openings | 101 | 0 | elevation-only windows; type creation | W0, A2 | same |
| Window design | concept panes only | real sash/casement families per type | no tool reads a window's design | W1-W4 | *Window families from DWG* (to open) |
| Slabs | one flat slab per floor | stepped where spots step; 7th slab -1.9 m | needs its first Revit run | `survey_floor_steps` (1.14.0, pull request) | *Model stepped slabs* |
| Stairs | 2 flights "missing" (7th) were a survey_stairs matching bug | all | stairs are checked, not built | fixed in 1.14.0 | *Model stepped slabs* |
| Ceilings | none placed (room lookup bug) | per section | room lookup | org side is fixing | org side |
| Roofs | 181 DirectShape pieces | roofs scored per section | not yet scored | V3 | *Extend 1:1 scoring to 3D* (done: `survey_section`) |
| Facade bands (cornices, string courses) | `facade_details` exists, untested | 1:1 on section profile | not run yet | F0 | *Capture neoclassical facade detail* |
| Facade ornament (surrounds, pilasters, quoins, consoles, balustrades, pediments, rustication) | nothing | each modelled, repeated by instancing | no detection, no profile, no score | F1-F7 | same |
| Rooms | names checked | all named | - | `place_rooms` exists | - |

---

## 3. Sequence of work (with dependencies)

Each phase ends with an org-side Revit run on 7 Swallow Place and a "Revit run results" entry in COORDINATION.md. Phases 2-4 can run in parallel once phase 0 is done.

**Phase 0: trust the score (first; everything else is measured by it)**
- V1 scorer: stop counting non-walls as missing walls. V2 scorer: report issues grouped by cause. Fix build errors (thread already running).
- Exit: the next run's issue list, read by a person, has no obvious false alarms.

**Phase 1: plan walls to 95% (depends on 0)**
- S1 single-line walls sit with one face on the drawn line, on the side the room is; thickness from the floors above/below or the type list, never a blind 100 mm. S2 snap thickness to the measured pair, not the nearest 20 mm step, when the gate is 10 mm. Then A1 autofix walls on every floor.
- Exit: 6 of 10 floors at 95% walls, the rest listed with causes.

**Phase 2: openings and window families (depends on 0; W1+ can start now)**
- W0 window-type fallback (instance sizes) so every window can be placed. A2 autofix openings everywhere. W1-W4 window families from the elevations.
- Exit: openings pass on every floor; every facade window has a type that matches its drawing.

**Phase 3: vertical (in progress in its threads)**
- Stepped slabs and missing stairs (thread running), ceilings (org side), roofs and slabs on `survey_section` (V3).
- Exit: every section passes.

**Phase 4: the facade (depends on F1 score; detection F2 can start now)**
- F0 run `facade_details` for bands; F1 facade score; F2 motif detection; F3 profiles; F4 detail families; F5 place by motif; F6 compare with the human model.
- Exit: front facade 90% of elevation lines within 20 mm; every motif either modelled or listed.

**Phase 5: speed (runs alongside, finishes last)**
- P1 one-call pipeline per floor, P2 image crops for looking, P3 the usage guide and decision recipes in COORDINATION.md.
- Exit: a fresh Claude takes Swallow Place from DWGs to passing floors in one session, with a person only answering listed questions.

---

## 4. Tool and feature backlog (the key output)

Priority: **P0** now, **P1** next, **P2** after. "Repo": S2R = Survey to Revit (reads DWGs, builds); Hub = MCP-Rev (scores, fixes, details). Rule kept from earlier work: DWG reading belongs in S2R and the hub consumes `survey_reading`; scoring and fixing belong in the hub.

### Scoring (V)
| Id | Tool / change | What it does | Repo | Pri |
|---|---|---|---|---|
| V1 | `survey_fidelity` missing-wall filter (partly done 1.14.7: ≤1 m, no line between, no face on a model wall, no room/spot height between; layer-role checks still to do) | A face pair counts as a missing wall only if it is on the wall layer, 50-900 mm apart (more only for a basement/party wall flagged by S2R), both lines longer than 300 mm, and not a chimney breast, stair, step or joinery outline (S2R layer roles). Others go to an "unclassified pairs" list, not the score. | Hub | P0 |
| V2 | issue causes (done 1.15.0, in `survey_run`) | Groups issues by likely cause before listing them: "256 walls exactly 50 mm off: single-line walls centred on their face", "40 windows: elevation head 150 mm above label". Claude fixes one cause instead of 400 items. | Hub | P0 |
| V3 | `survey_section` on roofs and slabs | Already merged (1.11.0); needs its first Revit run and the roof DirectShapes included. | Hub | P1 |
| V4 | `facade_score` | See F1. | Hub | P0 |
| V5 | score history (done 1.15.0, `survey_run` writes it) | Every scoring call appends to `runs/score-history.csv` (floor, metric, value, version), so progress and regressions are visible between versions. | Hub | P1 |

### Plan walls (S, A)
| Id | Tool / change | What it does | Repo | Pri |
|---|---|---|---|---|
| S1 | single-line walls on the face (face on the line done in S2R 0.23.1; thickness from floors above/below still to do) | One drawn line = one face. Put the wall's face on it, the body on the side away from the room label / towards the other walls; thickness from the same wall on the floors above/below (FacadeStack), else a layers.txt default per layer, else flag as low confidence. | S2R | P0 |
| S2 | thickness to the pair | Use the measured face pair (to 5 mm) for the wall type, not the nearest 20 mm step; types made on demand. | S2R | P1 |
| S3 | missed walls on the plan | Rebuild what V1 still lists after S1 (likely short piers and nibs under 1 m: lower the minimum wall length for piers between openings). | S2R | P1 |
| A1 | autofix walls finishes | Uses the build-error fixes so a refused wall is retried (unjoin, then move, then recreate) instead of stopping. | Hub | P0 (in *Fix model-build errors*) |

### Windows (W)
| Id | Tool / change | What it does | Repo | Pri |
|---|---|---|---|---|
| W0 | window-type fallback | When a type at a size can't be made, use a family with instance Width/Height (or the concept family) and say so. | Hub | P0 (in *Fix model-build errors*) |
| W1 | `window_types_from_survey` | For every window on every elevation: crop its linework, read the design (overall frame, sash split, meeting rail height, glazing bars rows x columns per sash, arched or flat head, sill projection, surround present), from the plan its reveal depth and frame position in the wall, from a section its sill profile. Cluster identical designs into types ("6-over-6 sash, flat head, 1:1.9") and return a short table with counts and the windows in each. | Hub (reads S2R's elevations) | P0 |
| W2 | `window_family_build` | From one W1 type, generate a loadable window family from Revit's window template: frame, two sliding sashes (or casements), glazing bars per the pattern, sill, optional arched head and surround; Width/Height/Sill as instance parameters, glazing bar counts as type parameters. One family per design, not per size, so the "can't make type at 4930x3900" class of errors disappears. | Hub | P0 |
| W3 | `window_swap` | Swap the placed concept windows for the W2 family type they belong to, keeping position and size; re-score openings. | Hub | P1 |
| W4 | window check against the human model | For each type, `reference_compare` with the human model's windows: same count per facade, same family split; list disagreements for a person. | Hub | P1 |

### Facade detail (F)
| Id | Tool / change | What it does | Repo | Pri |
|---|---|---|---|---|
| F0 | run `facade_details` | The bands tool exists (cornices, string courses, copings from sections + elevations). First Revit run on the front facade. | (run) | P0 |
| F1 | `facade_score` | Projects the model's facade (edges of walls, windows, details) onto the facade plane and measures each elevation line against it: % of elevation line length within 20 mm, misses grouped by height band and by motif. Returns an overlay image (elevation dark, model red). This is the target that makes facade work measurable. | Hub | P0 |
| F2 | `facade_motifs` (done as `facade_detail_map`, 1.17.0) | Finds the repeated pieces of linework on an elevation (a window surround drawn 24 times, a console drawn 40 times, a baluster drawn 120 times): groups them, gives each motif an id, a bounding box, a count, its positions, and a small picture. Claude then names each motif once (architrave, console, keystone...) instead of reading thousands of lines. | Hub | P0 |
| F3 | `detail_profile` | The profile of a detail from the best drawing that cuts it: a section DWG through it (as facade_details does for bands), else the plan (pilasters, columns, piers: plan outline), else the elevation outline with a depth from the section or a stated default (flagged). Returns a closed profile in mm, ready for a family. | Hub | P1 |
| F4 | `detail_family_build` | Generates a loadable family from a profile: sweep (cornices, architraves round an opening, string courses), extrusion (pilasters, quoins, panels, rustication blocks), revolve (balusters, column shafts, urns), blend (tapered consoles). Category per kind (Generic Models, Columns, Windows trim). Each family is editable in the family editor, so a person can refine it like an in-place family. | Hub | P1 |
| F5 | `place_by_motif` | Places a family at every position of a motif from F2 on its facade (one undo step), hosted on the wall face or free with the right offset. | Hub | P1 |
| F6 | `reference_pattern` (done inside `facade_detail_map reference:`, 1.17.0) | For a box on the facade, says how the human model built it: which families or in-place elements, category, level of detail, an element image. Claude copies the approach (and with `reference_copy`, the family itself where the human's family fits the survey). | Hub | P1 |
| F7 | rustication and coursing | Horizontal joints on the rusticated base from the elevation's joint lines: wall sweeps or reveals at each joint height. | Hub | P2 |

### Ten to 1:1 (2026-11-13, Mihai: "10 tasks/tools it would need, and implement"; all transferable to any survey)
1. `facade_course_check`: courses and window rows at the drawn heights (C3). Done 1.19.0.
2. `detail_support_check` + set-back fix in `facade_relief` (C4). Done 1.20.0.
3. `facade_check`: every facade check in one call, also in `survey_run` (C5). Done 1.22.0.
4. `facade_shift_fix`: move elements `facade_correspondence` finds shifted (dry run first, PhaseKeeper). Done 1.23.0.
5. `elevation_fit_check`: level marks per part of each elevation; parts that disagree reported, not used. Done 1.24.0 (reported; splitting the elevation is still by hand in elevation-index.json).
6. Curves, not fragments (moved up; Mihai 2026-11-13: "it fragments lines for a reason, it should also use curves... easy to be corrected by a human"): DWG reader keeps arcs, bulges, circles, ellipses, splines; chains of short lines fitted to arcs (`CurveChains`); `curve_check` + `curve_walls` replace staircases of short walls with one curved wall. Done 1.21.0. Bands carry on round curved walls as one sweep (1.21.1). Still to do: relief families on a curved stretch set to the tangent, and arcs in elevations (arches, curved pediments) as arcs.
7. Rustication as Reveal wall sweeps (F7). Done 1.25.0 (`facade_rustication`).
8. `place_by_type`: a family at every item of a `facade_detail_map` type (F5). Done 1.26.0 as `facade_types`.
9. `surround_family`: one editable family per surround type from the map (F3/F4). Done 1.26.0 inside `facade_types` (surround pieces are map types too); detail families now use true arcs where the drawing curves.
10. `fidelity_dashboard`: one 1:1 score over walls, openings, levels, sections, facades, with history.

### Facade checks (2026-11-13, docs/FACADE-CHECKS.md)
| # | Tool | What | Where | Priority |
|---|---|---|---|---|
| C1 | `facade_correspondence` (done, 1.18.0) | Model edges vs elevation lines on the facade plane: coverage (gaps), precision (spurious), shifted elements with their Δu/Δz, per map item. This is F1 `facade_score`. | Hub | P0 |
| C2 | `opening_clear_check` + opening mask in `facade_relief` (done, 1.18.0) | Rays through every window: what blocks the glass; drawn openings with no or wrong model window. Relief clipped to outside the drawn openings. | Hub | P0 |
| C3 | `facade_course_check` (done, 1.19.0) | Cornices, string courses, sill/head rows at the elevation's heights, run by run; storey offsets and bad height fits named. | Hub | P1 |
| C4 | `detail_support_check` (done, 1.20.0) | Detail floating in front of nothing (dormers on the facade plane) or buried in walls. | Hub | P1 |
| C5 | `facade_check` (done, 1.22.0) | Runs C1-C4, one score per facade; ends every facade build step and `survey_run`. | Hub | P1 |

Note on "model in place": the Revit API cannot create in-place families, so tools cannot make them. The equivalent the tools can make is a generated loadable family per detail (F4), which a person opens and edits exactly like an in-place one and which repeats across the facade. Very irregular one-offs (a carved pediment) stay as DirectShapes or are listed for a person.

### Speed (P)
| Id | Tool / change | What it does | Repo | Pri |
|---|---|---|---|---|
| P1 | `survey_run` (done 1.15.0; walls off by default until the wall-fix crash is fixed) | One call per floor (a background job): score, autofix walls, autofix openings, re-score, save; returns only the remaining causes (V2). Claude spends its time on the leftovers, not on driving 15 tools per floor. | Hub | P1 |
| P2 | `dwg_image` (done 1.15.0) | A picture of any part of a DWG sheet (a window, a bay of the elevation, a section detail) with the level marks, cropped and scaled, so Claude looks before it measures. Pairs with `check_view` + `view_image` for the model. | Hub | P0 |
| P3 | usage guide + recipes | COORDINATION.md tells the org-side Claude which tool for which question, in order, with "if you see X do Y" recipes. Being written in the thread *Write MCP usage guide*. | docs | P0 |
| P4 | answers that fit | Every tool answers with counts and the worst 10 items by default, details in a JSON file in `runs/`, so a call never floods Claude's context. Audit the tools that still return raw linework. | Hub | P1 |

---

## 5. Using the human-made Swallow Place model as a benchmark

The survey DWGs are the truth for positions and sizes; the human model is the truth for **how to model** (which categories, families, level of detail, what to leave out). Use it like this:

1. **Line it up once.** `reference_open` it, `reference_compare` with `align auto` (shift voting + ICP, already built). Record the transform in `runs/` so every later comparison reuses it.
2. **Second score, never the gate.** After each phase, `reference_compare` per category (walls, windows, doors, floors, roofs, generic models) gives right / off / missed / invented. Where the human model and the survey disagree, the survey wins; the disagreement is listed for Mihai (the human model may be simplified).
3. **Pattern library for the facade.** `reference_inventory` + `family_detail` list how each kind of detail was built (e.g. "architraves: one profile sweep family; consoles: loadable Generic Model; rustication: wall sweeps"). Save that as `runs/reference-patterns.json`; F6 serves it to Claude per facade box. Claude should follow the human's approach unless the survey shows it is wrong.
4. **Reuse what fits.** Where a human family matches the survey's motif within tolerance (F2 vs the reference element's box), `reference_copy` the family instead of generating one; then place it by motif (F5).
5. **Teach by comparison, not by reading.** For a facade bay, show Claude three pictures side by side: the elevation crop (`dwg_image`), the human model (`view_image` on the reference), our model. This is far faster than asking it to understand thousands of lines.

---

## 6. How Claude gets it right and fast

Why the org-side Claude is slow today: it reads raw linework, has 85+ tools without a map, has no score for the facade, and fixes issues one by one when hundreds share one cause.

The rules this plan builds towards:

1. **Tools do geometry; Claude decides.** Every measurement, fit, profile and placement is done inside a tool. Claude's job is choosing, naming motifs, and judging pictures. Never ask Claude to compute coordinates from listed lines.
2. **Score first, always.** Every step starts and ends with a score (`survey_fidelity`, `survey_section`, `facade_score`). No score, no work.
3. **Fix causes, not items.** Read the grouped causes (V2). If 50 issues share a cause, fix the cause (a layer rule, a plugin setting, a rebuild of the floor), never 50 hand edits.
4. **Model one, repeat many.** Identify a motif or window type once, build it once, place it everywhere it repeats (W1-W3, F2-F5).
5. **Look, then measure.** For anything visual, `dwg_image` of the drawing and `view_image` of the model before and after.
6. **Batch and jobs.** Many changes in one `batch` (one undo step); long floors as background jobs; `save_model` after each floor.
7. **A fixed order.** The workflow prompt `survey_to_one_to_one` (SurveyGuide.cs) is the single script; the usage guide in COORDINATION.md is its plain-language map. When the plan changes, both change in the same commit.

---

## 7. Threads and owners (projects side)

| Workstream | Owns backlog ids | Status |
|---|---|---|
| Fix model-build errors | A1, W0 (and the reversed-wall, door-cut, ceiling-sketch errors) | running |
| Model stepped slabs from spot heights | stepped slabs, 7th floor slab, missing stairs | pull request open (1.14.0), waiting for a Revit run |
| Capture neoclassical facade detail from DWG | F0-F7 | running |
| Write MCP usage guide for work Claude | P3 (COORDINATION.md guide) | running |
| Extend 1:1 scoring to 3D | V3 (done, merged 1.11.0) | waiting for a Revit run |
| **Proposed: Plan walls to 95%** | V1, V2, S1-S3 | to open (highest value: about 60% of wall misses) |
| **Proposed: Window families from DWG** | W1-W4 | to open |
| Add speed tools for faster runs | P1, P2, V2, V5 (P4 still open) | done in 1.15.0, awaiting first Revit run |
| Org side (Mihai's work account) | live Revit runs; ceilings room lookup | after each merge |

## 8. What the org side should run next (for COORDINATION.md)

1. On main 1.11.x + S2R default branch: `survey_reading`, `survey_fidelity`, then save the JSON in `runs/`. Check: are the 50 mm wall misses on walls built from a single line? (Pick three: `wall_info` + `cad_lines` near them.)
2. `survey_section` with `image: true` on Sections A-C; `facade_details` dry run on the front facade.
3. `reference_open` the human model, `reference_compare` (align auto) and `reference_inventory`; commit both JSONs to `runs/`. These seed the benchmark (section 5).

## Change log
- 2026-11-12: first version.
