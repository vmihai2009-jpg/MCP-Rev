// Ported from the mcp-servers-for-revit survey pipeline (survey-pipeline.patch, base 86cf705). Revit-free.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BarrGazetas.RevitMcp.Survey
{
    public class LayerInventory
    {
        public string Name { get; set; }
        public int Lines { get; set; }
        public int Polylines { get; set; }
        public int ClosedPolylines { get; set; }
        public int Points { get; set; }
        public int Blocks { get; set; }
        public int Total { get { return Lines + Polylines + Points + Blocks; } }
        public double ZMinMm { get; set; }
        public double ZMaxMm { get; set; }
    }

    public class SuggestedConfig
    {
        public BuildConfig Config { get; set; } = new BuildConfig();
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>Drafts a starter layer->element configuration from CAD layer names/contents. Always a draft for a human to review.</summary>
    public static class ConfigSuggester
    {
        private static readonly string[] IgnoreTokens = { "DEFPOINTS", "TEXT", "TXT", "ANNO", "ANNOTATION", "DIM", "DIMS", "HATCH", "TITLE", "TITLEBLOCK", "NOTE", "NOTES", "VIEWPORT", "VPORT", "XREF", "LEGEND", "TABLE", "SYMBOL", "NORTH" };
        private static readonly string[] WallTokens = { "WALL", "WALLS", "MUR", "MURS", "WAND", "WANDE" };
        private static readonly string[] FloorTokens = { "SLAB", "FLOOR", "FOOTPRINT", "BLDG", "BUILDING", "BUILDINGS", "ROOM", "ROOF" };
        private static readonly string[] TopoTokens = { "TOPO", "CONTOUR", "CONTOURS", "SPOT", "SPOTS", "SPOTHEIGHT", "ELEV", "GROUND", "TERRAIN", "DTM", "TIN", "SURFACE", "LEVELS", "HEIGHTS" };
        private static readonly string[] GridTokens = { "GRID", "GRIDS", "AXIS", "AXES" };
        private static readonly string[] PointTokens = { "TREE", "TREES", "LAMP", "POLE", "HYDRANT", "MANHOLE", "MH", "COLUMN", "COLUMNS", "SIGN", "BOLLARD", "CHAMBER", "GULLY", "VALVE" };

        public static SuggestedConfig Suggest(IEnumerable<LayerInventory> layers)
        {
            var res = new SuggestedConfig();
            var cfg = res.Config;
            var ignore = new List<string>(); var walls = new List<string>(); var floors = new List<string>();
            var topoPts = new List<string>(); var topoContours = new List<string>(); var grids = new List<string>(); var points = new List<string>();

            foreach (var l in layers.Where(x => x.Total > 0).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var tokens = Tokens(l.Name);
                if (Has(tokens, IgnoreTokens)) ignore.Add(l.Name);
                else if (Has(tokens, TopoTokens))
                {
                    bool contourLike = (l.Polylines + l.Lines) > l.Points && (l.ZMaxMm - l.ZMinMm) > 1.0;
                    (contourLike ? topoContours : topoPts).Add(l.Name);
                }
                else if (Has(tokens, GridTokens)) grids.Add(l.Name);
                else if (Has(tokens, PointTokens)) points.Add(l.Name);
                else if (Has(tokens, WallTokens)) walls.Add(l.Name);
                else if (Has(tokens, FloorTokens)) floors.Add(l.Name);
                else res.Notes.Add("Layer '" + l.Name + "' (" + l.Total + " entities) not recognised - map it manually if needed.");
            }

            if (ignore.Count > 0) cfg.Rules.Add(new LayerRule { Name = "ignore_annotation", Match = ignore, Action = Actions.Ignore, Priority = 100 });
            if (walls.Count > 0) cfg.Rules.Add(new LayerRule { Name = "walls", Match = walls, Action = Actions.WallDoubleLine });
            if (floors.Count > 0) cfg.Rules.Add(new LayerRule { Name = "floors", Match = floors, Action = Actions.Floor });
            if (topoPts.Count > 0) cfg.Rules.Add(new LayerRule { Name = "topo_points", Match = topoPts, Action = Actions.TopoPoints, TopoCellMm = 500 });
            if (topoContours.Count > 0) cfg.Rules.Add(new LayerRule { Name = "topo_contours", Match = topoContours, Action = Actions.TopoContours });
            if (grids.Count > 0) cfg.Rules.Add(new LayerRule { Name = "grids", Match = grids, Action = Actions.Grid });
            if (points.Count > 0)
            {
                cfg.Rules.Add(new LayerRule { Name = "point_objects", Match = points, Action = Actions.PointFamily });
                res.Notes.Add("Rule 'point_objects': set familyName/typeName (or a familyMap per CAD block) - no family is guessed.");
            }
            if (walls.Count > 0) res.Notes.Add("Rule 'walls' assumes double-line walls. If a layer holds single centre-lines change action to 'wall_centerline' and set thicknessMm.");
            if (cfg.Levels.Count == 0) cfg.Levels.Add(new LevelSpec { Name = "Level 1", ElevationMm = 0 });
            cfg.DefaultLevel = cfg.Levels[0].Name;
            return res;
        }

        private static List<string> Tokens(string layer)
        {
            string bare = LayerRuleSet.StripXref(layer ?? "");
            // split on non-alphanumerics and camel-case boundaries: "A-WALL-EXT", "SiteTopo" -> A, WALL, EXT / SITE, TOPO
            string spaced = Regex.Replace(bare, "([a-z])([A-Z])", "$1 $2");
            return Regex.Split(spaced, "[^A-Za-z0-9]+").Where(t => t.Length > 0).Select(t => t.ToUpperInvariant()).ToList();
        }

        private static bool Has(List<string> tokens, string[] words) { return tokens.Any(t => words.Contains(t)); }
    }
}
