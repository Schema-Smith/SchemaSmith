// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Schema.Domain
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ScriptObjectType
    {
        None,               // Before Scripts, After Scripts, Table Data, custom folders
        Views,
        Functions,
        Procedures,
        Triggers,
        Schemas,
        // SQL Server
        DataTypes,
        FullTextCatalogs,
        FullTextStopLists,
        XMLSchemaCollections,
        DDLTriggers,
        IndexedViews,
        Synonyms,
        // #323. Split out from Views/Functions so they can sit in their own folder on the
        // AfterTablesObjects slot: a SCHEMABINDING module has to be recreated AFTER the table work
        // that required dropping it, and the ordinary Views folder runs before tables on SQL Server.
        SchemaBoundViews,
        SchemaBoundFunctions,
        // PostgreSQL
        DomainTypes,
        EnumTypes,
        CompositeTypes,
        TriggerFunctions,
        WindowFunctions,
        Aggregates,
        Sequences,
        Rules,
        MaterializedViews,
        Collations,
        Publications,
        // MySQL
        Events,
        // SQL Server, after the rest so no existing value moves.
        SecurityPolicies,
        // PostgreSQL, likewise.
        RangeTypes,
        TextSearchConfigurations,
        Operators,
        Casts,
        EventTriggers,
        // MariaDB, likewise.
        Packages
    }
}
