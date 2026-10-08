// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Schema.Utility
{
    /// <summary>
    /// Converts the parsed model's JSON (the single source of truth for the model shape — produced by
    /// <c>JArray.FromObject(...)</c> in <see cref="Schema.Domain.Template"/>) into the XML transport used by
    /// the legacy SQL Server ingest encoding. The XML is shredded with XQuery <c>.nodes()</c>/<c>.value()</c>
    /// (SQL Server 2005+) below the OPENJSON compat cliff. Converting the JSON rather than re-serializing the
    /// object graph keeps the XML shape automatically in step with the model as its properties evolve.
    /// </summary>
    public static class ModelXmlSerializer
    {
        /// <summary>
        /// Converts a JSON array of model objects (e.g. the table-definitions array) to ingest XML with the
        /// given root element. Each array element becomes a repeated <c>&lt;Item&gt;</c> child of the root;
        /// nested JSON arrays become repeated elements named after their JSON property.
        /// </summary>
        /// <param name="modelJsonArray">A JSON array string, e.g. <c>[{...},{...}]</c>.</param>
        /// <param name="rootElement">The XML root element name (e.g. <c>Tables</c>).</param>
        /// <param name="itemElement">The repeated per-element name (e.g. <c>Table</c>).</param>
        public static string ToIngestXml(string modelJsonArray, string rootElement, string itemElement)
        {
            // Wrap the array under the singular item property so Json.NET emits one repeated element per entry,
            // then give the whole thing the requested root element.
            var wrapped = "{\"" + itemElement + "\":" + modelJsonArray + "}";
            var doc = JsonText.ToXDocument(wrapped, rootElement);
            return doc!.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>
        /// Converts a single JSON object (e.g. a kindling table definition) to XML rooted at
        /// <paramref name="rootElement"/>. Nested JSON arrays become repeated elements named by their property.
        /// </summary>
        /// <param name="modelJsonObject">A JSON object string, e.g. <c>{...}</c>.</param>
        /// <param name="rootElement">The XML root element name (e.g. <c>Table</c>).</param>
        public static string ToIngestXmlObject(string modelJsonObject, string rootElement)
        {
            var doc = JsonText.ToXDocument(modelJsonObject, rootElement);
            return doc!.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>
        /// Inverse of <see cref="ToIngestXmlObject"/>: converts the compare-side ingest XML (emitted by the
        /// SQL Server <c>GenerateTableXml</c>/<c>GenerateIndexedViewXml</c> procs below the <c>FOR JSON</c>
        /// binary floor) back to a JSON object string that <c>PlatformDeserializer.DeserializeTable</c> can
        /// materialize into the domain model. The proc emits <c>json:Array="true"</c> (the Json.NET metadata
        /// namespace) on repeated containers so a single-element array does not collapse to an object; every
        /// scalar arrives as a JSON string (XML is typeless) and Newtonsoft coerces it into the typed model.
        /// </summary>
        /// <param name="tableXml">The XML for one object, rooted at a single element (e.g. <c>&lt;Table&gt;</c>).</param>
        public static string FromIngestXml(string tableXml)
        {
            var root = XElement.Parse(tableXml);
            var json = JsonConvert.SerializeXNode(root, Newtonsoft.Json.Formatting.None, omitRootObject: true);

            // ExtendedProperties is an arbitrary-key dict (EP names are sysname — may contain spaces /
            // special chars), which cannot be an XML element name, so the proc emits it attribute-encoded:
            //   <Extensions><ExtendedProperties><p n="Name">Value</p>...</ExtendedProperties></Extensions>
            // SerializeXNode keys JSON by element name, so that lands as {"ExtendedProperties":{"p":{...}|[...]}}.
            // Rebuild the {Name: Value} dict the JSON-tier proc produces directly. Runs everywhere Extensions
            // appears (table + nested columns/indexes/FKs/stats/checks). (B2 — legacy-tier EP round-trip.)
            var obj = JsonText.ParseObject(json);
            RebuildExtendedProperties(obj);
            return obj.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static void RebuildExtendedProperties(JToken node)
        {
            switch (node)
            {
                case JObject o:
                    foreach (var prop in o.Properties().ToList())
                    {
                        if (prop.Name == "ExtendedProperties" && prop.Value is JObject ep && ep["p"] != null)
                            prop.Value = BuildExtendedPropertyDict(ep["p"]);
                        else
                            RebuildExtendedProperties(prop.Value);
                    }
                    break;
                case JArray a:
                    foreach (var item in a)
                        RebuildExtendedProperties(item);
                    break;
            }
        }

        private static JObject BuildExtendedPropertyDict(JToken p)
        {
            var dict = new JObject();
            var entries = p as JArray ?? new JArray(p);
            foreach (var e in entries.OfType<JObject>())
            {
                var name = (string)e["@n"];
                if (name == null) continue;
                dict[name] = e["#text"]?.ToString() ?? "";
            }
            return dict;
        }
    }
}
