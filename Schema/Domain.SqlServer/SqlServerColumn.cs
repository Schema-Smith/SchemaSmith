// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using System.ComponentModel;

namespace Schema.Domain.SqlServer
{
    public class SqlServerColumn : Column
    {
        [JsonProperty(Order = 100)]
        public string CheckExpression { get; set; }

        [JsonProperty(Order = 101)]
        public string ComputedExpression { get; set; }

        [JsonProperty(Order = 102)]
        public bool Persisted { get; set; }

        [JsonProperty(Order = 103)]
        public bool Sparse { get; set; }

        // COLUMN_SET FOR ALL_SPARSE_COLUMNS: an XML column that aggregates the table's sparse columns.
        // Legal at the 2008 floor alongside Sparse (SqlServerColumn:19) -- no version gate needed.
        [JsonProperty(Order = 109)]
        public bool IsColumnSet { get; set; }

        [JsonProperty(Order = 104)]
        public string Collation { get; set; }

        [JsonProperty(Order = 105)]
        public string DataMaskFunction { get; set; }

        [SchemaProperty(Pattern = "DETERMINISTIC|RANDOMIZED|NONE")]
        [JsonProperty(Order = 106)]
        [DefaultValue("NONE")]
        public string EncryptionType { get; set; } = "NONE";

        [JsonProperty(Order = 107)]
        public string EncryptionKey { get; set; }

        [JsonProperty(Order = 108)]
        public string EncryptionAlgorithm { get; set; }

        // ALTER TABLE ... ADD col type NULL DEFAULT x WITH VALUES. Without it SQL Server leaves existing
        // rows NULL when a NULLABLE column with a default is added -- PostgreSQL, MySQL and MariaDB all
        // backfill instead, so this is a genuine SQL Server difference rather than a missing knob elsewhere.
        // A NOT NULL column already backfills, so the clause only changes anything for the nullable case.
        //
        // AuthoredOnly because the catalog does not record whether the clause was used -- there is nothing
        // to extract, and without the marker a re-extract would silently drop it.
        //
        // Opt-in on purpose: defaulting it on would rewrite every existing row of a table on any nullable
        // column add, which is a data change nobody asked for.
        [SchemaProperty(AuthoredOnly = true,
            Description = "SQL Server only. When adding this column to an existing table, apply its Default to " +
                          "rows that are already there. Without it a nullable column's existing rows stay NULL. " +
                          "Requires Default; ignored when the column is created with the table.")]
        [JsonProperty(Order = 110)]
        public bool BackfillExistingRows { get; set; }
        // FILESTREAM storage for a VARBINARY(MAX) column: the value lives in the NTFS filegroup rather
        // than in the row. Requires FILESTREAM enabled on the server AND a FILESTREAM filegroup on the
        // database -- neither of which SchemaSmith turns on, so a package asking for it without them is
        // reported through UnsupportedFeaturePolicy rather than failing on a raw engine error.
        //
        // The table must also carry a ROWGUIDCOL column (error 5505 otherwise). SchemaSmith requires the
        // package to declare it rather than inventing one: a column SchemaSmith adds by itself appears in
        // no package and vanishes on the next extract-redeploy round trip.
        [JsonProperty(Order = 111)]
        public bool FileStream { get; set; }

    }
}
