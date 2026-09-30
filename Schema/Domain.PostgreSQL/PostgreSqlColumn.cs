// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;

namespace Schema.Domain.PostgreSQL
{
    public class PostgreSqlColumn : Column
    {
        [JsonProperty(Order = 100)]
        public string GenerationExpression { get; set; }

        protected override bool IsDerivedColumn() => !string.IsNullOrWhiteSpace(GenerationExpression);

        [JsonProperty(Order = 101)]
        public string Collation { get; set; }

        [JsonProperty(Order = 102)]
        public string Generated { get; set; }

        [JsonProperty(Order = 103)]
        public bool Virtual { get; set; }

        [JsonProperty(Order = 104)]
        public string Storage { get; set; }

        [JsonProperty(Order = 105)]
        public string Compression { get; set; }

        [JsonProperty(Order = 106)]
        public string CheckExpression { get; set; }
    }
}
