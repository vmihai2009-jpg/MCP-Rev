// 1.45.0: sheet_revision. Make (or reuse) a revision and put it on a set of sheets, and set sheet parameters (status, drawn by,
// checked by) in the same undo step: an issue the way an architect does it, for a whole drawing series at once.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> SheetRevisions()
        {
            yield return new RevitTool
            {
                Name = "sheet_revision",
                Write = true,
                Description = "Issue a set of sheets: makes a revision (description, date, issued by/to) or reuses the one with the same description and date, adds it to every sheet whose number starts with sheet_prefixes (or is in sheet_numbers), and sets sheet parameters on them (e.g. {\"Sheet Status\": \"S0\", \"Drawn By\": \"MV\", \"Checked By\": \"CB\"}). " +
                              "Reports the project's revisions and the revision number each sheet now shows (Revit numbers it from the project's revision numbering). Without sheets, only lists the revisions. dry_run (default true) does it, reports, undoes. One undo step. Does not save.",
                InputSchema = Schema(
                    ("description", S("string", "Revision description, e.g. \"Stage 1 Issue - for information\""), false),
                    ("date", S("string", "Revision date as it should read, e.g. 26.11.26"), false),
                    ("issued_by", S("string", "Issued by"), false),
                    ("issued_to", S("string", "Issued to"), false),
                    ("sheet_prefixes", Arr("string", "Sheets whose number starts with one of these, e.g. [\"10.\", \"20.\"]"), false),
                    ("sheet_numbers", Arr("string", "Or exact sheet numbers"), false),
                    ("exclude_sheets", Arr("string", "Leave out sheets with this number, or with this text in their name"), false),
                    ("sheet_parameters", Obj("Sheet parameters to set on those sheets, name: value, e.g. {\"Sheet Status\": \"S0\"}"), false),
                    ("dry_run", S("boolean", "Do it, report, undo (default true)"), false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var cmp = StringComparison.OrdinalIgnoreCase;
                    bool dry = Args.Bool(a, "dry_run") ?? true;
                    string desc = Args.Str(a, "description"), date = Args.Str(a, "date");
                    var prefixes = Args.Strings(a, "sheet_prefixes"); var exact = Args.Strings(a, "sheet_numbers"); var excl = Args.Strings(a, "exclude_sheets");
                    var sets = (a?["sheet_parameters"] as JsonObject)?.Select(kv => (name: kv.Key, value: kv.Value?.ToString() ?? "")).ToList() ?? new List<(string, string)>();

                    // a project numbered per sheet has no project-wide revision number (Revit throws): each sheet's own number is reported instead
                    string ProjectRevNo(Revision r) { try { return r.RevisionNumber; } catch { return "per sheet"; } }
                    object List() => Revision.GetAllRevisionIds(doc).Select(i => (Revision)doc.GetElement(i)).Select(r => new
                    {
                        id = r.Id.Value, sequence = r.SequenceNumber, number = ProjectRevNo(r), date = r.RevisionDate, description = r.Description,
                        issued = r.Issued, issued_by = r.IssuedBy, issued_to = r.IssuedTo, numbering = doc.GetElement(r.RevisionNumberingSequenceId)?.Name, visibility = r.Visibility.ToString(),
                    }).ToList();

                    var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder && !s.IsTemplate
                                    && (exact.Any(n => n.Equals(s.SheetNumber, cmp)) || prefixes.Any(p => s.SheetNumber.StartsWith(p, cmp)))
                                    && !excl.Any(x => s.SheetNumber.Equals(x, cmp) || s.Name.IndexOf(x, cmp) >= 0))
                        .OrderBy(s => s.SheetNumber).ToList();
                    if (sheets.Count == 0) return new { revisions = List(), note = "No sheets given (or none match): revisions listed only." };
                    if (desc == null || date == null) throw new ArgumentException("Give description and date.");

                    var rows = new JsonArray(); var paramFails = new List<string>();
                    long revId = 0; bool reused = false; string revNo = null;
                    List<string> notes = null;
                    try
                    {
                        notes = BuildTools.Tx.Run(doc, "MCP: sheet revision", () =>
                        {
                            var rev = Revision.GetAllRevisionIds(doc).Select(i => (Revision)doc.GetElement(i))
                                .FirstOrDefault(r => string.Equals(r.Description, desc, cmp) && string.Equals(r.RevisionDate, date, cmp));
                            if (rev != null) reused = true;
                            else
                            {
                                rev = Revision.Create(doc);
                                rev.Description = desc;
                                rev.RevisionDate = date;
                            }
                            if (Args.Str(a, "issued_by") is string ib && !rev.Issued) rev.IssuedBy = ib;
                            if (Args.Str(a, "issued_to") is string it && !rev.Issued) rev.IssuedTo = it;
                            revId = rev.Id.Value;
                            foreach (var s in sheets)
                            {
                                var ids = s.GetAdditionalRevisionIds().ToList();
                                bool had = ids.Contains(rev.Id) || s.GetAllRevisionIds().Contains(rev.Id);
                                if (!ids.Contains(rev.Id)) { ids.Add(rev.Id); s.SetAdditionalRevisionIds(ids); }
                                foreach (var (name, value) in sets)
                                {
                                    var p = s.LookupParameter(name);
                                    if (p == null) { paramFails.Add($"{s.SheetNumber}: no parameter \"{name}\""); continue; }
                                    if (p.IsReadOnly) { paramFails.Add($"{s.SheetNumber}: \"{name}\" is read-only"); continue; }
                                    bool ok = p.StorageType == StorageType.String ? p.Set(value) : p.SetValueString(value);
                                    if (!ok) paramFails.Add($"{s.SheetNumber}: \"{name}\" refused \"{value}\"");
                                }
                            }
                            doc.Regenerate();
                            revNo = ProjectRevNo(rev);
                            foreach (var s in sheets)
                                rows.Add(new JsonObject
                                {
                                    ["sheet"] = s.SheetNumber, ["name"] = s.Name,
                                    ["revision_on_sheet"] = s.GetRevisionNumberOnSheet(rev.Id),
                                    ["current_revision"] = s.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString(),
                                });
                            if (dry) throw new DryRunDone();
                        });
                    }
                    catch (DryRunDone) { }

                    return new
                    {
                        dry_run = dry,
                        revision = new { id = dry ? (long?)null : revId, reused, description = desc, date, number = revNo },
                        sheets = rows, sheet_count = rows.Count,
                        sheet_parameters_set = sets.Select(s => $"{s.name} = {s.value}").ToList(),
                        failed = paramFails.Take(30).ToList(),
                        revisions_now = dry ? null : List(),
                        revit = notes,
                        undo = dry ? "Nothing changed (dry run)." : "One undo step in Revit. Not saved.",
                    };
                }
            };
        }
    }
}
