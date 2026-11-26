# Start here (every AI agent, every session)

Read this page first. It is short on purpose: `COORDINATION.md` is over 200 KB (about 50,000 tokens) and most of it is
dated history. Read only the parts of it your task needs (section 4 below says which).

## 1. What this is

Two Revit 2025 add-ins that Claude drives over one MCP connection (`http://localhost:48884/mcp`):

- **Survey to Revit** (repo `DWG-Survey-to-Revit-3D`, tools `survey_*`): reads the surveyor's DWG plans, sections and
  elevations and builds the base model (walls, openings, floors, ceilings, stairs, roofs, rooms). It stays standalone.
- **The hub** (this repo, `BGMCP`, all other tools): scores the model against the DWGs, fixes what the score finds,
  and builds the facade detail.

The goal: a model of the existing building that matches the survey 1:1. Measured as: 95% of wall faces within 10 mm
per floor, openings, levels, slabs and roofs right, and the classical facade at 90% face-on and 95% in depth.
The human-made 7 Swallow Place model is a benchmark for how to model, not for positions (the DWGs win).

## 2. Standing rules (Mihai's)

1. **Any project.** No building, layer, family or facade names in code; tolerances are arguments. Layer roles come
   from Survey to Revit's rules and the job's `layers.txt`.
2. **Curves stay curves.** A survey arc becomes one curved wall or sweep, never short straight pieces.
3. **Easy for a person to correct.** Editable families, profiles on wall sweeps, one family per repeated detail, no
   DirectShapes. Anything guessed is marked as guessed.
4. **Measure, never guess.** Sizes from the DWGs; depth from sections, return elevations or the plan; photos say
   what a thing is, never its size.
5. Every tool that changes the model goes through PhaseKeeper (the survey's phase) and is one undo step.
6. **Teach the repo.** A mistake found on a run goes into `docs/FACADE-LESSONS.md` (hub) or `docs/LESSONS.md`
   (Survey to Revit) in the same commit as its fix.

## 3. How to work a survey

1. `mcp_status`, then load the prompt `survey_to_one_to_one` and follow it (it is the single script).
2. Score first and last: `survey_fidelity` (or `survey_run`, one call per floor). No score, no work.
3. Fix causes, not items: read the grouped causes; never dozens of hand edits.
4. Facade: call `facade_guide` before any facade step and follow it.
5. Look before keeping: `dwg_image` of the drawing, `facade_correspondence image: true` or `view_image` of the model.
6. Big answers: `tools/mcp-call.ps1` saves them to a file instead of the chat.

## 4. What to read next, by task

| Task | Read |
|---|---|
| Running the tools on a real model (org side) | `COORDINATION.md`: the "Start here (org side)" section at the top (current asks) and Part 1's rows for the tools you will call |
| Facade detail | `facade_guide` (tool), then `docs/FACADE-LESSONS.md` |
| Changing a tool's code | The tool's own file, its test in `tests/SurveyCore`, the matching Part 1 row in `COORDINATION.md` (update it in the same commit) |
| Planning what to build | `PLAN.md`, `docs/FACADE-1TO1-PLAN.md` |
| Learning from a finished model | `model_tools` (tool), then `docs/MODEL-LIBRARY.md` |
| What happened on earlier runs | `COORDINATION.md` Part 2, newest entries first; stop when you reach dates older than the work you need |

Part 2 of `COORDINATION.md` is a dated log. Do not read it end to end; search it for the tool, facade or floor you
are working on.

## 5. Working between the two accounts

- Mihai's work account runs Revit and the DWGs (only on his machine). The projects side can't; it builds and
  tests in the cloud and asks for runs in `COORDINATION.md`.
- Before cutting a branch, fetch and start from the current `main`; take the next free version number.
- Results go in `runs/YYYY-MM-DD/` as JSON, numbers in the entry.
