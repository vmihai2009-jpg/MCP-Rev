# Barr Gazetas MCP: Claude in Revit 2025

A Revit add-in that lets Claude read and work on the Revit session open on your computer. It adds an **MCP** panel
to the **Barr Gazetas** tab with three buttons:

- **MCP** switches the connection on (green) and off. Nothing is reachable while it is off.
- **Connect** shows how to connect Claude (ready to copy), the options, the tools on offer and the latest calls.
- **Update** installs a newer release from its zip, with Revit open.

It is also the **hub** for the other Barr Gazetas add-ins: any add-in can offer its own tools to Claude through
it. Survey to Revit does (once its MCP files are merged), so Claude can run a survey build and work through the
review list.

## Install

Close Revit, unzip, double-click `install.cmd`. No admin rights needed. Start Revit and choose *Always Load* if asked.
`uninstall.cmd` removes it. From source it builds first (needs the .NET 8 SDK and Revit 2025).

`install.cmd` is needed once. After that:

## Updates

1. Save the new release zip (`BGMCP-x.y.z.zip`) in Downloads. No need to unzip it.
2. In Revit, the button reads **Update ready**. Click it, then **Install**.
3. Restart Revit when convenient. Until then the old version keeps working.

- Each version goes into a folder of its own (`BGMCP\1.2.0\`) and `BGMCP.addin` is switched to it, so the file
  Revit has open is never overwritten.
- The zip is checked first: it must hold `BGMCP.Revit.dll`, and the version inside must match its name. Otherwise
  nothing changes.
- The version before is kept. To go back, choose its zip with **Update > Choose a zip**; older folders are tidied
  away when Revit starts.
- To share releases with a team, set the folder in **Connect > Updates** to a shared folder, and untick the
  start-up check if it is slow to reach.

To make a release: raise `<Version>` in `BGMCP.csproj`, then run `package.ps1`. It writes `dist\BGMCP-x.y.z.zip`.

## Connect Claude (once)

Click **MCP** in Revit, then **Connect** and copy from the window. Or:

Claude Code:

```bash
claude mcp add --transport http revit http://localhost:48884/mcp
```

Claude Desktop (`Settings > Developer > Edit Config`, then restart Claude; needs Node.js):

```json
{
  "mcpServers": {
    "revit": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://localhost:48884/mcp"]
    }
  }
}
```

Use `localhost`, not `127.0.0.1`. To check it is on, open http://localhost:48884/ in a browser: it shows a small status page.

## What Claude can do

General Revit tools in this add-in (38). `*` changes the model: one undo step each, refused in read-only mode.

| Tool | What it does |
|---|---|
| `mcp_status` | What the connection offers: add-ins, read-only, running jobs. Claude starts here |
| `job_status` | The result of a long call that was handed back as a job |
| `get_model_info` | Project, active view, levels, phases, worksets, units, element counts |
| `get_selection`, `select_elements`, `isolate_elements` | See and show things in Revit |
| `query_elements` | Elements of a category, filtered by level, view or parameter text; can return named parameters in the same call |
| `get_elements`, `get_element_parameters` | Detail by id: position, size, phase, workset, every parameter |
| `set_parameters`* | Set values on one or many elements in one undo step |
| `delete_elements`* | Delete, after a preview and a confirm |
| `set_wall_line`*, `move_elements`*, `change_type`* | Repairs: a wall's line, moving things, swapping types |
| `list_levels`, `list_views`, `open_view` | Levels, views and sheets |
| `list_types`, `list_rooms` | Types with usage counts; rooms with areas |
| `list_schedules`, `get_schedule_data` | Read schedules as Revit shows them |
| `get_warnings`, `list_links` | Model health: warnings grouped, links and CAD files |

### Building to match a survey (1.4.0)

Survey to Revit makes the base model. These finish it by hand, one to one with the survey: what the reading missed or
got wrong is placed, reshaped or modelled directly. All are `*` (change the model), one undo step each.

| Tool | What it does |
|---|---|
| `list_cad`, `cad_lines` | The survey DWGs linked in the model, and their linework near a point or in a box, **in model mm** with layers. Checks the model against the plan without exporting anything |
| `wall_info` | One wall in full: line, thickness, base/top, hosted windows/doors/openings (position along, sill, size), joins at each end |
| `place_inserts`* | Windows and doors, many at once: point, host wall (or nearest), width, height, sill, flip. Makes a type of that size if there is none |
| `set_insert`* | Resize, re-sill, slide or flip a placed window or door |
| `create_walls`* | Walls by centre line, level, thickness (type made if needed) or type (curtain walls too), top level or height, offsets, joins on/off |
| `split_wall`* | Split a wall at a point; windows, doors and openings stay put on the part they fall in. For façades that step or change thickness |
| `set_wall_joins`* | Stop or allow joins at wall ends, so moving a façade wall does not drag its neighbours |
| `set_wall_extents`* | Base and top of walls: levels, offsets, unconnected height (parapets, upstands, double-height walls) |
| `create_floor`* | A slab from outline loops (holes for stairs and lightwells); `replace_id` reshapes an existing slab |
| `create_roof`* | Footprint roof with a pitch per edge (mansards, pitched and flat roofs) |
| `create_opening`* | Holes: in a floor, a shaft through floors, a rectangle in a wall |
| `create_level`* | A level and its plan (parapet, roof, mezzanine, split floors) |
| `model_sweep`* | Façade detail swept along a path: cornices, string courses, copings, plinths, sills, hood mouldings |
| `model_extrusion`* | Façade detail pushed out from a flat outline: piers, pilasters, quoins, surrounds, panels, chimneys, balcony slabs, blocked-up window infill |
| `save_model`* | Save the project to its file (never Save As) |

1.4.1: `wall_info` and `split_wall` also find windows that do not cut the wall themselves (Survey to Revit's, which
cut with a separate opening); `set_wall_line` lifts a wall's rectangular openings off and puts them back, so moving a
line no longer stops with "Element is reversed"; `place_inserts` reports the real size; `save_model` added.

**No dialogs.** Every change in 1.4.0 (and `set_wall_line`, `move_elements`, `change_type`) runs with its own failure
handling: warnings are dismissed and listed in the answer (`revit`), joins Revit cannot keep are unjoined, and anything
Revit would only fix by deleting elements rolls the change back and names the elements. Nobody has to click.

**What was missing in 1.3.0** to take a survey build to one to one, found on a real job (7 Swallow Place):
the reading misses some windows (10 of ~340 on one building) and nothing could place one; a façade wall merged with a
lining could not be split without losing its windows; moving a wall dragged joined walls until Revit stopped with a
dialog; slabs, roofs, openings and levels could not be corrected; façade detail (cornices, piers, string courses) could
not be modelled at all; and checking against the DWG meant exporting it with AutoCAD outside Revit.

### Survey to 1:1, from the DWGs (1.5.0)

| Tool | What it does |
|---|---|
| `dwg_wall_fit` | Every wall on a floor (any source) against the floor's linked plan DWG: the face pair that fits it, the corrected centre line and thickness, marked moderate or review; and the walls the DWG has that the model lacks |
| `dwg_wall_fit_apply`* | Applies those fixes in one undo step (moderate ones, or chosen ids), keeping windows and doors; one refused wall does not stop the rest |
| `dwg_read` | Reads a DWG file directly: TEXT, MTEXT and attributes (sill and head labels, spot heights, room names) and lines, near a place or by layer. Linked plans come back in model mm; elevations and sections in their own coordinates |
| `dwg_analyze` | A linked DWG by layer: contents, problems, and what a build would make; suggests a config |
| `dwg_extract` | Raw DWG geometry (lines, polylines, points, blocks) by layer, kind and area, a page at a time |
| `dwg_build_model`* | Builds by layer rules: walls from face pairs or centre lines, floors from outlines, Toposolid, grids, families on blocks. Dry run by default; re-runs add only what is missing |
| `survey_qa_check` | What `dwg_build_model` made against the DWG now: missing, moved, wrong size, duplicated, stale |
| `survey_coordinates`, `set_survey_coordinates`* | Survey point, project base point, shared position; take coordinates from a link |

The DWG build, QA and coordinate tools are ported from the survey pipeline written for the open-source
`mcp-servers-for-revit` (its core and its 62 tests unchanged in behaviour), made to work on the Revit 2025 API:
`GeometryInstance.Symbol` is gone (block names now come through `GetSymbolGeometryId()`), topography is a Toposolid
(`TopographySurface.Create` is obsolete from 2024), and configs are read with System.Text.Json, fields included.
`dwg_wall_fit` is the wall audit the Swallow Place session ran by hand against AutoCAD exports, made a tool that
reads the linked DWG in place. `dwg_read` uses ACadSharp (MIT), shipped beside the add-in, the reader Survey to
Revit uses; the Revit CAD link cannot give text.

### Ground, rooms and one undo step (1.10.0)

| Tool | What it does |
|---|---|
| `dwg_terrain`* | The ground from the survey site plan: spot-height labels (each at the cross or point it labels), 3D points and contours at their height, read straight from the DWG; window, floor and drain labels left out; typos (a level metres off the rest) dropped and listed; thinned. A Toposolid through them at survey datum, inside a boundary, with holes for the building. Dry run by default |
| `create_toposolid`* | A Toposolid through points given (x, y in model mm, z survey datum), optional boundary, holes, replace |
| `toposolid_subdivide`* | Paths, paving, lawn: an area of the Toposolid with its own material |
| `place_rooms`* | Rooms where the plan names them: renames the room already there, or places one in the area the walls enclose at that point; numbers; height to the survey ceiling. Corrects what Survey to Revit's build missed |
| `batch`* | Several tool calls as one undo step (a TransactionGroup); all undone if one fails; dry run; later calls can use earlier results (`$0.floor_id`) |

Ceilings are `survey_ceilings` (1.9.1); rooms and ceilings in the base build come from Survey to Revit's `survey_build`.

### 3D: sections, curved walls, looking at the model (1.11.0)

| Tool | What it does |
|---|---|
| `survey_section` | The 1:1 score in 3D. The model is cut on each survey section's plane (every wall, floor, roof, ceiling, stair and generic model, from its real geometry) and measured against the section sheet: the whole cut profile, and per element slab top and thickness, wall and parapet tops, roof ridge, eaves and pitch (a roof modelled flat that the section draws pitched is flagged with the pitch to build), stair riser count and height. Long section lines nothing in the model follows are listed. Sections come from `section_index`, else Survey to Revit's "Survey Section X" views and the sheet titled "Section X", else the call. `image: true` returns a picture of each section with the model's cut drawn over it in red |
| `survey_section_autofix`* | The certain height fixes: wall and parapet tops, slab tops, roofs moved when ridge and eaves agree. Elements two sections disagree on are left. One undo step, dry run available |
| `view_image` | A view exported as a PNG and returned to Claude as an image, so it can look at the model, not only read numbers |
| `check_view`* | Aims a view for that: a facade seen square on from outside, a survey section, or a 3D box round elements |

Curved walls are now in `survey_fidelity` (each face sampled along the arc against the DWG, the arc refitted from
the DWG points) and `survey_autofix` re-curves them onto the DWG. The gate has new values in `fidelityGate`:
`sectionWithinPct` 90, `sectionTolMm` 20, `heightMm` 25, `slabThicknessMm` 20, `pitchDeg` 1.5, `riserMm` 5.

### When Revit refuses one element (1.12.0)

A change Revit refuses no longer stops the others: each window, door and wall is its own transaction (still one undo
step per call or per floor), and a refused one is tried again another way before it is reported. The answers list what
was done under `fallbacks` (survey_autofix) or as `Fallback:` lines in `revit`.

| Revit says | What happens now |
|---|---|
| `Can't make type "... 4930x3900"` | Another window family sized per instance is used (the generated "BG Survey Window" first), else the nearest existing type within 300 mm, else (reshaping) the size is left and the sill and position still applied. If none fits, a line says so and nothing changes for that window |
| `Element is reversed`, `Can't make Wall` (moving a wall) | Joins are stopped at the wall's ends and at the neighbours' ends that meet it, then the move is tried again (`set_wall_joins` rejoins them) |
| `Can't cut instance of ... out of Wall` (moving a wall) | Its doors and windows are kept where they were along the wall, inside its length |
| `Can't cut instance of ... out of Wall` (placing) | Slid along the wall to fit inside it; or a line saying why not (wall too short, head above the wall, overlaps window N) |
| `Ceiling sketch is invalid` (moving a wall) | The ceiling sketched on that wall is remade with the same outline, no longer locked to the wall, and the wall moved |
| `family_detail`: `Loaded Family Editing failed.` | The family's types and parameter values as the project holds them, and one instance's solids |

