// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Schema.Domain
{
    /// <summary>
    /// How SchemaTongs sequences a table's object lists when it has nothing to preserve — a first
    /// extraction, and any entry that did not exist in the file being replaced.
    /// <para>
    /// This is an extraction preference and nothing on the deploy path reads it. Making an already-deployed
    /// table's physical column order match the package is a table rebuild, not an ordering preference, and
    /// is a separate opt-in.
    /// </para>
    /// <para>
    /// <b>This setting orders <c>Columns</c> and nothing else.</b> Indexes, foreign keys, check
    /// constraints and — where the engine has them — statistics, XML indexes and fulltext indexes are
    /// always emitted in name order by the generator, whatever this is set to: they are sets, with no
    /// physical sequence for <see cref="Physical"/> to mean anything about. The stored-procedure
    /// parameter that carries this (<c>@p_ObjectOrder</c>, or <c>@SchemaSmith_ObjectOrder</c> on
    /// MySQL/MariaDB) therefore has exactly the same scope the setting does.
    /// </para>
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ObjectOrder
    {
        /// <summary>Alphabetical by name. The default, and stable when a source table's ordinal order changes.</summary>
        Name,

        /// <summary>
        /// The table's own column order. Useful when the package is meant to read like the table does;
        /// note that two databases can order the same logical table differently, so a package extracted
        /// this way is not guaranteed to match elsewhere.
        /// </summary>
        Physical
    }
}
