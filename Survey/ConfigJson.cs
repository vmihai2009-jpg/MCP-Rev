// Revit-free. How build configs are read and results written (the patch used Newtonsoft; BG MCP uses System.Text.Json).
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BarrGazetas.RevitMcp.Survey
{
    public static class ConfigJson
    {
        /// <summary>
        /// Case-insensitive, comments and trailing commas allowed, and FIELDS included: Tolerances, Pt and the cleaning
        /// counts are fields, which System.Text.Json would otherwise skip without a word (Newtonsoft read them).
        /// </summary>
        public static readonly JsonSerializerOptions Read = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, IncludeFields = true,
        };

        public static readonly JsonSerializerOptions Write = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            IncludeFields = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public static BuildConfig Parse(string json)
        {
            BuildConfig cfg;
            try { cfg = JsonSerializer.Deserialize<BuildConfig>(json, Read); }
            catch (Exception ex) { throw new ArgumentException("The config is not valid: " + ex.Message); }
            if (cfg == null) throw new ArgumentException("The config is empty.");
            cfg.Rules = cfg.Rules ?? new List<LayerRule>();
            cfg.Tolerances = cfg.Tolerances ?? new Tolerances();
            cfg.Levels = cfg.Levels ?? new List<LevelSpec>();
            return cfg;
        }
    }
}