`place_inserts` places each item on its own, so one refused window no longer undoes the whole list.

Tools from other add-ins appear with their own prefix, for example `survey_build` and `survey_review` from Survey to Revit.

### One reading of the survey: Survey to Revit's

`survey_fidelity` and `survey_autofix` no longer read the plan DWGs with the hub's own layer names (`*Wall*`,
`*Window*`, `*Level*`) and the `S 0.94` / `H 3.27` labels only. When Survey to Revit is loaded (with its
`survey_reading` tool), they ask it for its reading of the survey and measure against that: its layer roles, the
survey's `layers.txt`, every sill and head label style it knows (`S`/`H`, `FS`+`SH`, `c800 h2400`, `OH`...), its
spot heights. So a survey drawn to another office's standard scores the way it builds, and a layers.txt fixes both.
The score also gains what only that reading has: windows and doors read on the plan that the model lacks, room
names against the model's rooms, ceiling notes against the model's ceilings. `survey_autofix` places the missing
plan windows. The elevations the hub still reads itself take their layers from Survey to Revit's roles too
(`survey_layer_roles`), with the hub's names as well. Without Survey to Revit, or with `source: cad_link`, the
hub reads the linked plans as before; every answer says which reader was used (`plans`).

How the two add-ins share it (strings only, as the bridge requires): `survey_reading` writes
`survey-reading.json` (schema `survey-reading/1`, documented in Survey to Revit's `Core/Survey/SurveyReading.cs`)
to its reports folder and answers with the path; the hub loads it with `Survey/SurveyReading.cs` and moves it into
the project's internal coordinates (the identity for a project Build from Survey set up; through the survey grid
otherwise). Other tools can use the same file: per floor, wall faces and arcs as drawn, window and door lines,
walls, windows, doors, height notes, spot heights, rooms, ceilings, stairs and the slab outline; sections and
roofs for the building.

