// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Schema.Delivery;
using System.ComponentModel;

namespace Schema.Domain.MySQL
{
    public class MySqlTable : Table, IDeliverableTable
    {
        string IDeliverableTable.Schema => null;

        [JsonIgnore]
        public IReadOnlyList<IDeliverableColumn> DeliverableColumns => Columns.Cast<IDeliverableColumn>().ToList();

        [JsonIgnore]
        public IReadOnlyList<IDeliverableForeignKey> DeliverableForeignKeys => ForeignKeys.Cast<IDeliverableForeignKey>().ToList();

        public override void ResolveScriptTokensInTableComponentScripts(List<KeyValuePair<string, string>> tokens)
        {
            base.ResolveScriptTokensInTableComponentScripts(tokens);
            var tableTokens = tokens.Concat(GetCustomTokens(Extensions, "Table.")).ToList();
            foreach (var column in Columns.OfType<MySqlColumn>())
            {
                var scriptTokens = tableTokens.Concat(GetCustomTokens(column.Extensions)).ToList();
                column.GenerationExpression = TableTokenReplace(column.GenerationExpression, scriptTokens);
            }
        }

        [JsonProperty(Order = 100)]
        [DefaultValue("InnoDB")]
        public string Engine { get; set; } = "InnoDB";

        [SchemaProperty(Pattern = "Dynamic|Compact|Compressed|Redundant|Fixed")]
        [JsonProperty(Order = 101)]
        public string RowFormat { get; set; }

        [JsonProperty(Order = 102)]
        public string CharacterSet { get; set; }

        [JsonProperty(Order = 103)]
        public string Collation { get; set; }

        [JsonProperty(Order = 104)]
        public string Comment { get; set; }

        [SchemaProperty(Minimum = 1)]
        [JsonProperty(Order = 105)]
        public ulong? AutoIncrementValue { get; set; }

        [JsonProperty(Order = 106)]
        public List<FullTextIndex> FullTextIndexes { get; set; } = [];

        // InnoDB transparent page compression. MariaDB has no COMPRESSION table option at all -- it spells
        // the same idea PAGE_COMPRESSED, on MariaDbTable -- so this is scoped to MySQL rather than left to
        // advertise a setting MariaDB would reject.
        [SchemaProperty(Pattern = "zlib|lz4|none", Platforms = [Platform.MySQL],
            Description = "MySQL only. InnoDB transparent page compression for the table's tablespace file. MariaDB spells this PAGE_COMPRESSED instead.")]
        [JsonProperty(Order = 107, NullValueHandling = NullValueHandling.Ignore)]
        public string Compression { get; set; }

        // The compression page size OF RowFormat = COMPRESSED, which is why it lives beside it rather than
        // as a row of its own -- it is meaningless without it, the same relationship PadIndex has to
        // FillFactor on SQL Server. Both engines report it identically (verified on 5.7, 8.0 and 10.2).
        [SchemaProperty(Minimum = 1, Maximum = 16,
            Description = "InnoDB compressed-page size in KB (1, 2, 4, 8 or 16). Only meaningful with RowFormat = COMPRESSED.")]
        [JsonProperty(Order = 108, NullValueHandling = NullValueHandling.Ignore)]
        public int? KeyBlockSize { get; set; }

        // Partitioning (#partitioning). Declared here rather than as a separate object because MySQL has no
        // partition-scheme object to point at -- the definition lives in the table DDL. Applied at CREATE and
        // verified thereafter; see MySqlPartitioning for why it is never migrated.
        [JsonProperty(Order = 109, NullValueHandling = NullValueHandling.Ignore)]
        public MySqlPartitioning Partitioning { get; set; }

        // InnoDB at-rest (transparent tablespace) encryption. MariaDB spells the same idea ENCRYPTED=YES/NO
        // (a bool, plus an optional ENCRYPTION_KEY_ID) -- see MariaDbTable.Encrypted -- rather than MySQL's
        // ENCRYPTION='Y'/'N' string, so this stays scoped to MySQL rather than offer MariaDB a setting its
        // grammar rejects. Order starts at 115, not 110: MariaDbTable (a subclass of this type) already
        // occupies 110-114, so anything added here must clear that range or collide (SharedTypeSerializationOrderTests).
        [SchemaProperty(Pattern = "Y|N", Platforms = [Platform.MySQL],
            Description = "MySQL only. InnoDB at-rest tablespace encryption ('Y' or 'N'). MariaDB spells this ENCRYPTED=YES/NO instead.")]
        [JsonProperty(Order = 115, NullValueHandling = NullValueHandling.Ignore)]
        public string Encryption { get; set; }

        // The InnoDB GENERAL tablespace (CREATE TABLESPACE ... ADD DATAFILE) this table is placed in --
        // placement, like SQL Server FileGroup and PostgreSQL Tablespace, applied only at CREATE. MariaDB
        // has no general tablespaces at any version (CREATE TABLESPACE is a syntax error there), so this
        // stays MySQL-only rather than offer MariaDB a setting its grammar rejects -- same shape as
        // Encryption above. Order 118, not 116: MariaDbTable (a subclass of this type) already occupies
        // 110-114/116/117, so anything added here must clear that range or collide
        // (SharedTypeSerializationOrderTests).
        // Pattern restricts to unquoted-identifier chars: the value is concatenated UNQUOTED into a
        // dynamically-built CREATE TABLE (`TABLESPACE <name>`), so anything outside a legal tablespace
        // identifier is either invalid DDL or an injection vector -- reject it at validation.
        [SchemaProperty(Platforms = [Platform.MySQL], Pattern = "^[A-Za-z0-9_$]+$",
            Description = "MySQL only. The InnoDB general tablespace this table is placed in; applied at create, a move is refused.")]
        [JsonProperty(Order = 118, NullValueHandling = NullValueHandling.Ignore)]
        public string Tablespace { get; set; }

        // The filesystem directory an InnoDB table's data file is placed in (DATA DIRECTORY='<path>'),
        // both engines -- unlike Tablespace above (MySQL-only general tablespaces), both MySQL and MariaDB
        // support this clause. Placement, applied only at CREATE; a declared change on an existing table
        // is refused, never applied as a move -- the same posture as Tablespace, SQL Server FileGroup and
        // PostgreSQL Tablespace. Order 119, not 116: MariaDbTable (a subclass of this type) already
        // occupies 110-114/116/117, so anything added here must clear that range or collide
        // (SharedTypeSerializationOrderTests).
        // Pattern forbids a single quote: the value is emitted inside a single-quoted DDL literal (escaped
        // on write, but belt-and-suspenders) AND read back on MariaDB by parsing CREATE_OPTIONS up to the
        // first quote -- a quote in the path could not survive that round-trip anyway, so it is rejected at
        // validation rather than silently truncated into a redeploy-refusing mismatch.
        [SchemaProperty(Platforms = [Platform.MySQL, Platform.MariaDb], Pattern = "^[^']+$",
            Description = "The filesystem directory the table's data file is placed in (InnoDB DATA DIRECTORY); applied at create, a move is refused. MySQL requires the directory to be listed in the server's innodb_directories.")]
        [JsonProperty(Order = 119, NullValueHandling = NullValueHandling.Ignore)]
        public string DataDirectory { get; set; }
    }
}
