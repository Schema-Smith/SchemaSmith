// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.Domain
{
    public class Template
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; } = "";

        // Not schema-required: a template may identify its target via SchemaIdentificationScript
        // instead (and on MySQL that field is a backward-compat alias for this one).
        [JsonProperty(Order = 2)]
        public string DatabaseIdentificationScript { get; set; }

        /// <summary>
        /// Optional: re-targets which database the <see cref="DatabaseIdentificationScript"/>
        /// enumeration query connects to. Empty/absent (the default) uses the platform init
        /// database (<c>master</c> / <c>postgres</c> / <c>information_schema</c>) — today's
        /// behavior. Point it at a control-plane registry database to read a tenant roster from a
        /// registry table at enumeration time; this is the only way to reach such a table on
        /// PostgreSQL, which cannot cross-database-query. Token-resolvable for per-environment
        /// control. Affects ONLY the enumeration connection — provisioning and existence checks
        /// stay on the init database, and <see cref="SchemaIdentificationScript"/> (schema
        /// discovery) already runs against the target database. (Serialized at Order 16 to pair
        /// conceptually with DatabaseIdentificationScript without renumbering existing properties.)
        /// </summary>
        [JsonProperty(Order = 16, NullValueHandling = NullValueHandling.Ignore)]
        public string IdentificationDatabase { get; set; }

        /// <summary>
        /// Schema templates (SQL Server / PostgreSQL only): a query returning one column,
        /// N rows; each row is a schema name to iterate over. When set, the engine fans the
        /// template out across the returned schemas, exposing the active name as the
        /// {{SchemaName}} token. Mirrors <see cref="DatabaseIdentificationScript"/> semantics,
        /// one level down.
        ///
        /// PLATFORM-SPECIFIC NOTE: This JSON field name has historically been a backward-compat
        /// alias for <see cref="DatabaseIdentificationScript"/> on MySQL packages (MySQL conflates
        /// "schema" and "database"). To preserve that backward compat without breaking existing
        /// MySQL packages, <see cref="Load(string, Product)"/> performs a platform-aware migration
        /// after deserialization: on MySQL, a value found here is moved into
        /// <see cref="DatabaseIdentificationScript"/> (when that is null) and a deprecation warning
        /// is logged. On SQL Server / PostgreSQL, the value drives the new schema-template
        /// fan-out feature unchanged. The schema-template feature is intentionally not offered on
        /// MySQL (no namespace-inside-database concept) — see design doc §2 for rationale.
        /// </summary>
        [JsonProperty(Order = 11, NullValueHandling = NullValueHandling.Ignore)]
        public string SchemaIdentificationScript { get; set; }

        /// <summary>
        /// True when this template fans out across schemas (i.e. <see cref="SchemaIdentificationScript"/>
        /// is non-empty after platform-aware migration). False for regular templates.
        /// </summary>
        [JsonIgnore]
        public bool IsSchemaTemplate => !string.IsNullOrWhiteSpace(SchemaIdentificationScript);

        [JsonProperty(Order = 3)]
        public string VersionStampScript { get; set; }

        [JsonProperty(Order = 4)]
        public bool IndexOnlyTableQuenches { get; set; }

        [JsonProperty(Order = 5)]
        public List<TemplateFolder> ScriptFolders { get; } = [];

        [JsonProperty(Order = 6)]
        public Dictionary<string, string> ScriptTokens { get; set; } = [];

        [JsonProperty(Order = 7)]
        [DefaultValue(true)]
        [SchemaProperty(Platforms = [Platform.SqlServer, Platform.PostgreSQL])]
        public bool UpdateFillFactor { get; set; } = true;

        [JsonProperty(Order = 8)]
        public string BaselineValidationScript { get; set; }

        /// <summary>
        /// When true (default), the template fails the run if discovery returns zero targets —
        /// zero matching databases for a regular template, or zero matching <c>(database, schema)</c>
        /// pairs for a schema template. When false, an empty discovery is treated as a no-op for
        /// this template and the run continues to subsequent templates.
        /// <para>This property replaces the prior <c>Required</c> field. The rename is a breaking
        /// change in the v2.1 Schema Templates release; user-authored <c>Template.json</c> files
        /// that still use <c>Required</c> need to be updated. Deserialization now rejects unknown
        /// package properties, so an unmigrated file fails to load with an error naming
        /// <c>Required</c> and the offending file — pointing straight at the rename — rather than
        /// the old silent default-<c>true</c> fallback. See the CHANGELOG for migration guidance.</para>
        /// </summary>
        [JsonProperty(Order = 9)]
        [DefaultValue(true)]
        public bool RequireAtLeastOneTarget { get; set; } = true;

        [JsonProperty(Order = 10)]
        public bool SkipIfReadOnly { get; set; }

        /// <summary>
        /// Schema templates only: when true and a schema returned by
        /// <see cref="SchemaIdentificationScript"/> does not exist on the target database, the
        /// engine emits <c>CREATE SCHEMA</c> for it before running the iteration. Default-false
        /// forces explicit opt-in — a typo in the discovery query should not silently create
        /// unintended schemas. Ignored on regular templates (warned if set non-default).
        /// </summary>
        [JsonProperty(Order = 12)]
        public bool CreateSchemaIfMissing { get; set; } = false;

        /// <summary>
        /// Schema templates only: when false, this template's per-schema work units run serially
        /// even though the global thread pool may have capacity. Escape hatch for templates that
        /// touch a shared resource and must not parallelize. Ignored on regular templates
        /// (warned if set non-default).
        /// </summary>
        [JsonProperty(Order = 13)]
        [DefaultValue(true)]
        public bool AllowParallel { get; set; } = true;

        /// <summary>
        /// Schema templates only: when true (default), a single schema iteration's failure does
        /// not halt the others — remaining iterations continue and the product run exits non-zero.
        /// When false, on first iteration failure the dispatcher stops dispatching new iterations,
        /// in-flight iterations drain, and subsequent templates in <c>TemplateOrder</c> do not run.
        /// Ignored on regular templates (warned if set non-default).
        /// </summary>
        [JsonProperty(Order = 14)]
        [DefaultValue(true)]
        public bool ContinueOnSchemaFailure { get; set; } = true;

        /// <summary>
        /// Failure-isolation parity at the database level — applies to BOTH regular and schema
        /// templates. When true (default), one database's failure does not abort the product run
        /// across other databases. When false, the run aborts at the first DB-level failure.
        /// </summary>
        [JsonProperty(Order = 15)]
        [DefaultValue(true)]
        public bool ContinueOnDatabaseFailure { get; set; } = true;

        [JsonProperty(Order = 16)]
        public bool? DropTablesRemovedFromProduct { get; set; }

        [JsonProperty(Order = 17)]
        public bool? DropUnknownIndexes { get; set; }

        // #323. Opt-in: a column change blocked by a SCHEMABINDING module drops the module and lets
        // the after-tables object pass recreate it from this package. Requires those modules to live
        // in a folder on the AfterTablesObjects slot, which SchemaTongs arranges on extraction.
        // DROPPING DISCARDS PERMISSIONS -- SchemaSmith manages none, so the script must re-grant.
        [JsonProperty(Order = 25)]
        [SchemaProperty(Platforms = [Platform.SqlServer])]
        public bool? DropSchemaBoundDependents { get; set; }

        // #417. The filegroup CDC change tables go on, for every EnableCDC table in this template that does not
        // declare its own CdcFilegroup. Null means unmanaged: an existing placement is never touched.
        [JsonProperty(Order = 26, NullValueHandling = NullValueHandling.Ignore)]
        [SchemaProperty(Platforms = [Platform.SqlServer], MaxLength = 128,
            Description = "Default filegroup for CDC change tables of EnableCDC tables that declare no CdcFilegroup of their own. A table on a different filegroup gets a new capture instance there; the old one keeps its history. Unset leaves existing placement alone.")]
        public string CdcFilegroup { get; set; }

        [JsonProperty(Order = 18)]
        public bool? DropColumnsRemovedFromProduct { get; set; }

        [JsonProperty(Order = 19)]
        public bool? DropForeignKeysRemovedFromProduct { get; set; }

        [JsonProperty(Order = 20)]
        public bool? DropCheckConstraintsRemovedFromProduct { get; set; }

        [JsonProperty(Order = 21)]
        [SchemaProperty(Platforms = [Platform.PostgreSQL])]
        public bool? DropExcludeConstraintsRemovedFromProduct { get; set; }

        [JsonProperty(Order = 22)]
        [SchemaProperty(Platforms = [Platform.SqlServer, Platform.PostgreSQL])]
        public bool? DropStatisticsRemovedFromProduct { get; set; }

        [JsonProperty(Order = 23)]
        public bool? DropIndexesRemovedFromProduct { get; set; }

        /// <summary>
        /// Template tier of the rebuild-policy cascade. Null inherits from the product (or the
        /// environment). Unlike the <c>Drop*RemovedFromProduct</c> flags either side of it, the
        /// levels do not combine — the nearest declared policy wins whole
        /// (<c>ProductQuench.ResolveCascadedPolicy</c>).
        /// </summary>
        [JsonProperty(Order = 24)]
        public RebuildPolicy RebuildPolicy { get; set; }

        [JsonIgnore]
        public Product Product { get; set; }

        [JsonIgnore]
        public List<Table> Tables { get; } = [];

        /// <summary>
        /// Component files (tables / materialized views / indexed views) that
        /// <see cref="InstanceLoad"/> skipped because they could not be parsed as JSON at all —
        /// populated only when loading with <c>tolerateComponentLoadErrors: true</c>. PackageLoader
        /// turns each entry into an SS-LOAD-001 finding so a skip is always reported, never silently
        /// dropped. Empty on the deploy path, which never tolerates a component load failure.
        /// </summary>
        [JsonIgnore]
        public List<ComponentLoadError> ComponentLoadErrors { get; } = [];

        /// <summary>
        /// File-token resolution failures collected instead of thrown when <see cref="Load"/> is
        /// called with <c>tolerateFileTokenErrors: true</c> (--Validate's lenient load). Empty on
        /// the deploy path, which never tolerates an unresolvable file token. Mirrors
        /// <see cref="Product.FileTokenErrors"/> — PackageLoader turns each entry into an
        /// SS-TOK-004 finding.
        /// </summary>
        [JsonIgnore]
        public List<FileTokenError> FileTokenErrors { get; } = [];

        /// <summary>
        /// Deprecated aliases this template's load migrated. Recorded on every load path (it costs nothing
        /// and deploy ignores it); <c>--Validate</c> reports each one through <c>DeprecationCheck</c>.
        /// </summary>
        [JsonIgnore]
        public List<DeprecationNotice> DeprecationNotices { get; } = [];

        [JsonIgnore]
        public string TableSchema { get; set; } = "";

        // A2: XML twin of TableSchema — the same model encoded as ingest XML, so an author can shred the
        // model with XQuery (.nodes()/.value()) on a below-cliff SQL Server where OPENJSON parse-errors.
        // Computed from TableSchema (the single source of truth), so it stays in step with the model.
        [JsonIgnore]
        public string TableXml => ModelXmlSerializer.ToIngestXml(
            string.IsNullOrWhiteSpace(TableSchema) ? "[]" : TableSchema, "Tables", "Table");

        [JsonIgnore]
        public List<PostgreSqlMaterializedView> MaterializedViews { get; } = [];

        [JsonIgnore]
        public string MaterializedViewSchema { get; set; } = "[]";

        [JsonIgnore]
        public string MaterializedViewXml => ModelXmlSerializer.ToIngestXml(
            string.IsNullOrWhiteSpace(MaterializedViewSchema) ? "[]" : MaterializedViewSchema, "MaterializedViews", "MaterializedView");

        // Scheduled events, MySQL/MariaDB. Declarative form of what the Events/ folder has always held
        // as raw scripts -- both are supported, and a .sql file there keeps working untouched.
        [JsonIgnore]
        public List<MySqlEvent> Events { get; } = [];

        // Domain types, PostgreSQL. Same additive shape: Domain Types/ holds .json (declared) and .sql
        // (scripted, unchanged), so no existing package changes behaviour.
        [JsonIgnore]
        public List<PostgreSqlDomainType> DomainTypes { get; } = [];

        [JsonIgnore]
        public string DomainTypeSchema { get; set; } = "[]";

        // Enum types, PostgreSQL. Same additive shape as Events: Enum Types/ holds .json (declared) and
        // .sql (scripted, unchanged).
        [JsonIgnore]
        public List<PostgreSqlEnumType> EnumTypes { get; } = [];

        [JsonIgnore]
        public string EnumTypeSchema { get; set; } = "[]";

        // Sequences, PostgreSQL. Same additive shape: Sequences/ holds .json (declared) and .sql (scripted).
        [JsonIgnore]
        public List<PostgreSqlSequence> Sequences { get; } = [];

        [JsonIgnore]
        public string SequenceSchema { get; set; } = "[]";

        [JsonIgnore]
        public string EventSchema { get; set; } = "[]";

        [JsonIgnore]
        public List<SqlServerIndexedView> IndexedViews { get; } = [];

        [JsonIgnore]
        public string IndexedViewSchema { get; set; } = "[]";

        [JsonIgnore]
        public string IndexedViewXml => ModelXmlSerializer.ToIngestXml(
            string.IsNullOrWhiteSpace(IndexedViewSchema) ? "[]" : IndexedViewSchema, "IndexedViews", "IndexedView");

        [JsonIgnore]
        public string FilePath { get; set; }

        [JsonIgnore]
        public Dictionary<string, string> QueryTokens { get; set; } = [];

        [JsonIgnore]
        public Dictionary<string, string> NonQueryTokens { get; set; } = [];

        [JsonIgnore]
        public Dictionary<string, string> LoggableTokens { get; set; } = [];

        /// <summary>
        /// Resolved per-token <see cref="TokenScope"/> map, populated by
        /// <see cref="ResolveTokenScopes"/>. Null until the walk runs; callers should treat any
        /// "missing" entry as <see cref="TokenScope.PerDb"/> for query tokens (today's default)
        /// or <see cref="TokenScope.PerProduct"/> for static tokens.
        /// </summary>
        [JsonIgnore]
        private Dictionary<string, TokenScope> _tokenScopes;

        /// <summary>
        /// Database-scoped ObjectTypes that schema templates may not declare ScriptFolders for —
        /// these cannot fan out per schema iteration and must live on a regular template that runs
        /// earlier in TemplateOrder. See <see cref="ValidateSchemaTemplateRules"/> rule 2.
        /// </summary>
        private static readonly HashSet<ScriptObjectType> DisallowedSchemaTemplateObjectTypes = new()
        {
            ScriptObjectType.Schemas,
            ScriptObjectType.DDLTriggers,
            ScriptObjectType.FullTextCatalogs,
            ScriptObjectType.FullTextStopLists
        };

        /// <summary>
        /// The path used for logging (stripped of long path prefix).
        /// </summary>
        [JsonIgnore]
        public string LogPath => LongPathSupport.StripLongPathPrefix(FilePath);

        #region Computed Script Collections

        // Per-slot folder accessors — the single source of slot-membership truth. The script
        // accessors below flatten these, and the folder-gate path (#260) filters these per target
        // before flattening, so gating and dispatch always agree on which folder feeds which slot.
        [JsonIgnore]
        public List<TemplateFolder> BeforeFolders => FoldersInSlots(TemplateQuenchSlot.Before);

        [JsonIgnore]
        public List<TemplateFolder> ObjectFolders => FoldersInSlots(TemplateQuenchSlot.Objects);

        [JsonIgnore]
        public List<TemplateFolder> BetweenTablesAndKeysFolders => FoldersInSlots(TemplateQuenchSlot.BetweenTablesAndKeys);

        [JsonIgnore]
        public List<TemplateFolder> AfterTableFolders => FoldersInSlots(TemplateQuenchSlot.AfterTablesScripts);

        [JsonIgnore]
        public List<TemplateFolder> AfterTablesObjectFolders => FoldersInSlots(TemplateQuenchSlot.Objects, TemplateQuenchSlot.AfterTablesObjects);

        [JsonIgnore]
        public List<TemplateFolder> TableDataFolders => FoldersInSlots(TemplateQuenchSlot.TableData);

        [JsonIgnore]
        public List<TemplateFolder> AfterFolders => FoldersInSlots(TemplateQuenchSlot.After);

        private List<TemplateFolder> FoldersInSlots(params TemplateQuenchSlot[] slots) =>
            ScriptFolders.Where(f => slots.Contains(f.QuenchSlot)).ToList();

        [JsonIgnore]
        public List<SqlScript> BeforeScripts => BeforeFolders.SelectMany(f => f.Scripts).ToList();

        [JsonIgnore]
        public List<SqlScript> ObjectScripts => ObjectFolders.SelectMany(f => f.Scripts).ToList();

        [JsonIgnore]
        public List<SqlScript> BetweenTablesAndKeysScripts => BetweenTablesAndKeysFolders.SelectMany(f => f.Scripts).ToList();

        [JsonIgnore]
        public List<SqlScript> AfterTableScripts => AfterTableFolders.SelectMany(f => f.Scripts).ToList();

        [JsonIgnore]
        public List<SqlScript> AfterTablesObjectScripts => AfterTablesObjectFolders.SelectMany(f => f.Scripts).ToList();

        [JsonIgnore]
        public List<SqlScript> TableDataScripts => TableDataFolders.SelectMany(f => f.Scripts).ToList();

        [JsonIgnore]
        public List<SqlScript> AfterScripts => AfterFolders.SelectMany(f => f.Scripts).ToList();

        #endregion

        public Template Clone()
        {
            var clone = new Template
            {
                Name = Name,
                FilePath = FilePath,
                TableSchema = TableSchema,
                DatabaseIdentificationScript = DatabaseIdentificationScript,
                IdentificationDatabase = IdentificationDatabase,
                VersionStampScript = VersionStampScript,
                Product = Product,
                ScriptTokens = new Dictionary<string, string>(ScriptTokens),
                NonQueryTokens = new Dictionary<string, string>(NonQueryTokens),
                LoggableTokens = new Dictionary<string, string>(LoggableTokens),
                // Slice 3 schema-template fields: clone all five so per-iteration clones honor
                // the original's fan-out config (audit issue I9). Without these copies, an
                // iteration clone would observe defaults instead of the user's settings.
                SchemaIdentificationScript = SchemaIdentificationScript,
                CreateSchemaIfMissing = CreateSchemaIfMissing,
                AllowParallel = AllowParallel,
                ContinueOnSchemaFailure = ContinueOnSchemaFailure,
                ContinueOnDatabaseFailure = ContinueOnDatabaseFailure
            };
            foreach (var token in QueryTokens)
                clone.QueryTokens.Add(token.Key, token.Value);
            clone.ScriptFolders.AddRange(ScriptFolders.Select(s => s.Clone()));
            clone.Tables.AddRange(Tables);
            clone.MaterializedViews.AddRange(MaterializedViews);
            clone.Events.AddRange(Events);
            clone.DomainTypes.AddRange(DomainTypes);

            clone.EnumTypes.AddRange(EnumTypes);
            clone.DomainTypeSchema = DomainTypeSchema;
            clone.EnumTypeSchema = EnumTypeSchema;
            clone.Sequences.AddRange(Sequences);
            clone.SequenceSchema = SequenceSchema;
            clone.MaterializedViewSchema = MaterializedViewSchema;
            clone.EventSchema = EventSchema;
            clone.IndexedViews.AddRange(IndexedViews);
            clone.IndexedViewSchema = IndexedViewSchema;
            // Rebuild the token-scope map from the cloned tokens. Cheaper than copying the
            // private Dictionary (which the audit explicitly recommended against) and
            // ensures the cloned scope map references the cloned token dictionaries, not
            // the originals.
            if (_tokenScopes != null)
                clone.ResolveTokenScopes();
            // _perDbQueryTokenCache is intentionally NOT copied — the clone gets its own fresh
            // ConcurrentDictionary via that field's initializer when the new Template is constructed
            // above. A clone represents a fresh resolution state and its token bodies may differ
            // from the original's, so cached resolved values from the original cannot be assumed
            // valid against the clone's bodies.
            return clone;
        }

        // Single source of truth for the "Templates/<name>/Template.json" on-disk convention —
        // Load() uses it to locate the file, and PackageLoader uses it independently (via
        // TryLoadTemplate) to name a template whose Load() call threw before ever returning a
        // Template instance, so there's no loaded object to read FilePath off of.
        internal static string GetTemplateFilePath(Product product, string templateName)
        {
            var schemaPackagePath = Path.GetDirectoryName(product.FilePath) ?? "";
            return Path.Join(schemaPackagePath, "Templates", templateName, "Template.json");
        }

        /// <summary>
        /// Loads a Template and its Tables from disk using platform-aware deserialization.
        /// Resolves file tokens, merges product + template tokens, loads scripts, and applies token replacement.
        /// </summary>
        /// <param name="tolerateComponentLoadErrors">
        /// Deploy path leaves this false: a Table/Materialized-View/Indexed-View that fails to
        /// deserialize (e.g. a misnamed property) throws immediately and aborts the whole load —
        /// deploying against a package the tool couldn't fully parse is the risk that behavior
        /// closes. `--Validate` (PackageLoader) passes true: its contract is to report every
        /// problem it can find in one pass, so a single bad component file is excluded from the
        /// loaded template instead of taking down every other finding the run would otherwise
        /// produce. JsonSchemaCheck re-validates every package file straight off disk regardless
        /// of what loaded here, so the excluded file's precise SS-JSON-001 finding still surfaces.
        /// </param>
        /// <param name="tolerateFileTokenErrors">
        /// Deploy path leaves this false: an unresolvable <c>ScriptTokens</c> file reference
        /// throws immediately and aborts the template load, same as always. `--Validate`
        /// (PackageLoader) passes true so the failure lands in <see cref="FileTokenErrors"/> as a
        /// reportable finding instead of aborting the load.
        /// </param>
        /// <param name="missingMemberHandling">
        /// Deploy path leaves this at the default (Error) so an unrecognised Template.json property
        /// still stops the run. `--Validate` (PackageLoader) passes Ignore instead — the same
        /// leniency <see cref="Product.Load"/> already has for Product.json — so a parseable-but-
        /// wrong Template.json loads fully (full check coverage for that template) instead of
        /// excluding the whole template over one bad property; JsonSchemaCheck independently
        /// re-validates the raw file and reports the precise SS-JSON-001 regardless of which way
        /// this loaded.
        /// </param>
        public static Template Load(string templateName, Product product, bool tolerateComponentLoadErrors = false, bool tolerateFileTokenErrors = false, MissingMemberHandling missingMemberHandling = MissingMemberHandling.Error)
        {
            var templateFilePath = GetTemplateFilePath(product, templateName);
            var templatePath = Path.GetDirectoryName(templateFilePath) ?? "";

            var template = JsonHelper.TemplateLoad(templateFilePath, product.Platform, missingMemberHandling);
            template.FilePath = templateFilePath;
            template.Product = product;

            template.MigrateMySqlSchemaIdentificationScriptAlias();

            // Validate schema-template structural rules (folders, filenames, presence) BEFORE
            // expensive load work — fail fast with a clear error rather than after Table.Load
            // has parsed half a dozen JSON files.
            template.ValidateSchemaTemplateRules(templatePath);

            foreach (var token in template.ScriptTokens)
                template.LoggableTokens.Add(token.Key, token.Value);

            var tokenErrors = TokenHelper.ResolveFileTokens(template.ScriptTokens, templatePath, product.Platform, tolerateFileTokenErrors);
            foreach (var tokenError in tokenErrors)
                template.FileTokenErrors.Add(new FileTokenError(template.FilePath, tokenError));

            // Merge template and product script tokens — template takes precedence
            var scriptTokens = template.ScriptTokens
                .Concat(product.ScriptTokens.Concat(product.QueryTokens)
                    .Where(st => !template.ScriptTokens.ContainsKey(st.Key)))
                .ToDictionary(k => k.Key, v => v.Value);

            template.InstanceLoad(scriptTokens, product.Platform, tolerateComponentLoadErrors);

            // Resolve the per-token TokenScope map so the SchemaQuench dispatcher can decide which
            // <*Query*> tokens need re-running per schema iteration vs. once per DB. Idempotent on
            // regular templates (no {{SchemaName}} references → every token stays at its default
            // scope), so the call is harmless when there's no schema-template fan-out in play.
            template.ResolveTokenScopes();

            // Warn (but don't fail) when schema-only fields are set non-default on regular templates.
            // These have no effect outside the schema-template fan-out path; surface the likely-mistake
            // through the progress log instead of failing the load (the user may be experimenting).
            template.WarnIfSchemaOnlyFieldsSetOnRegularTemplate();

            return template;
        }

        /// <summary>
        /// Backward compatibility: on MySQL, <see cref="SchemaIdentificationScript"/> was historically
        /// an alias for <see cref="DatabaseIdentificationScript"/> (MySQL conflates the two concepts).
        /// MySQL packages still in the wild may use the legacy field name. When a MySQL template
        /// arrives with the legacy alias populated, migrate the value into the canonical field
        /// (when that is null), warn the user to rename, and clear the alias so downstream code
        /// sees a regular MySQL template (no schema-fan-out, which is intentionally SQL-Server /
        /// PostgreSQL only — see design doc §2).
        /// </summary>
        private void MigrateMySqlSchemaIdentificationScriptAlias()
        {
            if (Product?.Platform.GetBasePlatform() != Platform.MySQL) return;
            if (string.IsNullOrWhiteSpace(SchemaIdentificationScript)) return;

            DatabaseIdentificationScript ??= SchemaIdentificationScript;
            DeprecationNotices.Add(new DeprecationNotice("SS-DEP-001", FilePath,
                $"Template '{Name}' uses 'SchemaIdentificationScript', a deprecated alias on MySQL and MariaDB. " +
                "Rename it to 'DatabaseIdentificationScript' -- the value is identical, and the alias only still works because load migrates it."));
            LogFactory.GetLogger("ProgressLog").Warn(
                $"Template '{Name}' (MySQL) uses the legacy 'SchemaIdentificationScript' alias. " +
                $"Rename the field to 'DatabaseIdentificationScript' in {FilePath}. " +
                $"The alias is preserved for backward compatibility and migrates the value silently.");
            SchemaIdentificationScript = null;
        }

        private void InstanceLoad(Dictionary<string, string> scriptTokens, Platform platform, bool tolerateComponentLoadErrors)
        {
            LoadTables(platform, tolerateComponentLoadErrors);
            LoadMaterializedViews(platform, tolerateComponentLoadErrors);
            LoadEvents(platform, tolerateComponentLoadErrors);
            LoadDomainTypes(platform, tolerateComponentLoadErrors);

            LoadEnumTypes(platform, tolerateComponentLoadErrors);
            LoadSequences(platform, tolerateComponentLoadErrors);
            LoadIndexedViews(platform, tolerateComponentLoadErrors);

            // Run schema-default resolution BEFORE any token serialization touches the in-memory
            // Tables / MaterializedViews / IndexedViews. The resolver fills unset Schema fields
            // with either the platform default ("dbo" / "public") for regular templates or the
            // "{{SchemaName}}" token for schema templates. Every downstream serialization in this
            // method — the <*SpecificTable*> / <*SpecificMaterializedView*> / <*SpecificIndexedView*>
            // resolutions below, the TableSchema / MaterializedViewSchema / IndexedViewSchema
            // JSON snapshots, and the script-body substitutions of {{TableSchema}} etc. — depends
            // on the Schema field being populated. Running the resolver up front means the FIRST
            // serialization is post-resolver, so the user-facing script-token surface sees the
            // resolved values rather than the pre-resolver nulls (which NullValueHandling.Ignore
            // would silently omit from the JSON, leaving user scripts with no Schema field).
            SchemaDefaultResolver.Resolve(this);

            QueryTokens = TokenHelper.SplitOutQueryTokens(scriptTokens);
            TokenHelper.ResolveSpecificTableTokens(scriptTokens, Tables, platform);
            TokenHelper.ResolveSpecificMaterializedViewTokens(scriptTokens, MaterializedViews);
            TokenHelper.ResolveSpecificIndexedViewTokens(scriptTokens, IndexedViews);
            NonQueryTokens = scriptTokens;
            var tokens = scriptTokens.ToList();
            tokens.Add(new("TemplateName", Name ?? "UNSPECIFIED"));

            var tableQueue = new TaskQueueManager<Table>(Environment.ProcessorCount * 2);
            Tables.ForEach(table => tableQueue.AddToQueue(table, t => t.ResolveScriptTokensInTableComponentScripts(tokens)));
            tableQueue.WaitForAll();

            MaterializedViews.ForEach(mv =>
            {
                var viewTokens = tokens.Concat(Table.GetCustomTokens(mv.Extensions, "MaterializedView.")).ToList();
                mv.ShouldApplyExpression = Table.TableTokenReplace(mv.ShouldApplyExpression, viewTokens);
                foreach (var index in mv.Indexes)
                {
                    var indexTokens = viewTokens.Concat(Table.GetCustomTokens(index.Extensions)).ToList();
                    index.ShouldApplyExpression = Table.TableTokenReplace(index.ShouldApplyExpression, indexTokens);
                    index.FilterExpression = Table.TableTokenReplace(index.FilterExpression, indexTokens);
                }
            });

            IndexedViews.ForEach(iv =>
            {
                var viewTokens = tokens.Concat(Table.GetCustomTokens(iv.Extensions, "IndexedView.")).ToList();
                iv.ShouldApplyExpression = Table.TableTokenReplace(iv.ShouldApplyExpression, viewTokens);
                foreach (var index in iv.Indexes)
                {
                    var indexTokens = viewTokens.Concat(Table.GetCustomTokens(index.Extensions)).ToList();
                    index.ShouldApplyExpression = Table.TableTokenReplace(index.ShouldApplyExpression, indexTokens);
                }
            });

            TableSchema = JArray.FromObject(Tables).ToString();
            tokens.Add(new("TableSchema", TableSchema.Replace("'", "''")));
            // A2: XML twin, pre-escaped identically to the JSON sibling (added via the same non-escaping
            // substitution path here, so it must pre-escape single quotes for safe SQL string embedding).
            tokens.Add(new("TableXml", TableXml.Replace("'", "''")));

            MaterializedViewSchema = JArray.FromObject(MaterializedViews).ToString();
            tokens.Add(new("MaterializedViewSchema", MaterializedViewSchema.Replace("'", "''")));
            tokens.Add(new("MaterializedViewXml", MaterializedViewXml.Replace("'", "''")));

            EventSchema = JArray.FromObject(Events).ToString();
            tokens.Add(new("EventSchema", EventSchema.Replace("'", "''")));

            DomainTypeSchema = JArray.FromObject(DomainTypes).ToString();
            tokens.Add(new("DomainTypeSchema", DomainTypeSchema.Replace("'", "''")));

            EnumTypeSchema = JArray.FromObject(EnumTypes).ToString();
            tokens.Add(new("EnumTypeSchema", EnumTypeSchema.Replace("'", "''")));

            SequenceSchema = JArray.FromObject(Sequences).ToString();
            tokens.Add(new("SequenceSchema", SequenceSchema.Replace("'", "''")));

            IndexedViewSchema = JArray.FromObject(IndexedViews).ToString();
            tokens.Add(new("IndexedViewSchema", IndexedViewSchema.Replace("'", "''")));
            tokens.Add(new("IndexedViewXml", IndexedViewXml.Replace("'", "''")));

            if (ScriptFolders.Count == 0) ScriptFolders.AddRange(GetDefaultTemplateFolders(platform, IsSchemaTemplate));

            var templateDir = Path.GetDirectoryName(FilePath) ?? "";
            if (platform == Platform.SqlServer)
                ApplyLegacyFolderFallbacks(templateDir);

            var folderQueue = new TaskQueueManager<ScriptFolder>(Environment.ProcessorCount * 2);
            ScriptFolders.ForEach(folder => folderQueue.AddToQueue(folder, f => f.LoadSqlFiles(templateDir, tokens, platform)));
            folderQueue.WaitForAll();

            DatabaseIdentificationScript = SqlScript.TokenReplace(DatabaseIdentificationScript ?? "", tokens);
            VersionStampScript = SqlScript.TokenReplace(VersionStampScript ?? "", tokens);
            BaselineValidationScript = SqlScript.TokenReplace(BaselineValidationScript ?? "", tokens);
        }

        /// <summary>
        /// Single-arg overload preserved for callers (SchemaTongs, FolderMappingConfig, existing tests)
        /// that don't need the schema-template carve-out. Returns the regular-template default set.
        /// </summary>
        public static List<TemplateFolder> GetDefaultTemplateFolders(Platform platform) =>
            GetDefaultTemplateFolders(platform, isSchemaTemplate: false);

        /// <summary>
        /// Returns the per-platform default <see cref="TemplateFolder"/> set used when a Template.json
        /// has no explicit <c>ScriptFolders</c>. When <paramref name="isSchemaTemplate"/> is true, omits
        /// database-scoped object types that cannot legitimately fan out per schema (design §3.3):
        /// <list type="bullet">
        /// <item><see cref="ScriptObjectType.Schemas"/> — schema objects ARE the iteration unit; defining
        /// them as content would be self-referential.</item>
        /// <item><see cref="ScriptObjectType.DDLTriggers"/>, <see cref="ScriptObjectType.FullTextCatalogs"/>,
        /// <see cref="ScriptObjectType.FullTextStopLists"/> — database-scoped on SQL Server; fanning out
        /// per tenant would either fail with duplicate-name errors or create N copies that all fire on
        /// every DDL change.</item>
        /// <item><see cref="ScriptObjectType.Publications"/> — <c>CREATE PUBLICATION</c> has no schema
        /// qualifier on PostgreSQL, so it is database-scoped for the same reason as the SQL Server trio
        /// above.</item>
        /// </list>
        /// MySQL has no schema-template path; the parameter is ignored on MySQL.
        /// </summary>
        public static List<TemplateFolder> GetDefaultTemplateFolders(Platform platform, bool isSchemaTemplate)
        {
            var folders = platform.GetBasePlatform() switch
            {
                Platform.SqlServer =>
                [
                    new TemplateFolder { FolderPath = "Before Scripts", QuenchSlot = TemplateQuenchSlot.Before },
                    new TemplateFolder { FolderPath = "Schemas", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Schemas },
                    new TemplateFolder { FolderPath = "DataTypes", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.DataTypes },
                    new TemplateFolder { FolderPath = "FullTextCatalogs", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.FullTextCatalogs },
                    new TemplateFolder { FolderPath = "FullTextStopLists", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.FullTextStopLists },
                    new TemplateFolder { FolderPath = "XMLSchemaCollections", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.XMLSchemaCollections },
                    new TemplateFolder { FolderPath = "Functions", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Functions },
                    new TemplateFolder { FolderPath = "Views", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Views },
                    new TemplateFolder { FolderPath = "Procedures", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Procedures },
                    // Sequences (2012) and Synonyms (2005) are SCRIPTED-ONLY like every other folder here —
                    // no drop-by-absence for scripted types (see DatabaseQuench), so wiring them in is additive.
                    // Sequences post-dates the 2008 floor; gate a target below 2012 via the folder's own
                    // ShouldApplyExpression (the existing per-folder gate mechanism) rather than a new check here.
                    new TemplateFolder { FolderPath = "Sequences", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Sequences },
                    new TemplateFolder { FolderPath = "Synonyms", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Synonyms },
                    new TemplateFolder { FolderPath = "Triggers", QuenchSlot = TemplateQuenchSlot.AfterTablesObjects, ObjectType = ScriptObjectType.Triggers },
                    new TemplateFolder { FolderPath = "DDLTriggers", QuenchSlot = TemplateQuenchSlot.AfterTablesObjects, ObjectType = ScriptObjectType.DDLTriggers },
                    // #323. SCHEMABINDING modules live here rather than in Views/Functions above,
                    // because a column change they block is resolved by dropping them and letting
                    // this pass recreate them -- which only works after the table work has run.
                    new TemplateFolder { FolderPath = "SchemaBound Views", QuenchSlot = TemplateQuenchSlot.AfterTablesObjects, ObjectType = ScriptObjectType.SchemaBoundViews },
                    new TemplateFolder { FolderPath = "SchemaBound Functions", QuenchSlot = TemplateQuenchSlot.AfterTablesObjects, ObjectType = ScriptObjectType.SchemaBoundFunctions },
                    new TemplateFolder { FolderPath = "Table Data", QuenchSlot = TemplateQuenchSlot.TableData },
                    new TemplateFolder { FolderPath = "After Scripts", QuenchSlot = TemplateQuenchSlot.After },
                ],
                Platform.PostgreSQL =>
                [
                    new TemplateFolder { FolderPath = "Before Scripts", QuenchSlot = TemplateQuenchSlot.Before },
                    new TemplateFolder { FolderPath = "Schemas", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Schemas },
                    new TemplateFolder { FolderPath = "Domain Types", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.DomainTypes },
                    new TemplateFolder { FolderPath = "Enum Types", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.EnumTypes },
                    new TemplateFolder { FolderPath = "Composite Types", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.CompositeTypes },
                    // Collations are schema-scoped (like Domain/Enum/Composite Types above), so this folder
                    // fans out per schema in schema-template mode the same as those three — no exclusion needed.
                    new TemplateFolder { FolderPath = "Collations", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Collations },
                    new TemplateFolder { FolderPath = "Functions", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Functions },
                    new TemplateFolder { FolderPath = "Trigger Functions", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.TriggerFunctions },
                    new TemplateFolder { FolderPath = "Window Functions", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.WindowFunctions },
                    new TemplateFolder { FolderPath = "Aggregates", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Aggregates },
                    new TemplateFolder { FolderPath = "Procedures", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Procedures },
                    new TemplateFolder { FolderPath = "Sequences", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Sequences },
                    // Publications are database-scoped (CREATE PUBLICATION has no schema qualifier), so —
                    // like Schemas above — this folder is excluded from schema-template fan-out below.
                    new TemplateFolder { FolderPath = "Publications", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Publications },
                    new TemplateFolder { FolderPath = "Rules", QuenchSlot = TemplateQuenchSlot.AfterTablesObjects, ObjectType = ScriptObjectType.Rules },
                    new TemplateFolder { FolderPath = "Triggers", QuenchSlot = TemplateQuenchSlot.AfterTablesObjects, ObjectType = ScriptObjectType.Triggers },
                    new TemplateFolder { FolderPath = "Views", QuenchSlot = TemplateQuenchSlot.AfterTablesObjects, ObjectType = ScriptObjectType.Views },
                    new TemplateFolder { FolderPath = "Table Data", QuenchSlot = TemplateQuenchSlot.TableData },
                    new TemplateFolder { FolderPath = "After Scripts", QuenchSlot = TemplateQuenchSlot.After },
                ],
                Platform.MySQL =>
                [
                    new TemplateFolder { FolderPath = "Before Scripts", QuenchSlot = TemplateQuenchSlot.Before },
                    new TemplateFolder { FolderPath = "Events", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Events },
                    new TemplateFolder { FolderPath = "Functions", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Functions },
                    new TemplateFolder { FolderPath = "Procedures", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Procedures },
                    new TemplateFolder { FolderPath = "Triggers", QuenchSlot = TemplateQuenchSlot.AfterTablesObjects, ObjectType = ScriptObjectType.Triggers },
                    new TemplateFolder { FolderPath = "Views", QuenchSlot = TemplateQuenchSlot.AfterTablesObjects, ObjectType = ScriptObjectType.Views },
                    new TemplateFolder { FolderPath = "Table Data", QuenchSlot = TemplateQuenchSlot.TableData },
                    new TemplateFolder { FolderPath = "After Scripts", QuenchSlot = TemplateQuenchSlot.After },
                ],
                Platform.Unknown => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Platform has not been assigned."),
                _ => new List<TemplateFolder>()
            };

            // MariaDb-only: MySQL has no native SEQUENCE object at all (not a version gap — the base MySQL
            // folder set never carries this type). The switch above is keyed on GetBasePlatform(), which maps
            // MariaDb and MySQL to the same case, so the one MariaDb-only addition is layered on afterward
            // against the raw `platform` parameter instead — no other folder-set divergence exists to mirror.
            if (platform == Platform.MariaDb)
                folders.Insert(folders.FindIndex(f => f.FolderPath == "Triggers"),
                    new TemplateFolder { FolderPath = "Sequences", QuenchSlot = TemplateQuenchSlot.Objects, ObjectType = ScriptObjectType.Sequences });

            if (!isSchemaTemplate) return folders;

            // Database-scoped objects cannot legitimately fan out per schema (design §3.3).
            return folders.Where(f =>
                f.ObjectType != ScriptObjectType.Schemas &&
                f.ObjectType != ScriptObjectType.DDLTriggers &&
                f.ObjectType != ScriptObjectType.FullTextCatalogs &&
                f.ObjectType != ScriptObjectType.FullTextStopLists &&
                f.ObjectType != ScriptObjectType.Publications).ToList();
        }

        /// <summary>
        /// For SQL Server products with no explicit ScriptFolders, checks whether the default
        /// folder names exist on disk. If the new standardized names (Before Scripts, After Scripts,
        /// Table Data) don't exist but the legacy names (MigrationScripts/Before, MigrationScripts/After,
        /// TableData) do, falls back to the legacy paths for backward compatibility.
        /// </summary>
        internal void ApplyLegacyFolderFallbacks(string templateDir)
        {
            var dir = ProductDirectoryWrapper.GetFromFactory();
            foreach (var folder in ScriptFolders)
            {
                if (dir.Exists(Path.Combine(templateDir, folder.FolderPath))) continue;

                var legacyPath = folder.FolderPath switch
                {
                    "Before Scripts" => "MigrationScripts/Before",
                    "After Scripts" => "MigrationScripts/After",
                    "Table Data" => "TableData",
                    _ => null
                };

                if (legacyPath != null && dir.Exists(Path.Combine(templateDir, legacyPath)))
                    folder.FolderPath = legacyPath;
            }
        }

        /// <summary>
        /// Loads DECLARATIVE events from the same Events/ folder that has always held scripted ones.
        /// <para>Only *.json is read here; a *.sql file in that folder is still picked up by the scripted
        /// Objects slot exactly as before, so no existing package changes behaviour. That is deliberate:
        /// promoting events to a managed type must not strip a folder users already populate.</para>
        /// </summary>
        /// <summary>
        /// Loads DECLARATIVE enum types from the same Enum Types/ folder that has always held scripted
        /// ones. Only *.json is read here; a *.sql file there still runs through the Objects slot.
        /// </summary>
        /// <summary>Loads DECLARATIVE sequences from the Sequences/ folder; *.sql there still runs scripted.</summary>
        private void LoadSequences(Platform platform, bool tolerateComponentLoadErrors)
        {
            if (platform != Platform.PostgreSQL) return;
            var path = Path.Join(Path.GetDirectoryName(FilePath) ?? "", "Sequences");
            if (!ProductDirectoryWrapper.GetFromFactory().Exists(path)) return;
            var files = ProductDirectoryWrapper.GetFromFactory()
                .GetFiles(path, "*.json", SearchOption.AllDirectories)
                .OrderBy(x => x);
            foreach (var f in files)
            {
                try
                {
                    var json = ProductFileWrapper.GetFromFactory().ReadAllText(f);
                    Sequences.Add(JsonConvert.DeserializeObject<PostgreSqlSequence>(json, PlatformDeserializer.StrictSettings)
                                  ?? throw new JsonSerializationException("Failed to deserialize sequence"));
                }
                // prepush-allow: generic-catch -- any load failure must surface wrapped with the file that caused it
                catch (Exception e) when (!tolerateComponentLoadErrors)
                {
                    throw new Exception($"Error loading sequence from {f}" + Environment.NewLine + e.Message, e);
                }
                // prepush-allow: generic-catch -- --Validate must record ANY component failure
                catch (Exception e)
                {
                    RecordComponentLoadError(f, e);
                }
            }
        }

        private void LoadDomainTypes(Platform platform, bool tolerateComponentLoadErrors)
        {
            if (platform != Platform.PostgreSQL) return;
            var path = Path.Join(Path.GetDirectoryName(FilePath) ?? "", "Domain Types");
            if (!ProductDirectoryWrapper.GetFromFactory().Exists(path)) return;
            var files = ProductDirectoryWrapper.GetFromFactory()
                .GetFiles(path, "*.json", SearchOption.AllDirectories)
                .OrderBy(x => x);
            foreach (var f in files)
            {
                try
                {
                    var json = ProductFileWrapper.GetFromFactory().ReadAllText(f);
                    DomainTypes.Add(JsonConvert.DeserializeObject<PostgreSqlDomainType>(json, PlatformDeserializer.StrictSettings)
                                    ?? throw new JsonSerializationException("Failed to deserialize domain type"));
                }
                // prepush-allow: generic-catch -- any load failure must surface wrapped with the file that caused it
                catch (Exception e) when (!tolerateComponentLoadErrors)
                {
                    throw new Exception($"Error loading domain type from {f}" + Environment.NewLine + e.Message, e);
                }
                // prepush-allow: generic-catch -- --Validate must record ANY component failure
                catch (Exception e)
                {
                    RecordComponentLoadError(f, e);
                }
            }
        }

        private void LoadEnumTypes(Platform platform, bool tolerateComponentLoadErrors)
        {
            if (platform != Platform.PostgreSQL) return;
            var path = Path.Join(Path.GetDirectoryName(FilePath) ?? "", "Enum Types");
            if (!ProductDirectoryWrapper.GetFromFactory().Exists(path)) return;
            var files = ProductDirectoryWrapper.GetFromFactory()
                .GetFiles(path, "*.json", SearchOption.AllDirectories)
                .OrderBy(x => x);
            foreach (var f in files)
            {
                try
                {
                    var json = ProductFileWrapper.GetFromFactory().ReadAllText(f);
                    EnumTypes.Add(JsonConvert.DeserializeObject<PostgreSqlEnumType>(json, PlatformDeserializer.StrictSettings)
                                  ?? throw new JsonSerializationException("Failed to deserialize enum type"));
                }
                // prepush-allow: generic-catch -- any load failure must surface wrapped with the file that caused it
                catch (Exception e) when (!tolerateComponentLoadErrors)
                {
                    throw new Exception($"Error loading enum type from {f}" + Environment.NewLine + e.Message, e);
                }
                // prepush-allow: generic-catch -- --Validate must record ANY component failure
                catch (Exception e)
                {
                    RecordComponentLoadError(f, e);
                }
            }
        }

        private void LoadEvents(Platform platform, bool tolerateComponentLoadErrors)
        {
            if (platform.GetBasePlatform() != Platform.MySQL) return;
            var eventsPath = Path.Join(Path.GetDirectoryName(FilePath) ?? "", "Events");
            if (!ProductDirectoryWrapper.GetFromFactory().Exists(eventsPath)) return;
            var files = ProductDirectoryWrapper.GetFromFactory()
                .GetFiles(eventsPath, "*.json", SearchOption.AllDirectories)
                .OrderBy(x => x);
            foreach (var f in files)
            {
                try
                {
                    var json = ProductFileWrapper.GetFromFactory().ReadAllText(f);
                    Events.Add(JsonConvert.DeserializeObject<MySqlEvent>(json, PlatformDeserializer.StrictSettings)
                               ?? throw new JsonSerializationException("Failed to deserialize event"));
                }
                // prepush-allow: generic-catch -- any load failure must surface wrapped with the file that caused it
                catch (Exception e) when (!tolerateComponentLoadErrors)
                {
                    throw new Exception($"Error loading event from {f}" + Environment.NewLine + e.Message, e);
                }
                // prepush-allow: generic-catch -- --Validate must record ANY component failure
                catch (Exception e)
                {
                    RecordComponentLoadError(f, e);
                }
            }
        }

        private void LoadMaterializedViews(Platform platform, bool tolerateComponentLoadErrors)
        {
            if (platform != Platform.PostgreSQL) return;
            var matViewsPath = Path.Combine(Path.GetDirectoryName(FilePath) ?? "", "Materialized Views");
            if (!ProductDirectoryWrapper.GetFromFactory().Exists(matViewsPath)) return;
            var files = ProductDirectoryWrapper.GetFromFactory()
                .GetFiles(matViewsPath, "*.json", SearchOption.AllDirectories)
                .OrderBy(x => x);
            foreach (var f in files)
            {
                try
                {
                    var json = ProductFileWrapper.GetFromFactory().ReadAllText(f);
                    MaterializedViews.Add(PlatformDeserializer.DeserializeMaterializedView(json, platform));
                }
                // prepush-allow: generic-catch -- any load failure must surface wrapped with the file that caused it; narrowing would let one escape unlabelled
                catch (Exception e) when (!tolerateComponentLoadErrors)
                {
                    throw new Exception($"Error loading materialized view from {f}\r\n{e.Message}", e);
                }
                // prepush-allow: generic-catch -- --Validate must record ANY component failure, and DeserializeMaterializedView rewraps into plain Exception
                catch (Exception e)
                {
                    // --Validate: excluded here so the rest of the template still loads. An
                    // Recorded either way (see RecordComponentLoadError). An unparseable file surfaces as
                    // SS-LOAD-001; a parseable-but-wrong one is classified and left for JsonSchemaCheck's
                    // on-disk pass to report precisely as SS-JSON-001.
                    RecordComponentLoadError(f, e);
                }
            }
        }

        private void LoadIndexedViews(Platform platform, bool tolerateComponentLoadErrors)
        {
            if (platform != Platform.SqlServer) return;
            var indexedViewsPath = Path.Combine(Path.GetDirectoryName(FilePath) ?? "", "Indexed Views");
            if (!ProductDirectoryWrapper.GetFromFactory().Exists(indexedViewsPath)) return;
            var files = ProductDirectoryWrapper.GetFromFactory()
                .GetFiles(indexedViewsPath, "*.json", SearchOption.AllDirectories)
                .OrderBy(x => x);
            foreach (var f in files)
            {
                try
                {
                    var json = ProductFileWrapper.GetFromFactory().ReadAllText(f);
                    IndexedViews.Add(PlatformDeserializer.DeserializeIndexedView(json, platform));
                }
                // prepush-allow: generic-catch -- any load failure must surface wrapped with the file that caused it; narrowing would let one escape unlabelled
                catch (Exception e) when (!tolerateComponentLoadErrors)
                {
                    throw new Exception($"Error loading indexed view from {f}\r\n{e.Message}", e);
                }
                // prepush-allow: generic-catch -- --Validate must record ANY component failure, and DeserializeIndexedView rewraps into plain Exception
                catch (Exception e)
                {
                    // --Validate: excluded here so the rest of the template still loads. An
                    // Recorded either way (see RecordComponentLoadError). An unparseable file surfaces as
                    // SS-LOAD-001; a parseable-but-wrong one is classified and left for JsonSchemaCheck's
                    // on-disk pass to report precisely as SS-JSON-001.
                    RecordComponentLoadError(f, e);
                }
            }
        }

        private void LoadTables(Platform platform, bool tolerateComponentLoadErrors)
        {
            var tablesPath = Path.Combine(Path.GetDirectoryName(FilePath) ?? "", "Tables");
            if (!ProductDirectoryWrapper.GetFromFactory().Exists(tablesPath)) return;
            var files = ProductDirectoryWrapper.GetFromFactory()
                .GetFiles(tablesPath, "*.json", SearchOption.AllDirectories)
                .OrderBy(x => x);
            foreach (var f in files)
            {
                try
                {
                    Tables.Add(Table.Load(f, platform));
                }
                // prepush-allow: generic-catch -- --Validate must record ANY component failure, and Table.Load rewraps its own into plain Exception (Table.cs:99)
                catch (Exception e) when (tolerateComponentLoadErrors)
                {
                    // --Validate: excluded here so the rest of the template still loads (and
                    // Duplication/Coherence still run against the tables that DID parse). An
                    // Recorded either way (see RecordComponentLoadError). An unparseable file surfaces as
                    // SS-LOAD-001; a parseable-but-wrong one is classified and left for JsonSchemaCheck's
                    // on-disk pass to report precisely as SS-JSON-001.
                    RecordComponentLoadError(f, e);
                }
            }
        }

        /// <summary>
        /// Records <paramref name="filePath"/> in <see cref="ComponentLoadErrors"/> when the file
        /// itself is not valid JSON — the file isn't parseable at all, so there is no object for
        /// JsonSchemaCheck to schema-check and nothing else would ever report it. A parseable-but-
        /// wrong file (an unrecognised/misnamed property rejected by MissingMemberHandling.Error) is
        /// left unrecorded here on purpose: JsonSchemaCheck re-validates the raw file straight off
        /// disk regardless of what loaded here, so that case already gets its own precise SS-JSON-001
        /// — recording it here too would report the same file under two different codes.
        /// </summary>
        // EVERY component that failed to load is recorded. Gating the record on unparseability made the
        // tolerant path strictly worse than the strict one: a file that is valid JSON but carries an
        // unrecognised property -- the exact class MissingMemberHandling.Error rejects, and the most common
        // authoring mistake -- was caught, not loaded, and not reported, so the table simply vanished from
        // the template with nothing said. ComponentLoadErrors cannot mean "the components that did not load"
        // with a guard like that in place, and that contract is the only thing a consumer can build on.
        // Parseability is kept as CLASSIFICATION (see ComponentLoadError.IsValidJson) so `--Validate` can
        // still report only the unparseable ones as SS-LOAD-001 and leave the rest to JsonSchemaCheck.
        private void RecordComponentLoadError(string filePath, Exception e) =>
            ComponentLoadErrors.Add(new ComponentLoadError(filePath, e.Message, IsParseableJson(filePath)));

        // Deliberately does NOT infer parseability from the CLR exception type that surfaced out of
        // Table.Load / PlatformDeserializer — that inference is unsound. MissingMemberHandling.Error
        // (an unrecognised/misnamed property: parseable, wrong shape) and a truncated/malformed
        // document (not parseable at all) both surface from Newtonsoft as JsonSerializationException
        // ("Unexpected end when deserializing object..." is a JsonSerializationException, not a
        // JsonReaderException, despite being a pure syntax failure) — so exception type cannot tell
        // the two cases apart. Re-parsing the raw text directly answers the actual question ("is this
        // valid JSON at all?") and stays correct regardless of which exception type Newtonsoft raises
        // for a given malformation, or whether a future deserialization setting changes that shape.
        private static bool IsParseableJson(string filePath)
        {
            try
            {
                var text = ProductFileWrapper.GetFromFactory().ReadAllText(filePath);
                JToken.Parse(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Runs the schema-template load-time validation rules (design §3.3) that don't require
        /// the materialized domain objects — folder set, filename prefixes, and
        /// <see cref="CreateSchemaIfMissing"/> presence dependency. Table <c>Schema</c> literal
        /// rejection is handled by <see cref="SchemaDefaultResolver"/> (slice 1) and bubbles up
        /// with file context via that resolver's outer wrap.
        /// </summary>
        private void ValidateSchemaTemplateRules(string templateDir)
        {
            // Rule 5 applies regardless of platform / SchemaIdentificationScript presence:
            // a true value here without a discovery script is always a config error.
            // Note: this is intentionally a hard throw rather than a warn. CreateSchemaIfMissing's
            // default is false, so any "non-default" on a regular template means the user explicitly
            // set it to true — and a true value without a SchemaIdentificationScript can never do
            // anything useful (no schemas to discover, no fan-out to perform). There is no
            // distinct warn path for this field on regular templates — the schema-only-field
            // warn surface (see WarnIfSchemaOnlyFieldsSetOnRegularTemplate) deliberately omits
            // CreateSchemaIfMissing for that reason.
            if (CreateSchemaIfMissing && !IsSchemaTemplate)
                throw new InvalidOperationException(
                    $"Template '{Name}' (file: {FilePath}) sets CreateSchemaIfMissing=true but " +
                    $"has no SchemaIdentificationScript. CreateSchemaIfMissing only applies to " +
                    $"schema templates — add a SchemaIdentificationScript or remove " +
                    $"CreateSchemaIfMissing from the template configuration.");

            if (!IsSchemaTemplate) return;

            // Rule 1 (defensive): MySQL never reaches here as a schema template because the alias
            // migration clears the field. If somehow we get here, fail loud with the design's
            // database-per-tenant hint rather than silently producing broken DDL.
            if (Product?.Platform.GetBasePlatform() == Platform.MySQL)
                throw new InvalidOperationException(
                    $"Template '{Name}' (file: {FilePath}) is a schema template on MySQL. " +
                    $"MySQL has no schema-inside-database concept — use database-per-tenant " +
                    $"instead (one DatabaseIdentificationScript-driven template per tenant DB).");

            // Rule 2: database-scoped ObjectType rejection.
            var offending = ScriptFolders.FirstOrDefault(f => DisallowedSchemaTemplateObjectTypes.Contains(f.ObjectType));
            if (offending != null)
                throw new InvalidOperationException(
                    $"Template '{Name}' (file: {FilePath}) is a schema template but declares a " +
                    $"ScriptFolder for '{offending.ObjectType}' (path: '{offending.FolderPath}'). " +
                    $"{offending.ObjectType} is database-scoped and cannot fan out per schema. " +
                    $"Move {offending.ObjectType} content into a non-schema-template that runs " +
                    $"before this one in TemplateOrder.");

            // Rule 3: filename prefix check for tables, materialized views, indexed views.
            ValidateSchemaTemplateFilenames(templateDir);
        }

        /// <summary>
        /// Schema templates require unqualified filenames (e.g. <c>Customers.json</c>, not
        /// <c>dbo.Customers.json</c>) for tables, materialized views, and indexed views — the
        /// schema is supplied per-iteration via <c>{{SchemaName}}</c>, so a literal prefix is
        /// semantically wrong. Strips a single trailing <c>.json</c> extension before checking
        /// for further dots in the bare name.
        /// </summary>
        private void ValidateSchemaTemplateFilenames(string templateDir)
        {
            var dir = ProductDirectoryWrapper.GetFromFactory();

            CheckFolder(Path.Combine(templateDir, "Tables"), "table");
            CheckFolder(Path.Combine(templateDir, "Materialized Views"), "materialized view");
            CheckFolder(Path.Combine(templateDir, "Indexed Views"), "indexed view");

            return;

            void CheckFolder(string folder, string ownerKind)
            {
                if (!dir.Exists(folder)) return;
                foreach (var file in dir.GetFiles(folder, "*.json", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(file);
                    var bareName = Path.GetFileNameWithoutExtension(fileName);
                    if (!bareName.Contains('.')) continue;

                    throw new InvalidOperationException(
                        $"Template '{Name}' (file: {FilePath}) is a schema template; the {ownerKind} " +
                        $"file '{fileName}' has a schema-qualified name. Schema templates require " +
                        $"unqualified filenames — rename to '{bareName.Substring(bareName.LastIndexOf('.') + 1)}.json' " +
                        $"(the schema is supplied per iteration via {{{{SchemaName}}}}).");
                }
            }
        }

        /// <summary>
        /// Per design §3.3, schema-only fields set non-default on a regular template are almost
        /// certainly a configuration mistake. Don't fail the load — surface a clear warning
        /// through the progress log so the user can correct it. <see cref="ContinueOnDatabaseFailure"/>
        /// is excluded: it applies universally (DB-level failure isolation parity).
        /// </summary>
        private void WarnIfSchemaOnlyFieldsSetOnRegularTemplate()
        {
            if (IsSchemaTemplate) return;

            var log = LogFactory.GetLogger("ProgressLog");

            if (!AllowParallel)
                log.Warn(
                    $"Template '{Name}' (file: {FilePath}) sets AllowParallel=false but is not a " +
                    $"schema template. AllowParallel only affects schema-template fan-out; the " +
                    $"setting is ignored for regular templates.");

            if (!ContinueOnSchemaFailure)
                log.Warn(
                    $"Template '{Name}' (file: {FilePath}) sets ContinueOnSchemaFailure=false but " +
                    $"is not a schema template. ContinueOnSchemaFailure only affects schema-template " +
                    $"fan-out; for DB-level failure-isolation use ContinueOnDatabaseFailure.");
        }

        /// <summary>
        /// Resolves the per-token <see cref="TokenScope"/> for every token defined on this Template
        /// via a depth-first dependency walk (design §5.6). A token's body is scanned for
        /// <c>{{SchemaName}}</c> (direct iteration scope) and for <c>{{OtherToken}}</c> references
        /// (transitive promotion when the referenced token is iteration-scoped). Cycles are
        /// detected via the recursion stack and surface as <see cref="InvalidOperationException"/>.
        /// Idempotent; safe to call multiple times.
        /// </summary>
        public void ResolveTokenScopes()
        {
            _tokenScopes = new Dictionary<string, TokenScope>(StringComparer.OrdinalIgnoreCase);

            // Walk QueryTokens and NonQueryTokens together — both can splice {{SchemaName}}.
            // Static (non-query) tokens that include {{SchemaName}} in their body are still
            // iteration-scoped: their substituted value differs per iteration.
            // The NonQueryTokens.Where(...!QueryTokens.ContainsKey...) filter is dead defense on the
            // production Load path (TokenHelper.SplitOutQueryTokens leaves the two dictionaries
            // disjoint by construction), but it matters for tests that hand-construct a Template
            // and populate both dictionaries with overlapping keys — without the filter, the
            // ToDictionary call would throw on a duplicate key before validation could surface it.
            var allBodies = QueryTokens
                .Concat(NonQueryTokens.Where(nq => !QueryTokens.ContainsKey(nq.Key)))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            var defaultScope = new Dictionary<string, TokenScope>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in allBodies)
                defaultScope[kv.Key] = QueryTokens.ContainsKey(kv.Key) ? TokenScope.PerDb : TokenScope.PerProduct;

            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Ordered recursion stack — mirrors `visiting` but preserves insertion order so the
            // cycle-detection error message names ONLY the cycle nodes (sliced from where `name`
            // first appears), not arbitrary prefix nodes that led to the cycle entry point.
            var visitingStack = new List<string>();
            foreach (var name in allBodies.Keys.ToList())
                Walk(name);

            return;

            TokenScope Walk(string name)
            {
                if (_tokenScopes.TryGetValue(name, out var resolved))
                    return resolved;
                if (!allBodies.TryGetValue(name, out var body))
                {
                    // Unknown reference (token doesn't exist on this template — could be a product-level
                    // token or a typo). Don't escalate the caller on the strength of an unknown.
                    return TokenScope.PerProduct;
                }
                if (!visiting.Add(name))
                {
                    // Slice the stack from the first occurrence of `name` so the path message names
                    // only the cycle itself (e.g. A → B → A), not prefix nodes that led into it.
                    var startIndex = visitingStack.FindIndex(
                        x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                    var cyclePath = startIndex >= 0
                        ? visitingStack.GetRange(startIndex, visitingStack.Count - startIndex)
                        : visitingStack;
                    throw new InvalidOperationException(
                        $"Cycle detected in template '{Name}' token graph involving '{name}'. " +
                        $"The token graph contains a cycle: {string.Join(" → ", cyclePath)} → {name}. " +
                        $"Remove the circular reference between these tokens.");
                }
                visitingStack.Add(name);

                try
                {
                    var scope = defaultScope[name];
                    if (body != null && body.Contains(SchemaDefaultResolver.SchemaNameToken))
                        scope = TokenScope.Iteration;

                    if (scope != TokenScope.Iteration && body != null)
                    {
                        foreach (var referenced in TokenHelper.GetTokensFromString(body))
                        {
                            if (referenced.Equals("SchemaName", StringComparison.OrdinalIgnoreCase))
                            {
                                scope = TokenScope.Iteration;
                                break;
                            }
                            // Only walk references that resolve to tokens we own — unknown names
                            // (product-level, built-in) cannot be iteration-scoped from our perspective.
                            if (!allBodies.ContainsKey(referenced)) continue;
                            if (Walk(referenced) == TokenScope.Iteration)
                            {
                                scope = TokenScope.Iteration;
                                break;
                            }
                        }
                    }

                    _tokenScopes[name] = scope;
                    return scope;
                }
                finally
                {
                    visiting.Remove(name);
                    if (visitingStack.Count > 0 && string.Equals(
                            visitingStack[^1], name, StringComparison.OrdinalIgnoreCase))
                        visitingStack.RemoveAt(visitingStack.Count - 1);
                }
            }
        }

        /// <summary>
        /// Returns true when the named token was promoted to <see cref="TokenScope.Iteration"/>
        /// by the most recent <see cref="ResolveTokenScopes"/> call. Returns false when the walk
        /// has not yet run or the token is unknown. The dispatcher uses this to decide whether
        /// to re-materialize a <c>&lt;*Query*&gt;</c> token's value per schema iteration.
        /// </summary>
        public bool IsIterationScoped(string tokenName) =>
            _tokenScopes != null
            && tokenName != null
            && _tokenScopes.TryGetValue(tokenName, out var s)
            && s == TokenScope.Iteration;

        /// <summary>
        /// Returns true when the named token resolved to <see cref="TokenScope.PerDb"/> in the
        /// most recent <see cref="ResolveTokenScopes"/> call — a <c>&lt;*Query*&gt;</c> token
        /// whose body does NOT (directly or transitively) reference <c>{{SchemaName}}</c>.
        /// Returns false when the walk has not yet run or the token is unknown. The dispatcher
        /// uses this to decide whether a query token's resolved value can be cached across
        /// schema iterations within the same target database.
        /// </summary>
        public bool IsPerDb(string tokenName) =>
            _tokenScopes != null
            && tokenName != null
            && _tokenScopes.TryGetValue(tokenName, out var s)
            && s == TokenScope.PerDb;

        // Per-(server, database) cache of resolved per-DB query token values. Populated on the
        // first schema-template iteration that resolves a given per-DB token against a given
        // target database and consulted on every subsequent iteration in the same (server, DB)
        // pair so the connection round-trip happens once instead of once per iteration. Concurrent
        // because the WorkUnitDispatcher may run schema-template iterations in parallel
        // (AllowParallel = true) and multiple iterations against the same (server, DB) all read
        // and write this cache concurrently — a non-concurrent Dictionary would corrupt under
        // that load. Keyed top-level by "serverdatabase" (case-insensitive;  cannot
        // appear in a valid SQL Server / PostgreSQL / MySQL server or database name) and
        // inner-level by token name (also case-insensitive — matches `_tokenScopes` and the
        // token resolver). Lifetime: tied to this Template instance. NOT carried into clones —
        // each clone gets a fresh instance via this field initializer, because a clone represents
        // a fresh resolution state and the parent's resolved values may not match the clone's
        // (possibly modified) token bodies.
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _perDbQueryTokenCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns (or lazily creates) the per-DB query token cache scoped to the supplied
        /// (server, databaseName) pair. The dispatcher calls this from
        /// <c>ResolveAndApplyQueryTokens</c> on schema-template iterations to (a) consult cached
        /// per-DB token values before the connection round-trip and (b) deposit freshly-resolved
        /// values for future iterations to reuse. Per-DB tokens are safe to cache across
        /// iterations because their resolution by definition does not depend on the iteration
        /// schema (no <c>{{SchemaName}}</c> in their body or any token they transitively reach).
        /// Returns <c>null</c> when <paramref name="server"/> or <paramref name="databaseName"/>
        /// is null or empty — the caller falls back to no-cache behavior in that case rather
        /// than synthesizing a global cache that would defeat per-DB isolation.
        /// Thread-safe — the returned inner dictionary is a <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// safe for concurrent read/write from parallel work-unit iterations against the same
        /// (server, database) tuple.
        /// </summary>
        public ConcurrentDictionary<string, string> GetOrCreatePerDbTokenCache(string server, string databaseName)
        {
            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(databaseName)) return null;
            //  (Start of Heading) cannot appear in a valid SQL Server / PostgreSQL / MySQL
            // identifier, so it's safe as a key separator that won't collide with names containing
            // typical delimiter characters like '|', '.', or ':'.
            var key = $"{server}{databaseName}";
            return _perDbQueryTokenCache.GetOrAdd(key,
                _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }
}
