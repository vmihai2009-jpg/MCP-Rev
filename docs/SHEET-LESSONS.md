# Sheet and view lessons (hub)

Mistakes and methods from sheet work (plans on sheets, viewports, scope boxes). Same rule as `FACADE-LESSONS.md`: every fix adds its row in the same commit.

| # | Seen | Cause | Rule | Where | State |
|---|------|-------|------|-------|-------|
| 1 | 2026-11-26, Swallow Place GAs turned 180: every viewport title landed below the sheet edge | A viewport title keeps its offset from the box outline, and the outline turns with the view | After turning a view, put the title back: record `GetLabelOutline()` before and move `LabelOffset` by the difference after, or copy the title position from a sheet already drawn the way the client wants (`title_like_sheet`) | `rotate_plan_views` 1.44.1 | fixed |
| 2 | Same run: grid bubbles ended up bottom/right | Turning the view swaps which end of each grid is at the top/left | GA plans have bubbles top and left only: per grid in each view, show the end that is highest (up-down grids) or leftmost (across grids) in the view's own Up/Right directions, and hide the other | `rotate_plan_views bubbles: top_left` | fixed |
| 3 | Same run: rotating the shared "GA Plans" scope box would also turn SK sheets and other views | A scope box is shared by every view on it | Turn a copy (`<box> 180`) and give it only to the chosen plans; list the views left on the old box. `in_place: true` only when every view on the box should turn | `rotate_plan_views` | method |
| 4 | Same run: plans with no scope box sat 79 mm off the others | Their own crop regions differed from the GA scope box | Give un-boxed GA plans the same turned scope box, then position every viewport by one model point (the box centre) so the floors stack in exported PDFs | `rotate_plan_views assign_unboxed, stack` | method |
| 5 | Same run: the north point did not turn with the plans | It is a symbol in a legend ("Scale and Arrow") placed on many sheets, including sheets whose plan is not turned (SK20.202, "Original Orientation") | Never turn the shared legend: duplicate it with detailing as `<legend> 180`, turn only the north symbol (family name contains "North") about its own point, and replace the legend viewport on the turned sheets only, at the same box centre and viewport type | `rotate_plan_views legend` 1.44.3 | fixed |

## Procedure: turn GA plans for a client

1. `list_views view_type: DrawingSheet` and query `OST_Views` with `Scope Box`, `Sheet Number`, `View Template`: which plans use which box; look for sheets already drawn the client's way (e.g. "Original Orientation") and exclude them.
2. `rotate_plan_views sheet_prefixes [...] exclude_sheets [...] title_like_sheet <that sheet> legend <north point legend>` dry run; read `plans`, `left_alone`, `grid_bubbles.failed`, `legend.symbols_turned` (must be 1 or more).
3. Run with `dry_run: false`, then `view_image` two or three sheets and both legends: plan turned, grids at the same sheet spot on every sheet, bubbles top/left, title on the sheet, north point turned.
4. Do not save; the person checks first.
