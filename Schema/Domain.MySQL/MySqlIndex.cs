// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.ComponentModel;
using Newtonsoft.Json;

namespace Schema.Domain.MySQL
{
    public class MySqlIndex : Index
    {
        [SchemaProperty(Pattern = "BTREE|HASH|FULLTEXT|SPATIAL")]
        [JsonProperty(Order = 100)]
        [DefaultValue("BTREE")]
        public string IndexType { get; set; } = "BTREE";

        [JsonProperty(Order = 101)]
        [DefaultValue(true)]
        public bool Visible { get; set; } = true;

        [JsonProperty(Order = 102)]
        public string Comment { get; set; }
    }
}
