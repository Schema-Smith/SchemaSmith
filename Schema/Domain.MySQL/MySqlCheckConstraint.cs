// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.ComponentModel;
using Newtonsoft.Json;

namespace Schema.Domain.MySQL
{
    public class MySqlCheckConstraint : CheckConstraint
    {
        [SchemaProperty(Description = "false declares the constraint NOT ENFORCED: it is kept but not checked. MySQL cannot alter it below 8.0.19, so a change drops and re-creates the constraint. Omitted means enforced. MariaDB has no such state.")]
        [JsonProperty(Order = 100)]
        [DefaultValue(true)]
        public bool Enforced { get; set; } = true;
    }
}
