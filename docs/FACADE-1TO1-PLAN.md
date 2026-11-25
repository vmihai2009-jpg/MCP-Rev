# Facade 1:1: where the detail loses accuracy, and the tools that close the gap

Written 2026-11-25 (hub 1.39.0) from a read of the whole facade path in both repos (hub `main` at 1.38.1, Survey to
Revit at 0.33.64) and the org side's measured state on the Swallow Place demo (COORDINATION.md, Org side, 2026-11-25
later). Goal: the modelled neoclassical front matches the survey's facade DWGs, measured, not by eye. Rules that apply
to every item below: works on any survey (no building, layer or facade names; tolerances are arguments), curves stay
curves, every result an editable family / profile / native element, every write tool through PhaseKeeper.

## 1. Where we are (measured, not guessed)

`facade_correspondence` on the Swallow Place demo after `facade_detail_pass` (coverage = share of the elevation's lines
the model draws within 20 mm; precision = share of the model's edges the elevation draws):

| Facade | F24 | F45 | F33 | F61 | F72 | F66 | F13 |
|---|---|---|---|---|---|---|---|
| coverage | 0.68 | 0.53 | 0.52 | 0.52 | 0.42 | 0.38 | 0.24 |
| precision | 0.76 | 0.55 | 0.27 | 0.48 | 0.18 | 0.16 | 0.19 |

Target 0.90 on both. Low **precision** (many model edges no drawing has) is the bigger problem on most facades. And
none of these numbers says anything about **depth**: there was no check of how far anything stands out until
`facade_depth_check` (1.39.0, below).

## 2. Where 1:1 is lost (read from the code)

The path today: Survey to Revit builds walls, openings, piers and an elevation/section index; everything classical
comes from the hub: `window_family` + `window_look` (BG Window types), `facade_profile` (bands and mansard from one
section), `facade_columns` (pilasters from the plan), `facade_rustication` (reveals), `facade_types` (one family per
repeated piece), `facade_relief` (everything else, as faces), chained by `facade_detail_pass`.

| # | Loss | Where in the code | What it does to the facade |
|---|---|---|---|
| L1 | **Depth is guessed for most detail.** `facade_relief` pushes each elevation face out by what the plan shows, else by its shape: band 120, strip 80, small 50, panel 30 mm. `facade_types` builds each piece `depth_mm` (default 80) deep, "a step out for each shape inside another". | `Survey/FacadeRelief.cs` (`ReliefOptions`, lines ~110-120), `SurveyFacadeTypes.cs` | Architraves, hoods, keystones, consoles, panels: right outline face-on, invented depth, flat front. No moulding. 2610 faces on one facade (F26 dry run): spurious edges (low precision) and hard to edit. |
| L2 | **Cornice profiles are re-sampled, not read.** `facade_profile` takes the section's outermost line every 10 mm of height and simplifies it to 5 mm (Douglas-Peucker). Arcs in the section (ovolo, cyma, cavetto) become chords; fillets under 10 mm vanish; undercuts (drip, soffit throat) can't exist in an envelope. | `Survey/SectionProfile.cs` `Envelope`/`Regions`; `ProfileFamilyBuilder.Build` draws straight lines only | Every band is a faceted approximation; breaks rule 2 (curves as curves) inside the profile. |
| L3 | **One section per facade.** The section crossing nearest the facade's middle gives the profile for the whole facade; other sections crossing it are ignored. | `facade_profile`, `cands.OrderBy(...uAlong - fl/2).First()` | A cornice that changes over a bay, a breakfront, or a section through a pediment bay gives the wrong profile for the rest. |
| L4 | **Heights of profile members come from the section's fit, not the elevation.** The elevation draws every change of plane of a cornice as a horizontal line at its exact height; the section's height fit (level marks on another sheet) is coarser. | `facade_profile` uses `heights.Height(y)` from the section only | Band edges a few to tens of mm off the elevation's lines (shows in `facade_course_check`). |
| L5 | **The window surround family (spec 4.2 in FACADE-DETAIL.md) was never built.** Surrounds are `facade_relief` faces or `facade_types` extrusions of the face-on drawing. | none yet | Architraves are flat steps instead of a moulding swept round the opening; arched heads as segments. |
| L6 | **Pilasters over-found.** Any two long verticals 150-1200 apart with no window between become a pilaster (164 items, 140 types on F24); only the fluted shafts are real. | `Survey/FacadeMap.cs` `Pilasters` | Noisy types; wrong pieces placed; type ids change between runs (the map numbers them in order), so `facade_types` comments don't survive a re-run. |
| L7 | **Window depth and size guessed.** `window_look` sets the frame back `reveal_mm` 100 and swaps windows up to 150 mm off the elevation's size. The plan draws each reveal at cut height; `window_detail` already measures that inset but `window_look` doesn't use it. | `SurveyWindowTools.cs` | Sashes all at one depth; sizes within 150 mm accepted. |
| L8 | **Depth never scored.** `survey_section` (the only 3D-vs-section score) cuts walls, floors, roofs, ceilings, stairs and generic models: **wall sweeps (cornices), reveals, columns and windows are left out**, and the face-on checks project everything flat. | `SurveySectionTools.CutModel` categories | A cornice 150 mm too shallow or missing scores as right. Fixed for the facade by `facade_depth_check` (1.39.0). |
| L9 | Survey-side gaps feed the facade: missing top-storey walls (F66/F72 windows can't be hosted), the elevation overlay's mirror test refusing F13/F24/F45/F66. | Survey to Revit walls; `SurveyElevationOverlay.cs` | Coverage capped on those facades whatever the detail tools do. |

The common thread: **the elevation is exact face-on (u, z) and the section is exact in depth (out, z) at one bay; today
the tools take the outline from one and invent the other.** 1:1 needs each detail's outline from the elevation and its
profile from a drawing that shows depth, with the source of every depth recorded.

## 3. The plan, in order (each: reads -> writes -> why it raises fidelity)

### P1. `facade_depth_check` (read-only) — **built, 1.39.0**
- **Reads:** every section in `section-index.json` that crosses the facade square enough (not just the middle one);
  its outline on the profile/wall layers, placed with its own fit.
- **Writes:** nothing in Revit; `facade-depth-<F>.json`.
- **Does:** cuts the model on each section's plane, including wall sweeps, reveals, columns, windows, railings; compares
  projection from each one's own wall face every 10 mm of height (so a wall position error isn't double-counted);
  share within `tolerance_mm` (10), mean/max error, worst bands typed `missing` / `extra` / `too_deep` / `too_shallow`
  with the element that is outermost there and the fix to run.
- **Why:** without it, depth errors (L1-L4, L8) are invisible. It is the number P2-P5 are judged by. Goal 95%.

### P2. True profiles from the section's own lines: `facade_profile method: outline`
- **Reads:** the section's drawn outline as chains of lines **and arcs** (`DwgFile` already keeps ARCs, circles and
  bulged polylines as `DwgArc` since 1.21.0); the elevation's band lines over the same height.
