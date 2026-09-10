// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace Schema.Configuration;

/// <summary>
/// Every configuration key the tools read, as constants.
/// <para>Product code reads through these rather than through string literals, so the accepted-key
/// set and the read set are the same symbols instead of two lists that happen to agree today. A
/// guard test scans the product sources and fails if a <c>config["…"]</c> literal names a key that is
/// not registered here, which is what keeps this a contract rather than a promise.</para>
/// </summary>
public static class SettingsKeys
{
    // ---- Shared across tools -------------------------------------------------------------------
    public const string ScriptTokens = "ScriptTokens";

    /// <summary>SchemaQuench deployment target.</summary>
    public static class Target
    {
        public const string Section = "Target";
        public const string Server = "Target:Server";
        public const string User = "Target:User";
        public const string Password = "Target:Password";
        public const string Port = "Target:Port";
        public const string Platform = "Target:Platform";
        public const string IntegratedSecurity = "Target:IntegratedSecurity";
        public const string SecondaryServers = "Target:SecondaryServers";
        public const string ConnectionProperties = "Target:ConnectionProperties";
        public const string Templates = "Target:Templates";
        public const string Databases = "Target:Databases";
        public const string Schemas = "Target:Schemas";
        public const string TemplateTargets = "Target:TemplateTargets";
    }

    /// <summary>
    /// Members of a single <c>Target:TemplateTargets:&lt;Template&gt;</c> entry. Read relative to an
    /// already-resolved per-template section, so these are bare names rather than full paths.
    /// </summary>
    public static class TemplateTarget
    {
        public const string Databases = "Databases";
        public const string Schemas = "Schemas";
        public const string CreateIfMissing = "CreateIfMissing";
    }

    /// <summary>SchemaTongs / DataTongs extraction source.</summary>
    public static class Source
    {
        public const string Section = "Source";
        public const string Server = "Source:Server";
        public const string User = "Source:User";
        public const string Password = "Source:Password";
        public const string Port = "Source:Port";
        public const string Platform = "Source:Platform";
        public const string Database = "Source:Database";
        public const string Schema = "Source:Schema";
        public const string IntegratedSecurity = "Source:IntegratedSecurity";
        public const string ConnectionProperties = "Source:ConnectionProperties";
    }

    /// <summary>Product / template identity used by the extraction tools.</summary>
    public static class ProductKeys
    {
        public const string Section = "Product";
        public const string Name = "Product:Name";
        public const string Path = "Product:Path";
        public const string CheckConstraintStyle = "Product:CheckConstraintStyle";
        /// <summary>Extraction ordering for a table's object lists: Name (default) or Physical.</summary>
        public const string ObjectOrder = "Product:ObjectOrder";
        /// <summary>When true (default), a re-extract keeps the order the file being replaced already had.</summary>
        public const string PreserveExistingOrder = "Product:PreserveExistingOrder";
    }

    public static class TemplateKeys
    {
        public const string Section = "Template";
        public const string Name = "Template:Name";
        public const string SchemaIdentificationScript = "Template:SchemaIdentificationScript";
    }

    /// <summary>What SchemaTongs / DataTongs extract.</summary>
    public static class ShouldCast
    {
        public const string Section = "ShouldCast";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string Aggregates = "ShouldCast:Aggregates";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string Catalogs = "ShouldCast:Catalogs";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string Collations = "ShouldCast:Collations";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string CompositeTypes = "ShouldCast:CompositeTypes";
        [ReadBy(SettingsTool.DataTongs)]
        public const string ConfigureDataDelivery = "ShouldCast:ConfigureDataDelivery";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string DdlTriggers = "ShouldCast:DDLTriggers";
        [ReadBy(SettingsTool.DataTongs)]
        public const string DeliveryEncoding = "ShouldCast:DeliveryEncoding";
        [ReadBy(SettingsTool.DataTongs)]
        public const string DisableRules = "ShouldCast:DisableRules";
        [ReadBy(SettingsTool.DataTongs)]
        public const string DisableTriggers = "ShouldCast:DisableTriggers";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string DomainTypes = "ShouldCast:DomainTypes";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string EnumTypes = "ShouldCast:EnumTypes";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string Events = "ShouldCast:Events";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string Functions = "ShouldCast:Functions";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string IndexedViews = "ShouldCast:IndexedViews";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string MaterializedViews = "ShouldCast:MaterializedViews";
        [ReadBy(SettingsTool.DataTongs)]
        public const string MergeDelete = "ShouldCast:MergeDelete";
        [ReadBy(SettingsTool.DataTongs)]
        public const string MergeUpdate = "ShouldCast:MergeUpdate";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string ObjectList = "ShouldCast:ObjectList";
        [ReadBy(SettingsTool.DataTongs)]
        public const string OutputContentFiles = "ShouldCast:OutputContentFiles";
        [ReadBy(SettingsTool.DataTongs)]
        public const string OutputScripts = "ShouldCast:OutputScripts";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string Procedures = "ShouldCast:Procedures";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string Publications = "ShouldCast:Publications";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string Rules = "ShouldCast:Rules";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string SaveInvalidScripts = "ShouldCast:SaveInvalidScripts";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string Schemas = "ShouldCast:Schemas";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string ScriptDynamicDependencyRemovalForFunctions = "ShouldCast:ScriptDynamicDependencyRemovalForFunctions";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string Sequences = "ShouldCast:Sequences";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string StopLists = "ShouldCast:StopLists";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string Synonyms = "ShouldCast:Synonyms";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string TableTriggers = "ShouldCast:TableTriggers";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string Tables = "ShouldCast:Tables";
        [ReadBy(SettingsTool.DataTongs)]
        public const string TokenizeScripts = "ShouldCast:TokenizeScripts";
        [ReadBy(SettingsTool.DataTongs)]
        public const string UpdateDescendents = "ShouldCast:UpdateDescendents";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string UserDefinedTypes = "ShouldCast:UserDefinedTypes";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string ValidateScripts = "ShouldCast:ValidateScripts";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string Views = "ShouldCast:Views";
        [ReadBy(SettingsTool.SchemaTongs)]
        public const string XmlSchemaCollections = "ShouldCast:XMLSchemaCollections";
    }

