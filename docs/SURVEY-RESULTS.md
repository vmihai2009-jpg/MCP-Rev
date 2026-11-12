# Survey results log

One row per run that worked (or taught something), newest first, so the next project starts from the settings that
gave good results. Method: `docs/SURVEY-TO-1TO1.md`. Pictures and JSON: `runs/<date>/`.

## 7 Swallow Place & 251-259 Regent Street (Plowman Craven, testmcp), 2026-11-12

| Step | Tool + settings | Result | Notes |
|---|---|---|---|
| Editable bands | `facade_profile` F29 (1.14.5, as sweeps) | 4 profile families, 15 native wall sweeps: shopfront cornice 821 mm out, main cornice 861, 5th-floor band 279, balustrade base 52; all start at the facade line | profile set on the type; best-fit side/flip/offset per wall |
| Pilasters | `facade_columns` F29 (1.14.4) | plan sections found (fluted pilasters 850 wide, 137 proud, flutes r 24); 2 pieces built; most fluted pilasters' heights not confirmed by the elevation (edges broken at bands) | to become loadable families |
| Phases | `phase_fix dry_run:false`; 1.14.3 PhaseKeeper | 174 elements (118 stair railings, 48 relief shapes, 8 floor steps) moved to Existing; new elements Existing automatically | API-made elements default to the latest phase |
| Mansard + bands | `facade_profile` F29 (Section-A), F53 (Section-B), F78 (Section-A) | F29: 7 parts, main cornice 861 mm out (55% of the facade), shopfront cornice 821 mm (94%), mansard 23.75-26.75 m; 0 failed | section wall face shift -440 mm on F29; height tolerance: long lines anywhere within the part's height |
| Facade relief | `facade_relief` all 6 indexed facades | F29 19,780 lines -> 1,928 faces; F93 484, F53 423, F78 157, F84 102, F64 15 | plan depth only at the cut height; base = model wall face over all spanned storeys |
| Floor steps | `survey_floor_steps apply:true min_spots:2` (1.14.1) | spots on a slab within tolerance: Basement 42->62/105, Ground 12->29/56, Fourth 37->43, Fifth 25->27 | Ground Floor slab moved +620 (commonest of 12) |
| Windows (look) | `window_family build load`; `window_look` (1.12.2) | 118 windows on 91 BG Window types + 2 arched families; 15 left (model one wide opening vs narrower drawn window, shopfront parts) | 2 refused: wall too short for the drawn width |
| Windows (size) | `survey_autofix walls:false` with reveal <= 120 mm | First 0.598->0.630, Second 0.663->0.725, Third 0.766->0.786, Fourth 0.692->0.716 | |
| Elevation heights | `survey_autofix walls:false` (1.9.16) | 111 windows to elevation sill/head | plan S/H labels are internal |

### What didn't work (don't repeat)
- DirectShapes for detail the team will edit (relief, profile shapes, columns): not editable. Use profiles + wall
  sweeps, roofs, loadable families.
- Setting WallSweepInfo.ProfileId only: the sweep keeps the type's default 152x152 profile.
- A section part 1.33 m tall (window heads + pediments at the cut bay) swept as a band: hid the pediments.
- `facade_details` on F29: section read gave 1.3-1.5 m "bands" (slab/roof); use `facade_profile`.
- Per-look window families (1.12.1): 74 families, placed from z = 0 not the template's sill plane; replaced by one
  parametric family with types.
- Plan projection applied to every face of the elevation: buried columns and pushed whole storeys out to a balcony.
- Sweeping a section part along the whole facade without the elevation check: a balcony at the cut ran 38 m.
- `survey_autofix walls:true` dry run with the 1.12.0 wall fallback: Revit crashed (journal in runs/2026-11-12).
