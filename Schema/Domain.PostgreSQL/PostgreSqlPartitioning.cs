// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Schema.Domain.PostgreSQL
{
    /// <summary>
    /// How a PostgreSQL table is partitioned (declarative partitioning). <b>Created, appended to, verified -- never
    /// migrated.</b> A new table is created with its partitions; a declared partition the table lacks is added; a key, a
    /// bound or a partition tablespace that differs from the deployed one is refused by name, because changing any of them
    /// moves or re-checks rows. A partition the package does not declare is reported and left alone: partition managers
    /// create them out of band, and dropping one destroys data. Unset means unmanaged, not "unpartitioned".
    /// </summary>
    public class PostgreSqlPartitioning : DynamicBase
    {
        [SchemaProperty(Required = true, Pattern = "RANGE|LIST|HASH",
            Description = "RANGE, LIST or HASH.")]
        [JsonProperty(Order = 1)]
        public string Method { get; set; }

        [SchemaProperty(Required = true,
            Description = "The partition key, as it goes inside PARTITION BY <Method> ( ... ): a column (\"created_at\"), several (\"y, m\"), an expression (\"lower(code)\") or a collated column (\"code COLLATE \\\"C\\\"\"). Compared as PostgreSQL renders it, so any spelling of the same key matches.")]
        [JsonProperty(Order = 2)]
        public string Expression { get; set; }

        [SchemaProperty(
            Description = "The partitions, each a table of its own in the parent's schema. A partition the table lacks is created; one the package does not name is reported and left alone. Empty is legal for a table a partition manager fills.")]
        [JsonProperty(Order = 3)]
        public List<PostgreSqlPartition> Partitions { get; set; } = [];
    }

    /// <summary>One partition: a name, its bound and, for a partition that is itself partitioned, the next level.</summary>
    public class PostgreSqlPartition : DynamicBase
    {
        [SchemaProperty(Required = true, MaxLength = 63, Description = "The partition's table name, in the parent's schema.")]
        [JsonProperty(Order = 1)]
        public string Name { get; set; }

        [SchemaProperty(Required = true,
            Description = "The whole bound clause, as \\d+ shows it: \"FOR VALUES FROM (...) TO (...)\" for RANGE, \"FOR VALUES IN (...)\" for LIST, \"FOR VALUES WITH (modulus n, remainder r)\" for HASH, or \"DEFAULT\". Compared as PostgreSQL renders it, under the UTC time zone, so a timestamp with time zone written without an offset means UTC.")]
        [JsonProperty(Order = 2)]
        public string Bound { get; set; }

        [SchemaProperty(MaxLength = 128,
            Description = "The tablespace the partition is created on. Create-time only: a partition already on another tablespace is refused rather than moved. Omit to leave placement unmanaged.")]
        [JsonProperty(Order = 3, NullValueHandling = NullValueHandling.Ignore)]
        public string Tablespace { get; set; }

        [SchemaProperty(Description = "A partition that is itself partitioned: its key and its own partitions, one level down.")]
        [JsonProperty(Order = 4, NullValueHandling = NullValueHandling.Ignore)]
        public PostgreSqlPartitioning Partitioning { get; set; }
    }
}