    public static class OrphanHandling
    {
        public const string Section = "OrphanHandling";
        public const string Mode = "OrphanHandling:Mode";
    }

    public const string FolderMapping = "FolderMapping";
    public const string LogHygiene = "LogHygiene";

    // ---- SchemaQuench top-level ------------------------------------------------------------------
    public const string SchemaPackagePath = "SchemaPackagePath";
    public const string MaxThreads = "MaxThreads";
    public const string WhatIfOnly = "WhatIfONLY";
    public const string RunScriptsTwice = "RunScriptsTwice";
    public const string DropTablesRemovedFromProduct = "DropTablesRemovedFromProduct";
    public const string DropSchemaBoundDependents = "DropSchemaBoundDependents";
    public const string DropEventsRemovedFromProduct = "DropEventsRemovedFromProduct";
    public const string DropUnknownIndexes = "DropUnknownIndexes";
    public const string DropColumnsRemovedFromProduct = "DropColumnsRemovedFromProduct";
    public const string DropForeignKeysRemovedFromProduct = "DropForeignKeysRemovedFromProduct";
    public const string DropCheckConstraintsRemovedFromProduct = "DropCheckConstraintsRemovedFromProduct";
    public const string DropExcludeConstraintsRemovedFromProduct = "DropExcludeConstraintsRemovedFromProduct";
    public const string DropStatisticsRemovedFromProduct = "DropStatisticsRemovedFromProduct";
    public const string DropIndexesRemovedFromProduct = "DropIndexesRemovedFromProduct";

    /// <summary>
    /// MariaDB only, and one of the two Drop...RemovedFromProduct flags that default to FALSE
    /// (DropEventsRemovedFromProduct is the other; DropUnknownIndexes also defaults off). See the property
    /// description on Table for why -- a package that predates periods, or was extracted below
    /// 11.4, cannot declare the periods its table actually has.
    /// </summary>
    public const string DropPeriodsRemovedFromProduct = "DropPeriodsRemovedFromProduct";
    // Flat keys, not a bound RebuildPolicy section: every other setting here is a flat string key and
    // there is no section-binding facility to reuse. The three compose ONE policy object at the env
    // tier, which then competes with the product/template/table levels as a whole.
    public const string RebuildPolicyMode = "RebuildPolicyMode";
    public const string RebuildPolicyThreshold = "RebuildPolicyThreshold";
    public const string RebuildPolicyOnOrderMismatch = "RebuildPolicyOnOrderMismatch";

    /// <summary>
    /// MariaDB only. <c>KEEP</c> opts into altering a system-versioned table, which rewrites its stored
    /// history to the new shape. Anything else leaves the engine default, which refuses the change.
    /// </summary>
    public const string SystemVersioningAlterHistory = "SystemVersioningAlterHistory";
    public const string PreventDrop = "PreventDrop";
    public const string UpdateTables = "UpdateTables";
    public const string KindleTheForge = "KindleTheForge";
    public const string ForceReKindle = "ForceReKindle";
    public const string CheckpointDirectory = "CheckpointDirectory";
    public const string DeliverData = "DeliverData";
    public const string VerboseLogging = "VerboseLogging";
    public const string FailureContextLines = "FailureContextLines";
    public const string BottleneckThresholdMs = "BottleneckThresholdMs";
    public const string TrackRunOnceMigrations = "TrackRunOnceMigrations";
    public const string PruneObsoleteMigrationTracking = "PruneObsoleteMigrationTracking";
    public const string UnsupportedFeaturePolicy = "Target:UnsupportedFeaturePolicy";
    public const string CompatEncoding = "Target:CompatEncoding";
    public const string SourceCompatEncoding = "Source:CompatEncoding";
    public const string ArtifactPath = "ArtifactPath";
    public const string ScrubArtifacts = "ScrubArtifacts";

    // ---- DataTongs -------------------------------------------------------------------------------
    public const string ContentPath = "ContentPath";
    public const string ScriptPath = "ScriptPath";
    public const string TemplatePath = "TemplatePath";
    /// <summary>DataTongs' list of tables to extract (array-valued).</summary>
    public const string TablesToExtract = "Tables";

    // ---- SchemaShears ----------------------------------------------------------------------------
    public const string SourcePath = "SourcePath";
    public const string ManifestPath = "ManifestPath";
    public const string AlwaysIncludePath = "AlwaysIncludePath";
    public const string OutputPath = "OutputPath";
    public const string Zip = "Zip";
    public const string AllowDrops = "AllowDrops";

}
