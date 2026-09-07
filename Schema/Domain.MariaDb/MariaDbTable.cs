// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Newtonsoft.Json;
using Schema.Domain.MySQL;

namespace Schema.Domain.MariaDb
{
    /// <summary>
    /// A MariaDB table. Everything MariaDB shares with MySQL — which is nearly all of it — comes from
    /// <see cref="MySqlTable"/>; this type exists to carry what MariaDB has and MySQL does not.
    /// <para>
    /// Before it existed, <c>Platform.MariaDb</c> deserialized to <see cref="MySqlTable"/> and there was
    /// no way to scope a property to one of the two engines: a MariaDB-only property added to the shared
    /// type would appear in MySQL's generated <c>tables.mysql.schema</c> as well, offering MySQL users a
    /// setting the engine cannot honour and an editor that green-lights it.
    /// </para>
    /// </summary>
    public class MariaDbTable : MySqlTable
    {
        /// <summary>
        /// Whether the table keeps its own row history (MariaDB <c>WITH SYSTEM VERSIONING</c>, 10.3+).
        /// <para>
        /// Detected from <c>INFORMATION_SCHEMA.TABLES.TABLE_TYPE = 'SYSTEM VERSIONED'</c>, which answers
        /// for BOTH authoring forms — the implicit form exposes nothing else at all, so anything built on
        /// period columns would see such a table as plain.
        /// </para>
        /// <para>
        /// MariaDB-only by nature: MySQL has no system versioning at any version. It lives here rather
        /// than on <c>MySqlTable</c> so it cannot appear in <c>tables.mysql.schema</c> and offer MySQL
        /// users a setting the engine cannot honour.
        /// </para>
        /// </summary>
        [JsonProperty(Order = 110)]
        public bool IsSystemVersioned { get; set; }

        /// <summary>
        /// Application-time periods declared on the table (<c>PERIOD FOR &lt;name&gt;(start, end)</c>,
        /// MariaDB 10.4.3+). Empty for the overwhelming majority of tables.
        /// <para>
        /// <b>Detection has a genuine version hole, and it is not one SchemaSmith can close.</b> The
        /// feature arrived in 10.4.3 but the catalog that reports it,
        /// <c>INFORMATION_SCHEMA.PERIODS</c>, did not land until 11.4. Between those releases a period
        /// can exist on a table and nothing can be asked about it, so extraction from a 10.4.3 - 11.3
        /// server returns none and a package round-tripped through one loses its periods. Deploying a
        /// declared period to such a server still works; it is only the read that is blind.
        /// </para>
        /// </summary>
        // MariaDB's own page compression, distinct from MySQL's COMPRESSION option (see MySqlTable).
        [SchemaProperty(Description = "MariaDB only. InnoDB page compression for the table. MariaDB's equivalent of MySQL's COMPRESSION option, which it does not support.")]
        [JsonProperty(Order = 113)]
        public bool PageCompressed { get; set; }

        // Meaningless without PageCompressed, so --Validate reports a level declared without it.
        [SchemaProperty(Minimum = 1, Maximum = 9,
            Description = "MariaDB only. Compression level 1-9 for PageCompressed. Ignored unless PageCompressed is set.")]
        [JsonProperty(Order = 114, NullValueHandling = NullValueHandling.Ignore)]
        public int? PageCompressionLevel { get; set; }

        [JsonProperty(Order = 111)]
        public List<TablePeriod> Periods { get; set; } = [];

        /// <summary>
        /// When set, overrides the environment-level <c>DropPeriodsRemovedFromProduct</c> for this table
        /// only. Null inherits.
        /// <para>
        /// <b>Defaults to off, unlike every other drop-by-absence flag.</b> Extraction omits the
        /// <c>Periods</c> key entirely when a table has none, so a package written before periods were
        /// supported — or extracted from MariaDB 10.4.3–11.3, where the catalog cannot report them —
        /// carries no periods even when the table has one. Dropping on that absence would remove a
        /// declaration the package never had the chance to make.
        /// </para>
        /// <para>
        /// It lives here rather than on the shared <c>Table</c> for the same reason the rest of this
        /// type exists: on the shared type it would appear in the SQL Server, PostgreSQL and MySQL
        /// schemas as well, offering three engines a setting none of them can honour.
        /// </para>
        /// </summary>
        [SchemaProperty(AuthoredOnly = true, Description = "MariaDB only. When set, overrides the environment-level DropPeriodsRemovedFromProduct for this table. Defaults to off, unlike the other drop-by-absence flags, because a package that predates periods or was extracted below MariaDB 11.4 cannot declare the periods its table actually has.")]
        [JsonProperty(Order = 112)]
        public bool? DropPeriodsRemovedFromProduct { get; set; }

        /// <summary>
        /// InnoDB at-rest (transparent tablespace) encryption -- MariaDB's <c>ENCRYPTED=YES/NO</c>, its
        /// equivalent of MySQL's <see cref="MySqlTable.Encryption"/> string option, which it does not
        /// support. Lives here rather than on the shared <see cref="MySqlTable"/> for the same reason as
        /// <see cref="PageCompressed"/>: on the shared type it would appear in <c>tables.mysql.schema</c>
        /// as well, offering MySQL users a setting whose grammar it rejects.
        /// <para>
        /// A server without an encryption keyring plugin rejects <c>ENCRYPTED=YES</c> with its own error --
        /// that is engine/server configuration, not a version floor, so it is not gated here the way a
        /// version-crossed feature would be.
        /// </para>
        /// </summary>
        [SchemaProperty(Description = "MariaDB only. InnoDB at-rest tablespace encryption. MariaDB's equivalent of MySQL's Encryption option, which it does not support.")]
        [JsonProperty(Order = 116)]
        public bool Encrypted { get; set; }

        // Meaningless without Encrypted, same relationship PageCompressionLevel has to PageCompressed above.
        [SchemaProperty(Minimum = 1,
            Description = "MariaDB only. ENCRYPTION_KEY_ID for the table. Ignored unless Encrypted is set.")]
        [JsonProperty(Order = 117, NullValueHandling = NullValueHandling.Ignore)]
        public int? EncryptionKeyId { get; set; }

    }
}
