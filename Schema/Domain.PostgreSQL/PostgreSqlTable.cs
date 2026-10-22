// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

using Schema.Delivery;

namespace Schema.Domain.PostgreSQL
{
    public class PostgreSqlTable : Table, IDeliverableTable
    {
        [JsonIgnore]
        public IReadOnlyList<IDeliverableColumn> DeliverableColumns => Columns.Cast<IDeliverableColumn>().ToList();

        [JsonIgnore]
        public IReadOnlyList<IDeliverableForeignKey> DeliverableForeignKeys => ForeignKeys.Cast<IDeliverableForeignKey>().ToList();

        public override void ResolveScriptTokensInTableComponentScripts(List<KeyValuePair<string, string>> tokens)
        {
            base.ResolveScriptTokensInTableComponentScripts(tokens);
            var tableTokens = tokens.Concat(GetCustomTokens(Extensions, "Table.")).ToList();
            foreach (var column in Columns.OfType<PostgreSqlColumn>())
            {
                var scriptTokens = tableTokens.Concat(GetCustomTokens(column.Extensions)).ToList();
                column.GenerationExpression = TableTokenReplace(column.GenerationExpression, scriptTokens);
            }
            foreach (var index in Indexes.OfType<PostgreSqlIndex>())
            {
                var scriptTokens = tableTokens.Concat(GetCustomTokens(index.Extensions)).ToList();
                index.FilterExpression = TableTokenReplace(index.FilterExpression, scriptTokens);
            }
            foreach (var statistic in Statistics)
            {
                var scriptTokens = tableTokens.Concat(GetCustomTokens(statistic.Extensions)).ToList();
                statistic.ShouldApplyExpression = TableTokenReplace(statistic.ShouldApplyExpression, scriptTokens);
            }
            foreach (var exclude in ExcludeConstraints)
            {
                var scriptTokens = tableTokens.Concat(GetCustomTokens(exclude.Extensions)).ToList();
                exclude.ShouldApplyExpression = TableTokenReplace(exclude.ShouldApplyExpression, scriptTokens);
                exclude.FilterExpression = TableTokenReplace(exclude.FilterExpression, scriptTokens);
            }
            foreach (var policy in Policies)
            {
                var scriptTokens = tableTokens.Concat(GetCustomTokens(policy.Extensions)).ToList();
                policy.ShouldApplyExpression = TableTokenReplace(policy.ShouldApplyExpression, scriptTokens);
                policy.UsingExpression = TableTokenReplace(policy.UsingExpression, scriptTokens);
                policy.WithCheckExpression = TableTokenReplace(policy.WithCheckExpression, scriptTokens);
            }
        }

        // Default resolution moved to SchemaDefaultResolver (called from Template.Load) so schema
        // templates can default to "{{SchemaName}}" instead of "public". Regular templates still
        // see "public" post-load; bare-constructor instances see null until the resolver runs.
        [JsonProperty(Order = 100, NullValueHandling = NullValueHandling.Ignore)]
        public string Schema { get; set; }

        [JsonProperty(Order = 101)]
        public List<Statistic> Statistics { get; set; } = [];

        [JsonProperty(Order = 102)]
        public List<ExcludeConstraint> ExcludeConstraints { get; set; } = [];

        // Row-level security policies (#rls, gap item D1). Without these, RowLevelSecurity below
        // could only ever LOCK a table -- RLS with no policy returns no rows to anyone but the owner.
        [JsonProperty(Order = 103)]
        public List<PostgreSqlPolicy> Policies { get; set; } = [];

        [JsonProperty(Order = 105)]
        public bool RowLevelSecurity { get; set; }

        [JsonProperty(Order = 106)]
        public bool ForceRowLevelSecurity { get; set; }

        [JsonProperty(Order = 107)]
        public string AccessMethod { get; set; }

        [JsonProperty(Order = 108)]
        public string PersistenceType { get; set; }

        [JsonProperty(Order = 109)]
        [SchemaProperty(AuthoredOnly = true)]
        public bool UpdateFillFactor { get; set; }

        [SchemaProperty(Minimum = 0, Maximum = 100)]
        [JsonProperty(Order = 110)]
        public short FillFactor { get; set; }

        // Which tablespace the table's data lives on. PostgreSqlMaterializedView has carried this since
        // matviews shipped, so supporting it on one relation kind and not the other two was an accident of
        // what got built rather than a decision.
        //
        // Unset means "SchemaSmith does not manage placement here" -- NOT a declaration of the database
        // default. That is the FileGroup contract on SQL Server, and it exists because the alternative
        // (treating unset as "the default") makes every object a DBA placed elsewhere fail its SECOND
        // deploy, in packages that never mentioned placement at all.
        //
        // Create-time only, the same posture as FileGroup: ALTER TABLE ... SET TABLESPACE rewrites the
        // table under an ACCESS EXCLUSIVE lock, so a declared name that differs from the live one is
        // refused by name rather than silently moved.
        [SchemaProperty(MaxLength = 128,
            Description = "PostgreSQL only. The tablespace the table's data lives on. Omit to leave placement unmanaged — an omitted value does NOT mean the database default. Create-time only: moving an existing table rewrites it, so a declared tablespace that differs from where the table already lives is refused rather than moved.")]
        [JsonProperty(Order = 113, NullValueHandling = NullValueHandling.Ignore)]
        public string Tablespace { get; set; }

        // Unset means "not managed", matching AccessMethod and PersistenceType above. Extraction
        // emits this only for a table that is not at DEFAULT, so adding it churns no existing package.
        [SchemaProperty(Pattern = "DEFAULT|FULL|NOTHING|INDEX",
            Description = "PostgreSQL only. The table's REPLICA IDENTITY, which determines what a logical-replication publication sends for an UPDATE or DELETE — and whether either is permitted at all on a published table. INDEX requires ReplicaIdentityIndex. Omit to leave the server's current setting alone.")]
        [JsonProperty(Order = 111, NullValueHandling = NullValueHandling.Ignore)]
        public string ReplicaIdentity { get; set; }

        [SchemaProperty(MaxLength = 63,
            Description = "PostgreSQL only. Names the unique index backing ReplicaIdentity = INDEX. The index must be unique, non-partial and over NOT NULL columns; PostgreSQL rejects anything else.")]
        [JsonProperty(Order = 112, NullValueHandling = NullValueHandling.Ignore)]
        public string ReplicaIdentityIndex { get; set; }
    }
}
