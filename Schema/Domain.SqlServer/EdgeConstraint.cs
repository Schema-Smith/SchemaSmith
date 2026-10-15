// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;

namespace Schema.Domain.SqlServer
{
    /// <summary>
    /// A SQL Server edge constraint (2019+): which node tables an edge table's rows may connect, and what a delete of a
    /// connected node does to them. Valid only on a table whose <see cref="SqlServerTable.GraphType"/> is <c>Edge</c>.
    /// A change to the clauses or the action drops and re-creates the constraint; SQL Server has no ALTER for it.
    /// </summary>
    public class EdgeConstraint : DynamicBase
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; }

        /// <summary>The <c>CONNECTION</c> clauses: each a from-node table and a to-node table. Order does not matter.</summary>
        [SchemaProperty(Required = true, Description = "The CONNECTION clauses, each a from-node table and a to-node table (schema-qualified, or the edge table's own schema). Order does not matter.")]
        [JsonProperty(Order = 2)]
        public List<EdgeConstraintClause> Clauses { get; set; } = [];

        [SchemaProperty(Pattern = "NO ACTION|CASCADE", Description = "ON DELETE action when a connected node row is deleted: NO ACTION (the default) refuses the delete while an edge connects the node; CASCADE deletes the edges too.")]
        [JsonProperty(Order = 3, NullValueHandling = NullValueHandling.Ignore)]
        [DefaultValue("NO ACTION")]
        public string DeleteAction { get; set; }

        [JsonProperty(Order = 4)]
        public string ShouldApplyExpression { get; set; }

        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 5)]
        public string VariantName { get; set; }
    }

    /// <summary>One <c>CONNECTION</c> clause of an <see cref="EdgeConstraint"/>: edges may go from rows of one node table to rows of another.</summary>
    public class EdgeConstraintClause : DynamicBase
    {
        [SchemaProperty(Required = true, Description = "The node table the edge starts at, schema-qualified ([dbo].[Person]) or bare (the edge table's own schema).")]
        [JsonProperty(Order = 1)]
        public string FromTable { get; set; }

        [SchemaProperty(Required = true, Description = "The node table the edge ends at, schema-qualified or bare.")]
        [JsonProperty(Order = 2)]
        public string ToTable { get; set; }
    }
}
