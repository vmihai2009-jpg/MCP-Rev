using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>
    /// 1.36.16: finds a drawing named by another tool's index. Survey to Revit's elevation-index.json gives a sheet by its
    /// name alone ("29463B-9 (SECTIONS &amp; ELEVATIONS)": no folder, no .dwg), so reading it as a path failed on every
    /// survey (Mount Row: "There is no file 29463B-9 ..."). The name is looked for in the survey's folders, any survey.
    /// </summary>
    public static class DrawingPaths
    {
        /// <summary>The file the name means: itself when it exists; else the first file in the folders (and up to three
        /// levels under them) whose name, or name without extension, is the given name (case ignored, .dwg added when it
        /// has none). Null when nothing fits.</summary>
        public static string Resolve(string name, IEnumerable<string> folders)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try { if (File.Exists(name)) return Path.GetFullPath(name); } catch { }
            string file;
            try { file = Path.GetFileName(name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)); }
            catch { file = name; }
            if (string.IsNullOrWhiteSpace(file)) return null;
            bool hasExt = string.Equals(Path.GetExtension(file), ".dwg", StringComparison.OrdinalIgnoreCase);
            string stem = hasExt ? Path.GetFileNameWithoutExtension(file) : file;
            var dirs = (folders ?? Enumerable.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
            // the folders themselves first, then below them
            foreach (var d in dirs)
            {
                string direct = Path.Combine(d, hasExt ? file : file + ".dwg");
                if (File.Exists(direct)) return direct;
            }
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
            foreach (var d in dirs)
            {
                try
                {
                    var hit = Directory.EnumerateFiles(d, "*.dwg", opts)
                        .FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), stem, StringComparison.OrdinalIgnoreCase));
                    if (hit != null) return hit;
                }
                catch { }
            }
            return null;
        }
    }
}