`survey_fidelity` with `against_reference` also returns Survey to Revit's `survey_score` (right / off / missed /
invented against a hand-made model linked in), so there is one score to ask for. Gate settings added:
`ceilingMm` (20), `missingCeilings` (0), `missingRooms` (0).

Ready-made prompts (slash commands in Claude Code, e.g. `/mcp__revit__survey_to_one_to_one`):
- `survey_to_one_to_one`: the one survey workflow: Survey to Revit's analysis and base build, then every floor
  scored and fixed against the same reading until it passes, then floors, ceilings, stairs, roofs and facade.
- `dwg_survey_build`: building from a linked DWG by layer rules, with an example config.
- `revit_model_check`: a health check of the open model.
- `survey_to_model` (from Survey to Revit): build, then work through the review list; with the hub there it
  points to `survey_to_one_to_one`, which includes it.

## How it works

```
Claude ── http://localhost:48884/mcp ──► BG MCP (this add-in) ──► Revit, on Revit's own thread
                                             └──► other add-ins' tools (McpBridge)
```

- **Only this computer.** It listens on `localhost` and refuses web pages from elsewhere.
- **Off by default.** It starts with Revit only if you tick *Start with Revit*.
- **Read only** (Connect window) lets Claude look but refuses every tool that changes the model.
- **Undo.** Each change is one named undo step ("MCP: ...").
- **Revit must be idle.** Revit runs requests only between commands. While a dialog is open, calls wait. A call
  that takes longer than 45 seconds comes back as a job, and Claude collects the result with `job_status`.
