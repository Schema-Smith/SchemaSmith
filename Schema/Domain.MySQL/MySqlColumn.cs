// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.ComponentModel;
using Newtonsoft.Json;

namespace Schema.Domain.MySQL
{
    public class MySqlColumn : Column
    {
        [JsonProperty(Order = 100)]
        public string GenerationExpression { get; set; }

        [JsonProperty(Order = 101)]
        public bool AutoIncrement { get; set; }

        [SchemaProperty(Pattern = "STORED|VIRTUAL")]
        [JsonProperty(Order = 102)]
        public string Generated { get; set; }

        [JsonProperty(Order = 103)]
        public string CharacterSet { get; set; }

        [JsonProperty(Order = 104)]
        public string Collation { get; set; }

        [JsonProperty(Order = 105)]
        public string Comment { get; set; }

        // Mirrors MySqlIndex.Visible one level down (index -> column): hides the column from
        // SELECT * / INSERT-without-column-list. MySQL 8.0.23 / MariaDB 10.3 — see
        // SchemaSmith_SupportsInvisibleColumn.
        [JsonProperty(Order = 107)]
        [DefaultValue(false)]
        public bool Invisible { get; set; }

        // Restricts a spatial column to one spatial reference system (`col POINT SRID 4326`); NULL
        // means unrestricted. MySQL 8.0.3+ only -- MariaDB has no equivalent attribute at any version.
        // See SchemaSmith_SupportsColumnSrid.
        [JsonProperty(Order = 108)]
        public int? Srid { get; set; }

        // The column's `ON UPDATE CURRENT_TIMESTAMP[(n)]` auto-refresh clause (TIMESTAMP/DATETIME
        // only); NULL means the column does not auto-refresh on UPDATE. A nullable string, not a
        // bool, because the clause takes an optional fractional-seconds precision (0-6) that must
        // round-trip -- "CURRENT_TIMESTAMP(3)" collapsing to a bare "CURRENT_TIMESTAMP" would silently
        // change the column's behavior on redeploy. Deliberately independent of Default: a column's
        // `DEFAULT CURRENT_TIMESTAMP` (Column.Default, inherited) governs INSERT-time initialization
        // and is unrelated to this UPDATE-time refresh -- a column can have either, both, or neither.
        // Available since MySQL 5.6.5 / present in MariaDB from its earliest supported version, both
        // below this codebase's floors (MySQL 5.7, MariaDB 10.2 -- see VersionHelper), so unlike
        // Invisible/Srid above this needs no SchemaSmith_Supports... version gate anywhere.
        [JsonProperty(Order = 109)]
        public string OnUpdateCurrentTimestamp { get; set; }
    }
}
