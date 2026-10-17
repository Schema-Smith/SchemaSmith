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

        protected override bool IsDerivedColumn() => !string.IsNullOrWhiteSpace(ComputedExpression);

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

        // Unset is unmanaged: packages extracted before this existed omit it, and treating that as padded would re-pad
        // every legacy unpadded column on the first deploy. Extraction writes false and never true.
        [JsonProperty(Order = 112, NullValueHandling = NullValueHandling.Ignore)]
        [SchemaProperty(Description = "SQL Server only, for char, varchar, binary and varbinary columns. false creates the column " +
                                      "under SET ANSI_PADDING OFF, so trailing blanks (or trailing zeros) are trimmed on insert; " +
                                      "true pads it as usual. Leaving it out leaves an existing column's padding as it is.")]
        public bool? AnsiPadding { get; set; }

        /// <summary>
        /// The column is one the engine fills, declared by its role: a temporal table's period columns are <c>RowStart</c>
        /// and <c>RowEnd</c>, and the pair becomes <c>PERIOD FOR SYSTEM_TIME</c>. Declared, the name, <c>DATETIME2(n)</c> and
        /// <c>Hidden</c> are the author's; undeclared on a new temporal table, the convention <c>ValidFrom</c>/<c>ValidTo</c>
        /// <c>DATETIME2(7)</c> applies, and on an existing table the live pair is left alone. A ledger table's generated
        /// columns are <c>TransactionIdStart</c>/<c>End</c> and <c>SequenceNumberStart</c>/<c>End</c>; one left out takes the
        /// engine's name.
        /// </summary>
        [JsonProperty(Order = 113, NullValueHandling = NullValueHandling.Ignore)]
        [SchemaProperty(Pattern = "RowStart|RowEnd|TransactionIdStart|TransactionIdEnd|SequenceNumberStart|SequenceNumberEnd",
            Description = "A column the engine fills. RowStart or RowEnd (SQL Server 2016+): a temporal table's period column; declare both, as DATETIME2(n), on a table with IsTemporal, and the pair becomes PERIOD FOR SYSTEM_TIME. A rename or a change to Hidden is applied in place; a precision change is reported, because SQL Server cannot alter a period column. Omitted on a new temporal table, the columns are ValidFrom and ValidTo DATETIME2(7). TransactionIdStart, TransactionIdEnd, SequenceNumberStart or SequenceNumberEnd (SQL Server 2022): a ledger table's generated column, BIGINT; an append-only table has only the two Start columns. One left out takes the engine's name (ledger_start_transaction_id and so on), hidden. A rename or a change to Hidden is applied in place.")]
        public string GeneratedAlways { get; set; }

        [JsonProperty(Order = 114)]
        [SchemaProperty(Description = "SQL Server 2016+, with GeneratedAlways. The column is HIDDEN: left out of SELECT * and of an INSERT without a column list. Changed in place.")]
        public bool Hidden { get; set; }
    }
}