- **Short answers.** Tools return summaries with limits and paging, and an answer over 60,000 characters is cut,
  so one careless call cannot use up Claude's context.
- **Call log.** `%LocalAppData%\BarrGazetas\MCP\mcp-log.jsonl` has a line per call: tool, time taken, sizes,
  errors. Never the content. Use it to measure what Claude costs and saves.
- **Settings** are in `%AppData%\BarrGazetas\MCP\settings.json`. The environment variable `BG_REVIT_MCP_PORT`
  overrides the port for a whole machine.

## Letting another add-in offer tools

Add a public static class called `McpBridge` (any namespace) to the assembly Revit loads:

```csharp
public static class McpBridge
{
    public static string Name => "My add-in";
    // name, description, input schema (JSON Schema as text), flags
    public static string[][] List() => new[] { new[] { "mine_count_doors", "Counts the doors.", "{\"type\":\"object\",\"properties\":{}}", "" } };
    // returns JSON text; throw an exception with a readable message to report a problem
    public static string Call(UIApplication app, string name, string argsJson) => "{\"doors\":12}";
    public static string[][] Prompts() => new string[0][];   // optional: name, description, text
}
```

Flags, comma separated: `background` (needs no Revit; runs off Revit's thread and `app` is null), `write` (changes
the model), `long` (can take minutes). Only strings cross between the add-ins, so neither references the other and
each updates on its own. Give tools a prefix (`mine_`), return summaries, and make anything that changes the model
one undo step with a preview-then-confirm.

