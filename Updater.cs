using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BarrGazetas.RevitMcp
{
    /// <summary>A release zip found on disk: BGMCP-1.2.0.zip.</summary>
    internal sealed class Release
    {
        public string Zip;
        public Version Version;
        public override string ToString() => Version.ToString(3);
    }

    /// <summary>
    /// Installs BG MCP from a release zip (the same zip install.cmd comes in), with Revit running.
    ///
    /// Revit holds the running add-in's file, so nothing in use is ever overwritten: each version goes into a
    /// folder of its own (BGMCP\1.2.0\) and the manifest (BGMCP.addin) is switched to point at it. The new
    /// version runs the next time Revit starts; until then the old one keeps working. Old version folders are
    /// tidied away at start-up, keeping the one running and the one before it to go back to.
    ///
    /// No Revit in this file, so it is tested outside Revit (tests/McpSmoke).
    /// </summary>
    internal static class Updater
    {
        public const string DllName = "BGMCP.Revit.dll";
        public const string ManifestName = "BGMCP.addin";
        public const string AddInId = "3e9b6f21-7c4a-4d58-a1f0-8b2c5d9e4a63";
        private static readonly Regex ZipName = new Regex(@"^BGMCP-(\d+)\.(\d+)\.(\d+)\.zip$", RegexOptions.IgnoreCase);
        private static readonly Regex VersionFolder = new Regex(@"^\d+\.\d+\.\d+$");

        public static string AddinsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2025");
        public static string DefaultUpdates => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        public static string Root(string addins) => Path.Combine(addins, "BGMCP");

        public static Version Running => Short(typeof(Updater).Assembly.GetName().Version);
        /// <summary>Set under BGMCP.Loader, which loads the core from bytes (so the assembly has no Location).</summary>
        public static string HostedFolder;
        public static string RunningFolder => HostedFolder ?? Path.GetDirectoryName(typeof(Updater).Assembly.Location);

        public static Version Short(Version v) => v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        /// <summary>The version a zip's name says, or null for a name that is not BGMCP-x.y.z.zip.</summary>
        public static Version VersionOfName(string zip)
        {
            var m = ZipName.Match(Path.GetFileName(zip ?? ""));
            return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
        }

        /// <summary>Every BGMCP-x.y.z.zip in a folder, newest first. A missing or unreachable folder has none.</summary>
        public static List<Release> Find(string folder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new List<Release>();
                return Directory.GetFiles(folder, "BGMCP-*.zip")
                    .Select(z => new Release { Zip = z, Version = VersionOfName(z) })
                    .Where(r => r.Version != null)
                    .OrderByDescending(r => r.Version).ThenByDescending(r => File.GetLastWriteTimeUtc(r.Zip))
                    .ToList();
            }
            catch { return new List<Release>(); } // e.g. a network folder that is not connected
        }

        /// <summary>The newest zip newer than <paramref name="than"/>, or null.</summary>
        public static Release Newer(string folder, Version than) => Find(folder).FirstOrDefault(r => r.Version > than);

        /// <summary>
        /// Installs a release zip into its own version folder and points the manifest at it. Returns that folder.
        /// Checks the zip before anything changes: it must hold BGMCP.Revit.dll, and the version inside must be the
        /// one its name says. Throws with a message meant to be read; on any problem the manifest is untouched.
        /// </summary>
        public static string Install(string zip, string addins, string runningFolder, out Version version, bool writeManifest = true)
        {
            if (!File.Exists(zip)) throw new InvalidOperationException($"There is no file {zip}.");
            string temp = Path.Combine(Path.GetTempPath(), "BGMCP-update-" + Guid.NewGuid().ToString("N"));
            try
            {
                try { ZipFile.ExtractToDirectory(zip, temp); }
                catch (InvalidDataException) { throw new InvalidOperationException($"{Path.GetFileName(zip)} is damaged or is not a zip. Download it again."); }

                // The add-in, wherever it sits in the zip (a zip may wrap everything in a top folder).
                string dll = Directory.GetFiles(temp, DllName, SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault()
                    ?? throw new InvalidOperationException($"{Path.GetFileName(zip)} is not a BG MCP release: there is no {DllName} in it.");
                AssemblyName name;
                try { name = AssemblyName.GetAssemblyName(dll); }
                catch (BadImageFormatException) { throw new InvalidOperationException($"The {DllName} in {Path.GetFileName(zip)} is damaged."); }
                if (!string.Equals(name.Name, "BGMCP.Revit", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"The {DllName} in {Path.GetFileName(zip)} is not BG MCP.");
                version = Short(name.Version);
                var named = VersionOfName(zip);
                if (named != null && named != version)
                    throw new InvalidOperationException($"{Path.GetFileName(zip)} says {named.ToString(3)} but holds {version.ToString(3)}. Download it again.");

                string root = Root(addins);
                string target = Path.Combine(root, version.ToString(3));
                if (Same(target, runningFolder))
                    throw new InvalidOperationException($"BG MCP {version.ToString(3)} is the version running now.");

                // Copy into a side folder, then move it into place, so a half-copied version is never pointed at.
                Directory.CreateDirectory(root);
                string staging = target + ".new-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                Directory.CreateDirectory(staging);
                foreach (var file in Directory.GetFiles(Path.GetDirectoryName(dll)).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
                    File.Copy(file, Path.Combine(staging, Path.GetFileName(file)), true);
                if (Directory.Exists(target))
                {
                    if (InUse(target)) { TryDelete(staging); throw new InvalidOperationException($"BG MCP {version.ToString(3)} is in use by another Revit. Close it and try again."); }
                    Directory.Delete(target, true);
                }
                Directory.Move(staging, target);

                // under the loader the manifest stays on the loader, which picks the newest version folder
                if (writeManifest) WriteManifest(addins, $@"BGMCP\{version.ToString(3)}\{DllName}");
                return target;
            }
            finally { TryDelete(temp); }
        }

        public static string Manifest(string assemblyPath) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<RevitAddIns>\r\n" +
            "  <AddIn Type=\"Application\">\r\n" +
            "    <Name>BG MCP</Name>\r\n" +
            $"    <Assembly>{assemblyPath}</Assembly>\r\n" +
            $"    <AddInId>{AddInId}</AddInId>\r\n" +
            "    <FullClassName>BarrGazetas.RevitMcp.App</FullClassName>\r\n" +
            "    <VendorId>BARRGAZETAS</VendorId>\r\n" +
            "    <VendorDescription>Barr Gazetas</VendorDescription>\r\n" +
            "  </AddIn>\r\n" +
            "</RevitAddIns>\r\n";

        private static void WriteManifest(string addins, string assemblyPath)
        {
            string path = Path.Combine(addins, ManifestName);
            string temp = path + ".tmp";
            File.WriteAllText(temp, Manifest(assemblyPath));
            File.Move(temp, path, true);
        }

        /// <summary>The folder the manifest points at: what Revit loads next time it starts. Null if there is no manifest.</summary>
        public static string ManifestFolder(string addins)
        {
            try
            {
                string path = Path.Combine(addins, ManifestName);
                if (!File.Exists(path)) return null;
                var m = Regex.Match(File.ReadAllText(path), @"<Assembly>\s*(.+?)\s*</Assembly>");
                if (!m.Success) return null;
                string dll = m.Groups[1].Value;
                return Path.GetDirectoryName(Path.IsPathRooted(dll) ? dll : Path.Combine(addins, dll));
            }
            catch { return null; }
        }

        /// <summary>A newer version installed and waiting for Revit to restart, or null.</summary>
        public static Version Waiting(string addins, string runningFolder, Version running)
        {
            string next = ManifestFolder(addins);
            if (next == null || Same(next, runningFolder)) return null;
            try
            {
                string dll = Path.Combine(next, DllName);
                if (!File.Exists(dll)) return null;
                var v = Short(AssemblyName.GetAssemblyName(dll).Version);
                return v != running ? v : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Removes old version folders: keeps the one running, the one the manifest points at and the newest
        /// <paramref name="keep"/> others (to go back to). A folder another Revit still uses is left for next time.
        /// Also removes the 1.1.0 layout (the add-in straight in BGMCP\) once nothing runs from it.
        /// Returns what was removed.
        /// </summary>
        public static List<string> Tidy(string addins, string runningFolder, int keep = 1)
        {
            var removed = new List<string>();
            string root = Root(addins);
            if (!Directory.Exists(root)) return removed;
            string next = ManifestFolder(addins);

            var versions = Directory.GetDirectories(root)
                .Select(d => new { Dir = d, Name = Path.GetFileName(d) })
                .Where(d => VersionFolder.IsMatch(d.Name))
                .OrderByDescending(d => Version.Parse(d.Name)).ToList();
            var spare = versions.Where(d => !Same(d.Dir, runningFolder) && !Same(d.Dir, next)).Skip(keep);
            foreach (var d in spare)
                if (!InUse(d.Dir) && TryDelete(d.Dir)) removed.Add(d.Name);

            // half-finished installs
            foreach (var d in Directory.GetDirectories(root).Where(d => Path.GetFileName(d).Contains(".new-")))
                if (TryDelete(d)) removed.Add(Path.GetFileName(d));

            // the flat 1.1.0 layout
            string flat = Path.Combine(root, DllName);
            if (File.Exists(flat) && !Same(root, runningFolder) && !Same(root, next))
            {
                try { File.Delete(flat); File.Delete(Path.Combine(root, "BGMCP.Revit.pdb")); removed.Add(DllName); } catch { }
            }
            return removed;
        }

        private static bool Same(string a, string b)
        {
            if (a == null || b == null) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>True if a Revit has a file in the folder open (a loaded add-in cannot be opened for writing).</summary>
        private static bool InUse(string folder)
        {
            foreach (var file in Directory.GetFiles(folder, "*.dll"))
            {
                try { using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (IOException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
            }
            return false;
        }

        private static bool TryDelete(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); return true; }
            catch { return false; }
        }
    }
}
