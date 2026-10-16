// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;

namespace Schema.Domain.SqlServer
{
    /// <summary>
    /// A spatial index (<c>CREATE SPATIAL INDEX</c>) on a <c>geometry</c> or <c>geography</c> column. Its key is one column
    /// and a tessellation, not a column list, so it is its own list on the table, as <see cref="XmlIndex"/> is. Every
    /// attribute other than the column is optional: omitted, it is the engine's default on create and is not compared
    /// on an existing index, so a package that names only the column keeps whatever the index has. A declared attribute
    /// that differs re-creates the index.
    /// </summary>
    public class SpatialIndex : DynamicBase
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; }

        [SchemaProperty(Required = true, Description = "The geometry or geography column the index covers.")]
        [JsonProperty(Order = 2)]
        public string Column { get; set; }

        [SchemaProperty(Pattern = "GEOMETRY_GRID|GEOGRAPHY_GRID|GEOMETRY_AUTO_GRID|GEOGRAPHY_AUTO_GRID",
            Description = "USING: GEOMETRY_GRID or GEOMETRY_AUTO_GRID on a geometry column, GEOGRAPHY_GRID or GEOGRAPHY_AUTO_GRID on a geography column. The AUTO_GRID schemes are SQL Server 2012+. Omitted, the engine chooses.")]
        [JsonProperty(Order = 3, NullValueHandling = NullValueHandling.Ignore)]
        public string Tessellation { get; set; }

        [SchemaProperty(Description = "BOUNDING_BOX for a geometry tessellation, which requires one. Not allowed on a geography tessellation.")]
        [JsonProperty(Order = 4, NullValueHandling = NullValueHandling.Ignore)]
        public SpatialBoundingBox BoundingBox { get; set; }

        [SchemaProperty(Description = "GRIDS for GEOMETRY_GRID and GEOGRAPHY_GRID: the density of each of the four levels. Not allowed on an AUTO_GRID tessellation. Omitted, every level is MEDIUM.")]
        [JsonProperty(Order = 5, NullValueHandling = NullValueHandling.Ignore)]
        public SpatialGrids Grids { get; set; }

        [SchemaProperty(Minimum = 1, Maximum = 8192,
            Description = "CELLS_PER_OBJECT. Omitted, the engine's default for the tessellation: 16 for the GRID schemes, 8 for GEOMETRY_AUTO_GRID, 12 for GEOGRAPHY_AUTO_GRID.")]
        [JsonProperty(Order = 6, NullValueHandling = NullValueHandling.Ignore)]
        public int? CellsPerObject { get; set; }

        [SchemaProperty(Minimum = 1, Maximum = 100, Description = "FILLFACTOR. Omitted, the server's default.")]
        [JsonProperty(Order = 7, NullValueHandling = NullValueHandling.Ignore)]
        public byte? FillFactor { get; set; }

        [JsonProperty(Order = 8)]
        public string ShouldApplyExpression { get; set; }

        // Labels a conditional variant: the intent behind its ShouldApplyExpression,
        // echoed in quench log messages when the variant applies.
        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 9)]
        public string VariantName { get; set; }
    }

    public class SpatialBoundingBox
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public double XMin { get; set; }

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 2)]
        public double YMin { get; set; }

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 3)]
        public double XMax { get; set; }

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 4)]
        public double YMax { get; set; }
    }

    public class SpatialGrids
    {
        [SchemaProperty(Pattern = "LOW|MEDIUM|HIGH")]
        [JsonProperty(Order = 1, NullValueHandling = NullValueHandling.Ignore)]
        public string Level1 { get; set; }

        [SchemaProperty(Pattern = "LOW|MEDIUM|HIGH")]
        [JsonProperty(Order = 2, NullValueHandling = NullValueHandling.Ignore)]
        public string Level2 { get; set; }

        [SchemaProperty(Pattern = "LOW|MEDIUM|HIGH")]
        [JsonProperty(Order = 3, NullValueHandling = NullValueHandling.Ignore)]
        public string Level3 { get; set; }

        [SchemaProperty(Pattern = "LOW|MEDIUM|HIGH")]
        [JsonProperty(Order = 4, NullValueHandling = NullValueHandling.Ignore)]
        public string Level4 { get; set; }
    }
}
