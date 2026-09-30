// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests;

/// <summary>
/// Table JSON in the shape the deploy actually sends the procedures. The CLI never hands them a package file: it
/// deserializes the tables into the domain model and serializes that (Template.TableSchema). A hand-written
/// fixture can therefore carry -- or lack -- keys the product never would, and a test built on one can pass over
/// a defect the product hits. That happened: the SQL Server rule for an omitted Nullable on a computed column was
/// proven only by fixtures that omitted the key, while the product always sent "Nullable": false.
/// </summary>
internal static class DeployJson
{
    public static string ThroughTheModel(string tableJson, Platform platform)
    {
        var token = JToken.Parse(tableJson);
        IEnumerable<JToken> items = token is JArray array ? array : new[] { token };
        var tables = items
            .Select(t => PlatformDeserializer.DeserializeTable(t.ToString(), platform))
            .ToList();
        return JArray.FromObject(tables).ToString();
    }
}
