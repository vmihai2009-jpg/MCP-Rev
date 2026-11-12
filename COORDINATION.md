# COORDINATION.md: notes between the two AI agents working on this code

**If you are an AI coding agent and you have just opened this repository, read this whole file before you change anything.** It takes two minutes and it will save you from undoing someone else's work.

## What this file is

Mihai (the owner of these repositories, an architect) has **two separate Claude agents** working on the same code at the same time, from two different accounts. The agents cannot talk to each other directly and cannot see each other's chats. This file is the only way they can tell each other what they are doing.

The same file lives in both repositories, and the two copies are kept in step:

- **MCP-Rev** (`vmihai2009-jpg/MCP-Rev`, default branch `main`): the Revit add-in that gives Claude tools to read and edit a Revit model (the "hub", `BGMCP`).
- **DWG-Survey-to-Revit-3D** (`vmihai2009-jpg/DWG-Survey-to-Revit-3D`, default branch `claude/project-thread-owac6b`; there is no `main` branch): the Survey to Revit plugin that reads measured-survey DWGs and builds the 3D Revit model.

The shared goal: Claude, through the MCP-Rev tools, drives the Survey to Revit plugin to produce a 1:1 3D model of an existing building from the surveyor's DWG plans, sections and elevations, and then checks the model against the drawings.

The two sides:

- **"Projects side"**: a group of Claude agents working from Mihai's personal account. They each work on their own `claude/project-thread-*` branch and open a pull request for every change. They keep the section "Projects side" below up to date.
- **"Org side"**: a Claude agent working from Mihai's work account. **If you are reading this from the work account, that is you.** You own the section "Org side" below.

## How to use this file

1. **Pull the latest default branch first** (`git pull`), then read both sections below. Also look at the open pull requests on GitHub: they show work that is not on the default branch yet.
2. **Write only in your own section.** Add a new dated entry at the top of your section (newest first). Never delete or rewrite the other side's entries; if something there is wrong or out of date, say so in an entry in your own section.
3. **Write in plain prose for someone who knows nothing.** The other side has never seen your chat. Say what you changed, which files, which version number, and anything you need from the other side.
4. **Keep changes small and say which files they touch.** Before editing a file, check whether an open pull request or a recent entry here already touches it. If it does, either wait, build on top of that branch, or write an entry asking first.
5. **Update this file in the same commit or pull request as the work it describes,** so the note and the code arrive together. Commit this file to the default branch (or with your pull request) so the other side sees it on their next pull.
6. **Version numbers.** Both sides bump the version in `BGMCP.csproj` (MCP-Rev) and in the plugin (Survey to Revit). Before you pick a number, check the open pull requests and the entries here so two changes don't claim the same version. Note the number you took in your entry.
7. If you need something from Mihai rather than from the other agent, ask Mihai in your own chat; he reads both sides.

Entry format (copy this):

```
### YYYY-MM-DD: short title
- What: one or two sentences.
- Files: list of files touched.
- Version: x.y.z (or "none").
- Where: committed to <branch> / pull request #N.
- Needs from the other side: ... (or "nothing").
```

---

## Projects side (Mihai's personal account)

### 2026-11-12: current work and open pull requests

Nothing below is merged yet; each item is an open pull request waiting for Mihai's review. If you change any of the files listed, expect a merge conflict with that pull request and mention it in your section.

**MCP-Rev** (all target `main`):

- **#1 CI**: builds the add-in on GitHub without Revit installed and runs both test suites on every pull request. Files: `.github/workflows/ci.yml`, `BGMCP.csproj`, `loader/BGMCP.Loader.csproj`, `README.md`. Version: none.
- **#2 Terrain, rooms, batch**: builds the terrain from the site plan's spot levels, a `place_rooms` tool, and a `batch` tool that runs several edits as one undo step. Files: `BatchTool.cs`, `RoomTools.cs`, `SurveyTerrainTools.cs`, `Survey/Terrain.cs`, `Survey/DwgFile.cs`, `Hub.cs`, `SurveyGuide.cs`, `README.md`, `BGMCP.csproj`, tests. Version: 1.10.0. Not yet tested inside Revit.
- **#3 Hub uses Survey to Revit's reading**: the hub's 1:1 score and autofix compare the model against the *plugin's own* reading of the DWG (a `survey_reading` file, schema `survey-reading/1`) instead of reading the DWG a second time. Files: `Survey/SurveyReading.cs` (new), `Survey/Fidelity.cs`, `SurveyFidelityTools.cs`, `SurveyAutofixTools.cs`, `SurveyGuide.cs`, `Hub.cs`, `README.md`, tests. Version: none. Pairs with Survey to Revit #3.
- **#4 3D section score**: scores the model in 3D against the survey sections, fits curved walls, and adds view images. Files: `Survey/SectionFit.cs`, `Survey/CurvedFit.cs`, `Survey/LineImage.cs` (new), `SurveySectionTools.cs`, `ViewImageTools.cs` (new), `Survey/Fidelity.cs`, `SurveyAutofixTools.cs`, `SurveyFidelityTools.cs`, `SurveyGuide.cs`, `McpServer.cs`, `README.md`, tests. Version: 1.10.0.

Suggested merge order: #1, #3, #2, #4. Note that #2 and #4 both claim 1.10.0, and `main` has meanwhile moved on to 1.9.9 (`split_opening`) directly; whichever of them merges second will be renumbered. These branches were cut from `main` before 1.9.8 and 1.9.9 and will be rebased onto them.

**DWG-Survey-to-Revit-3D** (all target `claude/project-thread-owac6b`):

- **#1 CI**: builds the solution and runs the tests on every pull request. Files: `.github/workflows/ci.yml`, `README.md`.
- **#2 Surveyor standards**: reads other surveyors' height notes and warns when a DWG doesn't follow the layer rules. Files: `src/SurveyToRevit.Core/Survey/LayerCheck.cs` (new), `LayerRules.cs`, `LevelEstimator.cs`, `SurveyAnalyzer.cs`, `SurveySummary.cs`, `src/SurveyToRevit.Core/SurveyHeights.cs`, `WindowFinder.cs`, `README.md`, tests.
- **#3 Bridge tools for the hub**: adds MCP tools `survey_reading` and `survey_layer_roles`, which write out the plugin's reading of the DWG for MCP-Rev #3 to score against. Files: `src/SurveyToRevit.Core/Survey/SurveyReading.cs` (new), `src/SurveyToRevit.Revit/Mcp/McpTools.cs`, tests.

**Also in progress on our side (no pull request yet):** rooms, ceilings and terrain from the survey; once MCP-Rev #3 merges, the section score (#4) will also switch to reading the plugin's `survey_reading` file.

**What we'd like from the org side:** a short entry below saying what you are working on, which files you expect to touch, and which version numbers you are using, so we don't collide. We noticed you are committing straight to `main` in MCP-Rev (1.9.8 `reference_compare`, 1.9.9 `split_opening`); that's fine, just add a line here when you do.

---

## Org side (Mihai's work account)

_Write your entries here, newest first, using the format above. Things worth saying: what you're working on now, which files you expect to touch, the version numbers you're using, and anything you need from the projects side._

<!-- First entry goes here. -->
