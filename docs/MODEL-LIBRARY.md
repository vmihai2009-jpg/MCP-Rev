# Model Library: learning from finished Revit models

The plan (Mihai, 2026-11-26): load finished, human-made Revit models (the office's real survey models; 7 Swallow Place
first), take them apart with analysis tools, and keep what they teach as defaults the hub and Survey to Revit use when
a drawing is silent. No model weights change: "training" means better defaults, recipes and tool ideas, always
overridden by the project's own drawings. Full design (plain language): https://claude.ai/artifact/Eoo38P9U9AH3x1Eepx1Lg7

Order of authority, highest first: this project's drawings > Survey to Revit's lessons from people's edits
(`DWG-Survey-to-Revit-3D/docs/LEARNING.md`) > Model Library priors > the built-in fallback in code.

## Phases

| Phase | Tools | State |
|---|---|---|
| 1. Look inside a model | `model_tools`, `model_overview`, `model_walls`, `model_openings`, `model_floors_roofs`, `model_stairs`, `model_query` | **built, 1.46.0; needs a live run** |
| 2. Facade and profiles | `model_facade`, `model_profiles`, `model_cut` | **built, 1.47.0; needs a live run** |
| 3. The library | `library_add`, `library_ask`; folder `learning\library\` beside S2R's learning folder | planned |
| 4. Pair with the survey | `model_pair` + an S2R audit per model | planned |
| 5. Use it | build tools and S2R read `adopted/priors.json`; bench gate scores wall positions | planned |

## Phase 1: how to use it

The `model_*` tools are a **learning group**: they are not in the tool list (so normal sessions don't pay tokens
for them) but can be called by name. `model_tools` (always listed) shows them with their arguments.

1. `reference_open path: "<model.rvt>" name: m1` (read-only, in the background, never saved). Or leave `document`
   out to analyse the active project.
2. `model_overview document: m1`: storeys, categories, families, whether it was generated (Survey to Revit / hub
   marks in Comments: a generated model is not a person's work and must not be learned from), and which topics are
   worth reading.
3. Per topic, statistics first: `model_walls` (thickness per use/level/type/band, curved walls, joins, build-ups),
   `model_openings` (sizes, sill/head, reveal per wall band), `model_floors_roofs` (storeys, slab build-ups, ceiling
   heights as a share of the storey, roof slopes), `model_stairs` (riser/going, reaches the floor above?),
   `model_query` for anything else (category + filters + one parameter's spread).
4. Facade (phase 2): `model_facade` (every detail per facade, classified, with height, projection and depth ratio),
   `model_profiles shape: true` (the moulding profiles as lines and arcs, height 1), `model_cut` (a vertical plane
   through a facade gives its built section, curves kept; compare with the survey section).
5. Add `list: N` to see example ids, then `element_geometry` / `family_detail` on one of them.
6. `reference_close name: m1`.

What each answer means:

- **Wall use** is read from the wall type's Function and generic words in its name (party, partition, stud,
  internal, cavity...), never from one project's names. Thin interior walls (150 mm and under) count as partitions.
  Check a surprising group with `list: 5`.
- **Reveal** = the wall's outer face to the first piece of the window/door geometry that lies inside the wall
  (more than 5 mm from either face). Architraves and sills in front of the wall are ignored. Openings in curved
  walls are skipped. On big models a spread sample of `max_reveal` (400) openings is measured.
- **Facades** are the straight external walls grouped by the plane of their outer face (same facing within 3 deg,
  faces within 400 mm), named by facing in model axes and their face line. A piece belongs to a facade when it is a
  sweep on one of its walls, or a Generic Model / column / in-place family sitting on that plane.
- **Detail kind** comes from generic words in the family/type name (cornice, surround, corbel, pilaster, string,
  sill, quoin...); with no such word it is guessed from the shape (long and low near the top = cornice, lower =
  string course, tall and narrow = pilaster, small = ornament) and counted as `guessed`.
- **Projection** = how far the piece reaches in front of the wall's outer face; **depth ratio** = projection /
  height. These are the numbers the facade builders need when no section cuts a detail.
- **Profiles** are read from the Profile families themselves (opened in memory, never saved): their lines and arcs,
  normalised to height 1 so the same moulding at any size gives the same outline.
- **Storeys** use each level's elevation to the next one up.
- **Stairs reach the floor above** when their top is within `tol_mm` (30) of the next level more than 500 mm above
  their base.

## What to record after analysing a model (Mihai, 2026-11-26)

Everything learned is written down in the same commit, so the next session starts from it:

1. **Recipes**: how the person built each kind of thing, in plain steps, in `docs/MODEL-RECIPES.md` (create it with
   the first model). Name the model by its neutral id (M0001...), not the client or address.
2. **Tool ideas**: anything no tool can build yet, or builds differently, as a backlog row in `PLAN.md` with the
   model evidence. Proposals only; nothing is built without Mihai's go-ahead.
3. **An S2R audit** (phase 4, when the model's DWGs exist): what Survey to Revit would build vs what the person built,
   ranked by cost to the 1:1 score, each marked prior / layer rule / code. Kept on the office drive; the general
   lesson goes to `DWG-Survey-to-Revit-3D/docs/LESSONS.md`.

## Privacy

Client models never leave the office: they are opened read-only on Mihai's machine and closed without saving. Only
statistics, anonymous recipes and lessons go into GitHub; numbers per model, audits and families stay on the office
drive. Never copy client names, addresses, room names, sheet titles or people's names into the repos.
