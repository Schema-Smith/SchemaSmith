// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using Schema.Domain.MySQL;

namespace Schema.Domain.MariaDb
{
    /// <summary>
    /// MariaDB's column, adding the one attribute MySQL has no equivalent for.
    /// <para>The first MariaDB-specific column type, mirroring what <see cref="MariaDbTable"/> already does
    /// for the table. Adding a property to a MariaDB-only subclass is the established override pattern and
    /// is unrelated to the open question about MariaDB gaining a whole object <i>type</i> MySQL lacks.</para>
    /// </summary>
    public class MariaDbColumn : MySqlColumn
    {
        // MySQL has no system versioning at all, so this cannot move up to MySqlColumn.
        [SchemaProperty(Description = "MariaDB only. Excludes this column from the row history of a system-versioned table: an UPDATE that changes only this column writes no history row. Meaningless on a table that is not system-versioned — MariaDB accepts the clause there and silently discards it.")]
        [JsonProperty(Order = 110)]
        public bool WithoutSystemVersioning { get; set; }

        [SchemaProperty(Description = "MariaDB only. A column-level CHECK (`qty` >= 0), without the surrounding CHECK (...). MariaDB names it after the column and keeps it column-level across a redeploy; it converges through MODIFY COLUMN and extracts back here, not into CheckConstraints. MySQL has no column-level check: declare a named entry in CheckConstraints there.")]
        [JsonProperty(Order = 111)]
        public string CheckExpression { get; set; }
    }
}