- **Writes:** one Profile family per band whose loop is the section's own chain (arcs as `Arc`, not chords), on native
  wall sweeps as today.
- **How:** chain the outline between the band's top and bottom crossings of the wall face (`CurveChains`), close it
  along the face; then **snap** each horizontal break to the nearest elevation line within 25 mm (heights from the
  elevation, projections from the section: L4); record both sources in the family's Comments.
- **Why:** fixes L2 and L4; the profile family opens as the surveyor drew it and is easy to correct.

### P3. A profile per stretch, from every source that shows depth: `facade_profile` + `profile_sources`
- **Reads:** all sections crossing the facade (L3); the **return elevation**: the side elevation at a facade's corner
  draws the front's cornice and bands in silhouette, true depth (any survey with a return elevation has it); the plan
  at its cut height for plinths, piers and pilaster depths.
- **Writes:** sweeps split where the sources disagree by more than tolerance, each stretch with its own profile type.
- **Why:** a facade with a breakfront or a pediment bay stops getting one bay's cornice everywhere; where sources
  disagree it says so instead of averaging.

### P4. `surround_family` (FACADE-DETAIL.md 4.2, at last)
- **Reads:** a window type from `facade_detail_map` (its concentric architrave lines = the fascia steps, exact in u/z
  from the elevation; sill with horns; hood/cornice lines; pediment rakes or arc; keystone); depth of each step from, in
  order: a section that cuts a window of that type, the plan's jamb lines at cut height (architrave steps show there),
  the reference model; else the default, **flagged as guessed** in the type's Comments.
- **Writes:** one Generic Model family per design: a sweep of the stepped architrave profile round the opening path
  (straight jambs, the head as one `Arc` when arched), sill extrusion with horns, hood as a sweep, raking pediment
  sweeps + tympanum; Width/Height instance parameters, Material parameter; placed at every window of the type through
  PhaseKeeper.
- **Why:** the biggest visual difference between our facade and the hand model; replaces thousands of relief faces
  (L1, L5) with one editable family per design, which also lifts precision.

