// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using System.ComponentModel;

namespace Schema.Domain.SqlServer
{
    public class SqlServerIndex : Index
    {
        [JsonProperty(Order = 100)]
        public string FilterExpression { get; set; }

        [JsonProperty(Order = 101)]
        [DefaultValue("NONE")]
        public string CompressionType { get; set; } = "NONE";

        // Valid on an index as well as a table (probed on 2022), and alterable via ALTER INDEX ... REBUILD.
        // Same 2022-deploy / 2025-extract asymmetry as SqlServerTable.XmlCompression -- see the comment there.
        [SchemaProperty(Description = "SQL Server 2022+. Compresses XML column data in place. Sibling of CompressionType (DATA_COMPRESSION), and independent of it. **Deployable from 2022, but only EXTRACTABLE from 2025** — sys.partitions.xml_compression does not exist before then, so on 2022-2024 SchemaTongs carries the value forward from the package it is refreshing rather than dropping it.")]
        [JsonProperty(Order = 110)]
        public bool XmlCompression { get; set; }

        [JsonProperty(Order = 102)]
        public bool Clustered { get; set; }

        [JsonProperty(Order = 103)]
        public bool ColumnStore { get; set; }

        [SchemaProperty(Minimum = 0, Maximum = 100)]
        [JsonProperty(Order = 104)]
        public byte FillFactor { get; set; }

        [JsonProperty(Order = 105)]
        public string IncludeColumns { get; set; }

        [JsonProperty(Order = 106)]
        [SchemaProperty(AuthoredOnly = true)]
        public bool UpdateFillFactor { get; set; }

        // Filegroup placement (#filegroups): same name-only, null-means-default contract as
        // SqlServerTable.FileGroup -- see that property's comment. A table and its indexes are commonly
        // split across filegroups on purpose, so this is independent of the table's own FileGroup.
        [JsonProperty(Order = 107, NullValueHandling = NullValueHandling.Ignore)]
        public string FileGroup { get; set; }

        /// <summary>
        /// <c>IGNORE_DUP_KEY</c>. Not a tuning knob -- it changes what the engine does with the same
        /// statement. Verified on SQL Server 2022: with it ON, a multi-row INSERT containing a duplicate
        /// SUCCEEDS, the duplicate row is discarded with a warning and the other rows land; with it OFF,
        /// the whole statement fails with 2601 and nothing lands. Two databases whose index definitions
        /// otherwise match will disagree about whether an application's INSERT works.
        /// <para>Only meaningful on a unique index or unique constraint; SQL Server rejects it elsewhere.</para>
        /// </summary>
        [JsonProperty(Order = 108)]
        public bool IgnoreDuplicateKey { get; set; }

        /// <summary>
        /// <c>PAD_INDEX</c> -- applies <see cref="FillFactor"/> to the intermediate index pages as well as
        /// the leaf. Meaningless on its own, which is why it belongs with the property it modifies rather
        /// than standing alone. Read from <c>sys.indexes.is_padded</c>.
        /// <para>Unlike IGNORE_DUP_KEY there is no <c>ALTER INDEX ... SET</c> for it (error 155), so a
        /// change rides the ordinary index drop-and-recreate rather than an in-place alter.</para>
        /// </summary>
        [JsonProperty(Order = 109)]
        public bool PadIndex { get; set; }

        // Partition placement (#partitioning): same name-only contract as SqlServerTable.PartitionScheme --
        // see that property's comment. Independent of the table's own placement because an index is not
        // required to be aligned: a nonclustered index on a partitioned table may sit on a single filegroup,
        // and an index on an ordinary heap may be partitioned. Both are real designs, so neither is inferred
        // from the table.
        [JsonProperty(Order = 110, NullValueHandling = NullValueHandling.Ignore)]
        public string PartitionScheme { get; set; }

        [JsonProperty(Order = 111, NullValueHandling = NullValueHandling.Ignore)]
        public string PartitionColumn { get; set; }

        /// <summary>
        /// <c>BUCKET_COUNT</c> for a HASH index on a memory-optimized table (#J1). A HASH index needs one --
        /// it sizes the hash table -- and it is meaningless on any other index. Its presence is also what
        /// tells SchemaSmith to emit the index as <c>NONCLUSTERED HASH</c> rather than a range index.
        /// <para><b>Applied at CREATE, and a change is REFUSED</b> -- the same posture as the table's
        /// <c>MEMORY_OPTIMIZED</c>/<c>DURABILITY</c>. Nothing here emits
        /// <c>ALTER TABLE … ALTER INDEX … REBUILD WITH (BUCKET_COUNT = n)</c>: the only two places
        /// <c>BUCKET_COUNT</c> appears in <c>Schema/Scripts</c> are the extraction read in
        /// <c>GenerateTableJson</c> and the inline emit in <c>MissingTableAndColumnQuench</c>, and
        /// <c>ChangingBucketCountOnADeployedMemoryOptimizedTable_IsRefused</c> asserts the refusal.</para>
        /// <para>This comment previously claimed the opposite -- that the rebuild "is supported ... (verified
        /// live)" -- which is the inverse of what ships. Corrected rather than implemented: the refusal is
        /// the right behaviour, and a maintainer trusting the old text could have "restored" a convergence
        /// path the tests forbid.</para>
        /// <para>SQL Server rounds a requested count up to the next power of two, so a comparison would be
        /// against the rounded value the catalog reports rather than the raw request.</para>
        /// </summary>
        [SchemaProperty(Minimum = 1,
            Description = "BUCKET_COUNT for a HASH index on a memory-optimized table. Required for a hash index, ignored elsewhere. SQL Server rounds it up to the next power of two.")]
        [JsonProperty(Order = 112, NullValueHandling = NullValueHandling.Ignore)]
        public int? BucketCount { get; set; }

        /// <summary>
        /// The index is disabled (<c>ALTER INDEX … DISABLE</c>): its definition stays in the catalog but it holds no
        /// data and enforces nothing. Declared state, converged once the index exists: true disables a live index,
        /// false (the default) rebuilds a disabled one, which is what enables it. Read from <c>sys.indexes.is_disabled</c>.
        /// </summary>
        [SchemaProperty(Description = "The index is disabled (ALTER INDEX … DISABLE): defined but holding no data and enforcing nothing. Declared true, a live index is disabled after it is created; left false, an index someone disabled by hand is rebuilt, which enables it. Disabling a clustered index makes the table unreadable, and disabling a key's index disables the foreign keys that reference it.")]
        [JsonProperty(Order = 113)]
        public bool Disabled { get; set; }

        /// <summary>
        /// An ordered columnstore index's sort columns (<c>ORDER (…)</c>), comma-separated in sort order: a clustered
        /// columnstore from SQL Server 2022, a nonclustered one from 2025. Below that the index is created unordered and a
        /// downgrade is recorded. Read from <c>sys.index_columns.column_store_order_ordinal</c>.
        /// </summary>
        [SchemaProperty(Description = "ColumnStore only: the ORDER columns, comma-separated in sort order. A clustered columnstore index from SQL Server 2022, a nonclustered one from 2025; below that it is created unordered and a downgrade recorded. A change re-creates the index.")]
        [JsonProperty(Order = 114, NullValueHandling = NullValueHandling.Ignore)]
        public string Order { get; set; }
    }
}
