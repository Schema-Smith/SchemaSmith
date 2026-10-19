// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Schema.Domain.SqlServer
{
    /// <summary>
    /// What a selective XML index (SQL Server 2012 SP1+) promotes: the paths it indexes, in order, and the namespaces
    /// those paths use. A secondary selective index names one of these paths with <see cref="XmlIndex.ForPath"/>.
    /// </summary>
    public class SelectiveXmlIndex
    {
        [SchemaProperty(Description = "The WITH XMLNAMESPACES declarations the paths use. A namespace with no Prefix is the DEFAULT namespace.")]
        [JsonProperty(Order = 1, NullValueHandling = NullValueHandling.Ignore)]
        public List<SelectiveXmlNamespace> Namespaces { get; set; }

        [SchemaProperty(Required = true, Description = "The promoted paths, in the order they are declared.")]
        [JsonProperty(Order = 2)]
        public List<SelectiveXmlPath> Paths { get; set; } = [];
    }

    public class SelectiveXmlNamespace
    {
        [SchemaProperty(Description = "The prefix the paths use. Omitted, this is the DEFAULT namespace.")]
        [JsonProperty(Order = 1, NullValueHandling = NullValueHandling.Ignore)]
        public string Prefix { get; set; }

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 2)]
        public string Uri { get; set; }
    }

    public class SelectiveXmlPath
    {
        [SchemaProperty(Required = true, Description = "The path's name, which a secondary selective index names in ForPath.")]
        [JsonProperty(Order = 1)]
        public string Name { get; set; }

        [SchemaProperty(Required = true, Description = "The XQuery path, e.g. /order/id.")]
        [JsonProperty(Order = 2)]
        public string Path { get; set; }

        [SchemaProperty(Description = "Omitted, the engine infers it. An XQuery type (\"xs:string\", \"xs:double\", …) or \"node()\" maps the path AS XQUERY; anything else is a SQL type (\"DECIMAL(10,2)\", \"NVARCHAR(40)\") and maps it AS SQL.")]
        [JsonProperty(Order = 3, NullValueHandling = NullValueHandling.Ignore)]
        public string Type { get; set; }

        [SchemaProperty(Description = "MAXLENGTH for an xs:string path.")]
        [JsonProperty(Order = 4, NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxLength { get; set; }

        [SchemaProperty(Description = "SINGLETON: the path occurs at most once per document.")]
        [JsonProperty(Order = 5)]
        public bool SingletonCardinality { get; set; }
    }
}
