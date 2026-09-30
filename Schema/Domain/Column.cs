// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using Schema.Delivery;

namespace Schema.Domain
{
    public class Column : DynamicBase, IDeliverableColumn
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; } = "";

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 2)]
        public string DataType { get; set; } = "";

        // Whether the package said anything at all is itself information: on a computed or generated column an
        // omitted Nullable lets the engine decide, where an explicit false asks for NOT NULL. The deploy hands the
        // procedures the serialized model, so the omission has to survive serialization to be seen there.
        [JsonProperty(Order = 3, DefaultValueHandling = DefaultValueHandling.Include)]
        public bool Nullable
        {
            get => _nullable;
            set
            {
                _nullable = value;
                NullableDeclared = true;
            }
        }

        private bool _nullable;

        [JsonIgnore]
        public bool NullableDeclared { get; private set; }

        // A plain column's false is the default and stays out of package files, as it always has. A computed or
        // generated column's false is not a default -- omitted means the engine decides -- so it is always written.
        public bool ShouldSerializeNullable() => NullableDeclared && (Nullable || IsDerivedColumn());

        protected virtual bool IsDerivedColumn() => false;

        [JsonProperty(Order = 4)]
        public string Default { get; set; }

        [JsonProperty(Order = 90)]
        public string ShouldApplyExpression { get; set; }

        // Labels a conditional variant: the intent behind its ShouldApplyExpression,
        // echoed in quench log messages when the variant applies.
        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 92)]
        public string VariantName { get; set; }

        [JsonProperty(Order = 91)]
        public string OldName { get; set; }
    }
}
