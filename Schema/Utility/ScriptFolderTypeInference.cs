// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Schema.Domain;

namespace Schema.Utility;

public static class ScriptFolderTypeInference
{
    private static readonly Dictionary<string, ScriptObjectType> FolderNameToType =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["Views"] = ScriptObjectType.Views,
        ["Functions"] = ScriptObjectType.Functions,
        ["Procedures"] = ScriptObjectType.Procedures,
        ["Triggers"] = ScriptObjectType.Triggers,
        ["Schemas"] = ScriptObjectType.Schemas,
        ["DataTypes"] = ScriptObjectType.DataTypes,
        ["FullTextCatalogs"] = ScriptObjectType.FullTextCatalogs,
        ["FullTextStopLists"] = ScriptObjectType.FullTextStopLists,
        ["XMLSchemaCollections"] = ScriptObjectType.XMLSchemaCollections,
        ["DDLTriggers"] = ScriptObjectType.DDLTriggers,
        ["Synonyms"] = ScriptObjectType.Synonyms,
        ["SecurityPolicies"] = ScriptObjectType.SecurityPolicies,
        ["Domain Types"] = ScriptObjectType.DomainTypes,
        ["Enum Types"] = ScriptObjectType.EnumTypes,
        ["Composite Types"] = ScriptObjectType.CompositeTypes,
        ["Trigger Functions"] = ScriptObjectType.TriggerFunctions,
        ["Window Functions"] = ScriptObjectType.WindowFunctions,
        ["Aggregates"] = ScriptObjectType.Aggregates,
        ["Sequences"] = ScriptObjectType.Sequences,
        ["Rules"] = ScriptObjectType.Rules,
        ["Collations"] = ScriptObjectType.Collations,
        ["Publications"] = ScriptObjectType.Publications,
        ["Range Types"] = ScriptObjectType.RangeTypes,
        ["Text Search Configurations"] = ScriptObjectType.TextSearchConfigurations,
        ["Operators"] = ScriptObjectType.Operators,
        ["Casts"] = ScriptObjectType.Casts,
        ["Event Triggers"] = ScriptObjectType.EventTriggers,
        ["Events"] = ScriptObjectType.Events,
    };

    public static ScriptObjectType InferFromFolderName(string folderPath)
    {
        return FolderNameToType.TryGetValue(folderPath, out var type) ? type : ScriptObjectType.None;
    }
}