### P5. `facade_relief` only for what nothing else claims, with honest depths
- **Reads:** the map's items already built by P2-P4 / `facade_types` / `facade_columns` / `facade_rustication`.
- **Writes:** relief only for leftover faces; each face's depth source (`plan` / `section` / `reference` / `guessed`)
  in its Comments; `guessed` faces listed for a person in `review_queue` instead of extruded when `strict: true`.
- **Why:** removes the spurious edges that hold precision at 0.16-0.55 (L1); every invented number is visible.

### P6. `facade_detail_map` tighter and stable
- Pilaster only with a capital/base line or a plan projection at that u (L6); type ids from a geometry hash (size + line
  pattern), not a running number, so they are the same on every run; unclassified share reported per facade.

### P7. Windows from the plan's reveals
- `window_look` takes each window's reveal depth from `window_detail`'s plan measurement (default only when the plan
  doesn't show it), and resizes to the elevation within 10 mm rather than accepting 150 mm (L7).

### P8. Consoles, modillions, keystones in true side profile
- Their side profile is only in a section or a return elevation: when one shows it, the family is an extrusion of that
  side profile across the drawn width (front outline from the elevation as a void cut); else the face-on extrusion as
  today, flagged guessed. Same place-by-type mechanism as `facade_types`.

### P9. Survey to Revit / overlay items that cap coverage (L9)
- Top-storey walls missing under mansard storeys (S2R); elevation_overlay's mirror test (hub). Org side has both in hand.

Every write item ends with `facade_check` + `facade_depth_check` on the facade, and the run results go in COORDINATION.md.

## 4. What's already out there (GitHub and literature, 2026-11-25)

No open-source Revit MCP or add-in reads a survey's elevations and sections into classical detail; ours is ahead
there. Pieces worth knowing:

| Project | What it has | Use for us |
|---|---|---|
| HorizunGroup/horizun-revit-mcp (Apache-2.0, active) | Family authoring/editing and "DWG to BIM", in its paid tier | Ideas only for family-authoring tool shapes; nothing for profiles from CAD in the open part |
| bimwright/rvt-mcp (Apache-2.0), oakplank/RevitMCP (MIT), Demolinator/revit-mcp-server (MIT, pyRevit), LuDattilo/revit-mcp-server (MIT), RevitCortex | General model/edit tools, typed errors, toolsets | Nothing on facade detail; patterns already adopted in 1.37.0 |
| Autodesk Revit Public MCP Server (June 2026) | Official, Revit 2027+ | Not usable on Revit 2025 |
| mcneel/rhino.inside-revit (open source) | Robust curve -> Revit family / profile / sweep conversion | Reference for edge cases (arc chains in profile families, sweeps along mixed line/arc paths) |
| jeremytammik/the_building_coder_samples, tbc | Wall profile and sweep API samples | Reference for `WallSweep` / profile family calls |
| HBIM research (Murphy et al., Dublin; Politecnico di Torino/Milano theses; ISPRS archives) | Libraries of parametric classical objects (orders after Palladio/Vignola) fitted to survey data | A last-resort fallback: classical proportion rules to infer a profile's projections from the member heights the elevation gives, always flagged "inferred" |

Sources: [horizun-revit-mcp](https://github.com/HorizunGroup/horizun-revit-mcp), [rvt-mcp](https://github.com/bimwright/rvt-mcp),
[RevitMCP](https://github.com/oakplank/RevitMCP), [Demolinator/revit-mcp-server](https://github.com/Demolinator/revit-mcp-server),
[LuDattilo/revit-mcp-server](https://github.com/LuDattilo/revit-mcp-server),
[Autodesk Revit Public MCP](https://www.autodesk.com/blogs/aec/2026/06/17/revit-public-mcp-server/),
[ISPRS HBIM 2013](https://isprs-archives.copernicus.org/articles/XL-5-W1/57/2013/),
[CIPA 2021 Murphy et al.](https://iris.polito.it/bitstream/11583/2919052/1/CIPA%202021%20con%20Murphy%20et%20al..pdf).

## 5. What needs a live Revit run

Everything here compiles and the cores are tested on drawn test facades; nothing has run in Revit yet. First ask to
the org side: `facade_depth_check` on each Swallow Place / Mount Row facade with a crossing section, after
`facade_detail_pass`, and put `depthPct` per facade beside the correspondence numbers in §1. That baseline decides
whether P2 (profiles) or P4 (surrounds) moves the score more.
