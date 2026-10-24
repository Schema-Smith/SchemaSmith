// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Schema.Domain
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum TemplateQuenchSlot : ushort
    {
        Before,
        Objects,
        BetweenTablesAndKeys,
        AfterTablesScripts,
        AfterTablesObjects,
        TableData,
        After,
        None,
        // D13: runs after the baseline validation and before the first object pass, every deploy, one pass. Appended so no
        // existing value moves.
        Setup
    }
}
