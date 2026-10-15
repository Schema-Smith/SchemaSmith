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

        // TRANSITIONAL (Comment is the pre-2.8 spelling of Description on MySQL and MariaDB; roadmap WS-13 tracks its removal)
        [SchemaProperty(Deprecated = true, Description = "Deprecated alias of Description. Still honoured on deploy; --Validate reports it (SS-DESC-002).")]
        [JsonProperty(Order = 102)]
        public string Comment { get; set; }
    }
}