## Source

| File | What |
|---|---|
| `App.cs` | Ribbon panel, the three buttons |
| `Updater.cs`, `UpdateCommand.cs` | Finds, checks and installs release zips; tidies old versions. `Updater.cs` has no Revit in it |
| `Hub.cs` | Puts the tools, the server, settings and log together |
| `McpServer.cs` | The MCP server (HTTP, JSON-RPC, jobs, read-only, prompts). No Revit in it |
| `Providers.cs` | Finds other add-ins' `McpBridge` |
| `RevitTools.cs` | The general Revit tools. Add new ones here |
| `BuildTools.cs` | Creating and reshaping (1.4.0), and `Tx`: one undo step with no dialogs. Use `Tx.Run` for any new tool that changes the model |
| `SurveyTools.cs`, `SurveyDwg.cs`, `SurveyGuide.cs` | The 1.5.0 survey tools, their Revit side (reading links, the build, QA, coordinates) and the prompts |
| `Survey/*.cs` | Revit-free survey core: layer rules, planner, wall detection, loops, topography, checks, wall fit, DWG file reading |
| `RevitDispatcher.cs` | Runs each call on Revit's thread |
| `ConnectWindow.cs`, `Icons.cs`, `Settings.cs` | The Connect window, icons, settings and call log |
| `tests/McpSmoke` | Runs the real server outside Revit and checks it: `dotnet run --project tests/McpSmoke -c Release` |
| `tests/SurveyCore` | The survey core's tests (91): `dotnet run --project tests/SurveyCore -c Release` |
| `.github/workflows/ci.yml` | On every push and pull request: builds the add-in without Revit (`dotnet build BGMCP.csproj -c Release -p:RevitApiFromNuGet=true`, using the Revit 2025 API reference packages) and runs both test suites. Results are on the repository's Actions tab |

Deliberately left out: a tool that runs arbitrary code in Revit. Any program on the computer that can reach the
server could use it.
