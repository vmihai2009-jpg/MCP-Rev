// Ported from the mcp-servers-for-revit survey pipeline (survey-pipeline.patch, base 86cf705). Revit-free.
using System;
using System.Collections.Generic;

namespace BarrGazetas.RevitMcp.Survey
{
    // Plain POCOs: read with System.Text.Json, case-insensitively, so JSON like {"thicknessMm": 200} just works.

    public class LevelSpec
    {
        public string Name { get; set; }
        public double ElevationMm { get; set; }
    }

    public class BlockFamilyMap
    {
        /// <summary>CAD block name (glob allowed).</summary>
        public string Block { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
    }

    public static class Actions
    {
        public const string Ignore = "ignore";
        public const string WallCentreline = "wall_centerline";
        public const string WallDoubleLine = "wall_double_line";
        public const string Floor = "floor";
        public const string TopoPoints = "topo_points";
        public const string TopoContours = "topo_contours";
        public const string Grid = "grid";
        public const string PointFamily = "point_family";

        public static readonly string[] All = { Ignore, WallCentreline, WallDoubleLine, Floor, TopoPoints, TopoContours, Grid, PointFamily };
        public static bool IsValid(string a) { return Array.IndexOf(All, (a ?? "").ToLowerInvariant()) >= 0; }
    }

    public class LayerRule
    {
        public string Name { get; set; }
        /// <summary>Layer name globs (* and ?), case-insensitive. Also matched against the part after the last '|' or '$' (xref/bound-xref prefixes).</summary>
        public List<string> Match { get; set; } = new List<string>();
        public List<string> Exclude { get; set; } = new List<string>();
        public string Action { get; set; } = Actions.Ignore;
        /// <summary>Higher wins when several rules match the same layer. Ties: first declared wins.</summary>
        public int Priority { get; set; }

        public string TypeName { get; set; }
        public string FamilyName { get; set; }
        public List<BlockFamilyMap> FamilyMap { get; set; } = new List<BlockFamilyMap>();
        public string Level { get; set; }
        public double HeightMm { get; set; } = 2800;
        public double BaseOffsetMm { get; set; }

        // walls
        public double ThicknessMm { get; set; } = 200;
        public double MinThicknessMm { get; set; } = 60;
        public double MaxThicknessMm { get; set; } = 600;
        public double MinOverlapMm { get; set; } = 300;
        public double AngleTolDeg { get; set; } = 1.0;

        // floors
        public bool JoinLooseLines { get; set; } = true;
        public double MinAreaMm2 { get; set; } = 100000; // 0.1 m2

        // topography
        public double TopoCellMm { get; set; }
        public int MaxPoints { get; set; } = 30000;
        public double ContourSpacingMm { get; set; } = 5000;
        public string DecimateMode { get; set; } = "average";
        /// <summary>Optional constant Z (mm) to apply when the CAD points are flat (Z=0) – e.g. from a layer-name elevation.</summary>
        public double? ZOffsetMm { get; set; }
    }

    public class BuildConfig
    {
        public Tolerances Tolerances { get; set; } = new Tolerances();
        public List<LevelSpec> Levels { get; set; } = new List<LevelSpec>();
        public string DefaultLevel { get; set; }
        public List<LayerRule> Rules { get; set; } = new List<LayerRule>();
        /// <summary>Layers matching no rule are reported (default true). They are never modelled.</summary>
        public bool ReportUnmappedLayers { get; set; } = true;
    }
}
