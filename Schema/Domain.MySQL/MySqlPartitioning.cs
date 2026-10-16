// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Schema.Domain.MySQL
{
    /// <summary>
    /// How a MySQL or MariaDB table is partitioned (#partitioning).
    /// <para><b>Applied at CREATE, verified thereafter, never migrated.</b> MySQL carries the whole
    /// partition definition inside the table DDL, so unlike SQL Server there is no separate scheme object to
    /// point at — the definition has to live here. But the posture is identical:
    /// <c>ALTER TABLE … PARTITION BY</c> rewrites every row of the table, and a state-based diff cannot
    /// derive the SPLIT/MERGE intent behind a changed boundary, so a declaration that disagrees with a
    /// deployed table is refused by name rather than applied.</para>
    /// <para>Both engines were probed identical (MySQL 5.7 and 8.0, MariaDB 10.2 and 11.4), so this lives on
    /// <see cref="MySqlTable"/> and <c>MariaDbTable</c> inherits it rather than declaring a twin.</para>
    /// </summary>
    public class MySqlPartitioning : DynamicBase
    {
        /// <summary>
        /// RANGE, LIST, HASH, KEY, their LINEAR forms (a powers-of-two hash, still a count), or the COLUMNS forms of the first two. The COLUMNS variants take a
        /// column LIST rather than an expression and compare values column-by-column, which is why they are
        /// distinct methods rather than a flag.
        /// </summary>
        [SchemaProperty(Required = true, Pattern = "RANGE|LIST|HASH|KEY|LINEAR HASH|LINEAR KEY|RANGE COLUMNS|LIST COLUMNS",
            Description = "RANGE, LIST, HASH, KEY, LINEAR HASH, LINEAR KEY, RANGE COLUMNS or LIST COLUMNS. The LINEAR forms take a PartitionCount, as HASH and KEY do.")]
        [JsonProperty(Order = 1)]
        public string Method { get; set; }

        /// <summary>
        /// What the method is applied to: an expression for RANGE/LIST/HASH/KEY (<c>id</c>,
        /// <c>YEAR(created)</c>), or a comma-separated column list for the COLUMNS forms.
        /// <para>Compared NORMALIZED, and the supported floor is why. The engines do not agree on how they
        /// report it back: MySQL 5.7 returns the text the user wrote (<c>YEAR(dt)</c>) while MySQL 8,
        /// MariaDB 10.2 and MariaDB 11.4 all return a rewritten form (<c>year(`dt`)</c>). A literal compare
        /// would therefore refuse a package extracted on 5.7 and deployed to 8 — a false alarm on a layout
        /// that is identical. Backticks and whitespace are stripped and case is folded before comparing.
        /// </para>
        /// </summary>
        [SchemaProperty(Required = true,
            Description = "The partitioning expression, or a comma-separated column list for the COLUMNS methods. Compared with backticks, whitespace and case normalized away, because the engines disagree on how they report it.")]
        [JsonProperty(Order = 2)]
        public string Expression { get; set; }

        /// <summary>
        /// HASH, KEY and their LINEAR forms only: how many partitions to spread across. RANGE and LIST name their partitions
        /// individually in <see cref="Partitions"/> instead, because each one carries its own boundary.
        /// </summary>
        [SchemaProperty(Minimum = 1,
            Description = "Number of partitions for HASH and KEY. Not used by RANGE or LIST, which name each partition and its boundary explicitly.")]
        [JsonProperty(Order = 3, NullValueHandling = NullValueHandling.Ignore)]
        public int? PartitionCount { get; set; }

        /// <summary>
        /// The partitions, <b>in declared order</b>. Order is load-bearing for RANGE — the boundaries must
        /// ascend, and the engine rejects a definition where they do not — so this is a list rather than a
        /// set and extraction reads it by <c>PARTITION_ORDINAL_POSITION</c>.
        /// </summary>
        [JsonProperty(Order = 4)]
        public List<MySqlPartition> Partitions { get; set; } = [];
    }

    /// <summary>One named partition and the boundary that selects it.</summary>
    public class MySqlPartition : DynamicBase
    {
        [SchemaProperty(Required = true, MaxLength = 64)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; }

        /// <summary>
        /// The boundary: what follows <c>VALUES LESS THAN</c> for RANGE (a value, a tuple for RANGE COLUMNS,
        /// or <c>MAXVALUE</c>) or <c>VALUES IN</c> for LIST. Null for HASH and KEY, which have no boundary —
        /// the engine assigns rows by hashing.
        /// </summary>
        [SchemaProperty(Description = "The VALUES LESS THAN boundary for RANGE, or the VALUES IN list for LIST. Omitted for HASH and KEY, which have no boundary.")]
        [JsonProperty(Order = 2, NullValueHandling = NullValueHandling.Ignore)]
        public string Values { get; set; }
    }
}
