# Neoclassical facade detail from the survey DWGs: the method and the tools

How Claude goes from a surveyor's elevations, sections and plans to an editable Revit model of a classical facade
(cornices, string courses, rustication, architraves, sills, hoods, pediments, keystones, consoles, pilasters, quoins,
balustrades), fast and right. Written for 7 Swallow Place / 251-259 Regent Street (Plowman Craven survey), meant for
any survey. Order of work and lessons: `docs/SURVEY-TO-1TO1.md` step 5. Tool catalogue: `COORDINATION.md` Part 1.

## 1. The decision: editable families, not DirectShapes

Everything built for the facade must be editable by an architect in Revit (Mihai's rule, rule 6 in
`docs/SURVEY-TO-1TO1.md`). So:

| What | Revit element | Tool |
|---|---|---|
| Continuous bands (cornice, string course, plinth, balustrade base) | Profile family on a **wall sweep** type | `facade_profile` |
| Mansard | Basic Roof (extrusion roof) | `facade_profile` |
| Columns, pilasters | Loadable family per shape | `facade_columns` |
| Windows | One parametric family (BG Window), a type per survey window | `window_family`, `window_look` |
| Surround, sill, hood, pediment, keystone, consoles of one window design | Loadable Generic Model family per **design**, placed at every window of it | `facade_relief` (today), surround family from the map's type (next, 4.2) |
| Repeated small pieces (console, modillion, baluster, quoin block) | Loadable family placed many times | `place_family` (+ `reference_copy types_only` when the human model's family fits) |
| Rustication joints | Wall sweep of type Reveal | to build (4.4) |

Revit's API cannot create in-place families. A generated loadable family is the editable equivalent: it opens in the
family editor like an in-place one, and it repeats. `model_sweep`, `model_extrusion` and `facade_details` make
DirectShapes: for checking only, never in a model to hand over. Carved one-offs no family can capture are listed for a
person.

## 2. How each detail appears in the survey DWGs

An elevation shows width and height but **no depth**; a section shows depth but only at one bay; a plan shows depth
only at its cut height (about 1 m above the floor). Every detail needs two drawings.

| Detail | Elevation (u, z) | Section (depth profile) | Plan (depth at cut) | How reliably the map finds it |
|---|---|---|---|---|
| Cornice / entablature | Several long horizontal lines close together at the top, one per change of plane | The full profile (corona, cymatium, dentils as steps) | Roof plan edge | High: long, regular |
| String course, plinth | One or two long horizontals | Small profile | Plinth shows on ground plan | High |
| Rustication | Horizontal joint lines at a steady pitch over the base storeys | V or square grooves | No | High when joints run past the windows |
| Architrave | Concentric lines round the opening (each line a fascia step) | Only if the section cuts a window | A step in the wall face beside the opening | High for 2+ steps, medium for flat |
| Sill | Line(s) under the opening running past both jambs (horns) | Sill projection | No | High |
| Hood / window cornice | Two+ horizontals over the head, wider than the surround | If cut | No | High |
| Pediment | Two sloped lines meeting over the middle (triangular), or many short pieces (segmental, arcs come in as chords) | If cut | No | High triangular, medium curved |
| Keystone | Small shape centred on the head | No | No | Medium |
| Consoles / modillions | Small alike shapes under a hood's ends or under a band, repeated | Rarely | No | Medium |
| Pilaster | Two long verticals 150-1200 apart with no window between, capital/base lines at the ends | No | **Yes**: the shaft's section in front of the wall | High |
| Quoins | Short horizontals from the corner, alternating long and short | No | Corner step if proud | High |
| Balustrade | Many alike small upright shapes at a steady pitch, rails over and under | Base profile | Balcony front | High |

Plowman Craven's layers on this job: windows on `Ss_25_30_95-*`, stonework on `EF_25_*` wall layers, levels on
`Zz_60_45` / `*Level*`, sections' cut outline on `*Profile*`. The map reports every layer it saw and what it did with it
(windows / detail / ignored), so a new surveyor's layer names show up at once.

## 3. The method, detail by detail

1. **Read** (`facade_detail_map facade: F.. image: true`): every item named, with its box on the facade (u along from
   the left end seen from outside, z above datum and model mm), its measurements, and its **type** (alike items
   share one). Look at the picture before trusting the list.
2. **Depth** for each type, in this order: the section that crosses the facade (`facade_profile` reads it), the plan
   at that storey (pilasters, piers, architrave steps), the human model (`facade_detail_map ... reference: <name>`
   gives each item's projection from the reference), else the typical values the map quotes. Say which source was used.
3. **Build** one per type, editable (table in 1), then place it at every item of the type.
4. **Check** with `facade_detail_map ... reference: active` (which items have nothing built there yet), `view_image`
   beside the elevation, `survey_section` for the bands.

Where the map is unsure (curved pediments, keystones, consoles, anything "unclassified") it is a **proposal**: Claude
looks at the picture, confirms or corrects in its answer, and only then builds. Unclassified shapes stay in
`facade_relief` as faces until someone names them.

## 4. Tools

### 4.1 Done
- `facade_detail_map` (1.17.0, this file's tool): reads an elevation into named items and types (Revit-free core
  `Survey/FacadeMap.cs`, tests in `tests/SurveyCore/FacadeMapTests.cs`); `reference` matches each item to the human
  model's elements (family, in-place, projection) and lists what the person modelled that the map missed;
  `reference: active` does the same against our own model; writes `facade-map-<F>.json` and a PNG.
- `facade_profile`, `facade_columns`, `facade_relief`, `window_family`, `window_look` (org side, 1.12-1.14.6).

### 4.2 Next: `surround_family` (map type to one family)
- In: facade, a type id from the map (e.g. T3), depth source (`section` | `plan` | `reference` | mm).
- Does: a Generic Model family with a sweep along the opening's outline using the type's architrave steps as the
  profile (step offsets from the map, projections from the depth source), the sill as an extrusion with its horns, the
  hood as a sweep of its line heights, the pediment's raking sweeps and tympanum, the keystone; Width/Height
  parameters so one family fits every window of the type; Material parameter. Places one at every window of the type.
- Replaces `facade_relief`'s surround faces for those windows.

### 4.3 Next: `facade_score` (PLAN.md F1, the measurable target)
- Projects the model's edges on the facade plane and measures each elevation line against them: share of elevation
  line length within 20 mm of a model edge, per storey and per map item (so the score says "T3 architraves: 92%").
  Overlay image. Goal: 90% per facade.

### 4.4 Next: rustication reveals
- From the map's rustication item (joint heights, runs): a Reveal wall sweep type with a 20 x 20 groove Profile family
  and one sweep per joint height on the facade's walls.

### 4.5 Next: `place_by_type`
- Places a family at every item of a map type (consoles, modillions, balusters, quoin blocks), at the item's u/z,
  offset to the wall face, one undo step.

### 4.6 Small reader fix
- `DwgFile.Read` turns arcs into chords but ignores polyline bulges, circles, ellipses and splines. Moulded profiles
  and curved pediments are often drawn that way: read them as short chords too (needed for curved pediments and
  section profiles).

## 5. Using the human-made model

- **Learn the technique**: `reference_inventory`, `family_detail` (what `docs/SURVEY-TO-1TO1.md` already records:
  in-place sweeps for cornices, `EF_25_10-Wall_Ext_Detail_NN` extrusion families for pilasters, surrounds and
  consoles, railings with square/round balusters).
- **Get the depths**: `facade_detail_map ... reference: <name>` gives every item's projection as modelled by hand.
- **Find what we miss**: its `notInMap` list = detail the person modelled that the map didn't name: read those boxes on
  the elevation and tell the projects side which shapes the map should learn.
- **Never copy positions or sizes**: those come from the survey; reuse a family only where its size fits the type.

## 6. Limits to know

- The map has been tested on a drawn test facade only, not yet on the real elevations: the first Revit run should
  report, per facade, the item counts, the unclassified share and anything named wrongly (with the u/z box).
- It reads one elevation at a time; a band that turns a corner is two items on two facades.
- Layers matching `*Profile*` are ignored on elevations (the outline/ground line), like `facade_details` does.
