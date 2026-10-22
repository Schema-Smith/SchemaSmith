// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;

namespace Schema.Domain
{
    public class Index : DynamicBase
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; } = "";

        [JsonProperty(Order = 2)]
        public bool PrimaryKey { get; set; }

        [JsonProperty(Order = 3)]
        public bool Unique { get; set; }

        [JsonProperty(Order = 4)]
        public bool UniqueConstraint { get; set; }

        // A columnstore index has no key columns: SQL Server reports every one of its columns as
        // included, so extraction emits them in IncludeColumns and leaves this empty, and the deploy
        // path only renders it `WHEN ColumnStore = 0`. Requiring it unconditionally made --Validate
        // reject packages SchemaTongs had just produced. Engines without a columnstore concept have no
        // ColumnStore property, so there it stays plainly required.
        [SchemaProperty(Required = true, RequiredUnless = "ColumnStore")]
        [JsonProperty(Order = 10)]
        public string IndexColumns { get; set; } = "";

        [SchemaProperty(Description = "Optional. A description stored on the object: an MS_Description extended property on SQL Server, COMMENT ON on PostgreSQL, the COMMENT clause on MySQL and MariaDB. On SQL Server and PostgreSQL an omitted value leaves an existing description alone and \"\" removes it; on MySQL and MariaDB an omitted value clears the comment, as the engine's own DDL does.")]
        [JsonProperty(Order = 11, NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get; set; }

        [JsonProperty(Order = 90)]
        public string ShouldApplyExpression { get; set; }

        // Labels a conditional variant: the intent behind its ShouldApplyExpression,
        // echoed in quench log messages when the variant applies.
        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 91)]
        public string VariantName { get; set; }
    }
}
