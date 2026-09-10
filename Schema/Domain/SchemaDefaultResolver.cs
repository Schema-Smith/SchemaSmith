// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Linq;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;

namespace Schema.Domain
{
    /// <summary>
    /// Resolves per-platform schema defaults after JSON deserialization, before DDL generation.
    /// Regular templates: unset Schema / RelatedTableSchema resolve to the platform default
    /// ("dbo" on SQL Server, "public" on PostgreSQL — matching pre-refactor behavior bit-for-bit).
    /// Schema templates: unset values resolve to the "{{SchemaName}}" token; literal Schema
    /// values on tables / indexed views / materialized views / enum types / domain types /
    /// sequences are rejected (the object lives in {{SchemaName}} by construction). Literal
    /// RelatedTableSchema values on FKs are preserved as cross-schema references.
    /// </summary>
    public static class SchemaDefaultResolver
    {
        public const string SchemaNameToken = "{{SchemaName}}";

        public static void Resolve(SqlServerTable table, bool isSchemaTemplate, Platform platform)
        {
            if (table == null) return;
            table.Schema = ResolveTableSchema(table.Schema, table.Name, "table", isSchemaTemplate, platform);
            foreach (var fk in table.ForeignKeys.OfType<SqlServerForeignKey>())
                Resolve(fk, isSchemaTemplate, platform);
        }

        public static void Resolve(PostgreSqlTable table, bool isSchemaTemplate, Platform platform)
        {
            if (table == null) return;
            table.Schema = ResolveTableSchema(table.Schema, table.Name, "table", isSchemaTemplate, platform);
            foreach (var fk in table.ForeignKeys.OfType<PostgreSqlForeignKey>())
                Resolve(fk, isSchemaTemplate, platform);
        }

        public static void Resolve(SqlServerForeignKey fk, bool isSchemaTemplate, Platform platform)
        {
            if (fk == null) return;
            fk.RelatedTableSchema = ResolveRelatedTableSchema(fk.RelatedTableSchema, isSchemaTemplate, platform);
        }

        public static void Resolve(PostgreSqlForeignKey fk, bool isSchemaTemplate, Platform platform)
        {
            if (fk == null) return;
            fk.RelatedTableSchema = ResolveRelatedTableSchema(fk.RelatedTableSchema, isSchemaTemplate, platform);
        }

        public static void Resolve(SqlServerIndexedView view, bool isSchemaTemplate, Platform platform)
        {
            if (view == null) return;
            view.Schema = ResolveTableSchema(view.Schema, view.Name, "indexed view", isSchemaTemplate, platform);
        }

        public static void Resolve(PostgreSqlMaterializedView view, bool isSchemaTemplate, Platform platform)
        {
            if (view == null) return;
            view.Schema = ResolveTableSchema(view.Schema, view.Name, "materialized view", isSchemaTemplate, platform);
        }

        // The three declarative PostgreSQL types (2.6.0) resolve exactly like the materialized view above,
        // and for the same reason: each is a first-class schema-qualified object a tenant owns. They were
        // missing here while their own doc comments already claimed the behaviour, so under a schema
        // template an object authored without a Schema was created in public on EVERY tenant -- the quench
        // procedures each COALESCE a null Schema to 'public', so the omission surfaced as data in the wrong
        // place rather than as an error.
        public static void Resolve(PostgreSqlEnumType enumType, bool isSchemaTemplate, Platform platform)
        {
            if (enumType == null) return;
            enumType.Schema = ResolveTableSchema(enumType.Schema, enumType.Name, "enum type", isSchemaTemplate, platform);
        }

        public static void Resolve(PostgreSqlDomainType domainType, bool isSchemaTemplate, Platform platform)
        {
            if (domainType == null) return;
            domainType.Schema = ResolveTableSchema(domainType.Schema, domainType.Name, "domain type", isSchemaTemplate, platform);
        }

        public static void Resolve(PostgreSqlSequence sequence, bool isSchemaTemplate, Platform platform)
        {
            if (sequence == null) return;
            sequence.Schema = ResolveTableSchema(sequence.Schema, sequence.Name, "sequence", isSchemaTemplate, platform);
        }

        /// <summary>
        /// Resolves all platform-typed children of a template in one pass. Called from
        /// Template.Load after deserialization completes. Fails loud (throws) when the
        /// template's Product / Platform isn't set rather than silently no-opping —
        /// a silent no-op would leave Schema fields unresolved and produce confusing
        /// downstream "schema name is null" DDL errors several layers away.
        ///
        /// In production Product.Platform always comes through PlatformJsonConverter +
        /// ParsePlatform, which reject Unknown at deserialization. So a thrown error
        /// here means the caller built a Template programmatically without hooking up
        /// the Product (a programmer error) — which is precisely when failing loud helps.
        /// </summary>
        public static void Resolve(Template template)
        {
            if (template == null)
                throw new ArgumentNullException(nameof(template));
            if (template.Product == null)
                throw new InvalidOperationException(
                    $"Template '{template.Name}' has no Product set. SchemaDefaultResolver " +
                    $"requires Template.Product to determine the platform default. Did you " +
                    $"construct the Template directly instead of going through Template.Load?");
            var platform = template.Product.Platform;
            if (platform == Platform.Unknown)
                throw new InvalidOperationException(
                    $"Template '{template.Name}' (file: {template.FilePath}) has Product.Platform = " +
                    $"Unknown. Set Platform to SqlServer, PostgreSQL, or MySQL in Product.json.");

            var isSchemaTemplate = template.IsSchemaTemplate;

            try
            {
                foreach (var table in template.Tables)
                {
                    switch (table)
                    {
                        case SqlServerTable sst: Resolve(sst, isSchemaTemplate, platform); break;
                        case PostgreSqlTable pgt: Resolve(pgt, isSchemaTemplate, platform); break;
                        // MySqlTable: no schema defaulting (MySQL has no namespace concept).
                    }
                }

                foreach (var view in template.IndexedViews)
                    Resolve(view, isSchemaTemplate, platform);

                foreach (var view in template.MaterializedViews)
                    Resolve(view, isSchemaTemplate, platform);

                // The three declarative PostgreSQL types. Their absence here is what made the overloads
                // above unreachable: a template's enum types, domain types and sequences were never
                // visited, so their Schema stayed null all the way to the quench, which COALESCEs it to
                // 'public'. Under a schema template that put a tenant's own objects in public on every
                // tenant -- silently, at exit 0. Tables, FKs, indexed views and materialized views all
                // resolved correctly, which is exactly why it survived: the objects a user checks first
                // are the ones that were already right.
                foreach (var enumType in template.EnumTypes)
                    Resolve(enumType, isSchemaTemplate, platform);

                foreach (var domainType in template.DomainTypes)
                    Resolve(domainType, isSchemaTemplate, platform);

                foreach (var sequence in template.Sequences)
                    Resolve(sequence, isSchemaTemplate, platform);
            }
            catch (InvalidOperationException inner)
            {
                // Rewrap with template context so a user staring at a multi-template product
                // can find the offending JSON file without grep-spelunking.
                throw new InvalidOperationException(
                    $"In template '{template.Name}' (file: {template.FilePath}): {inner.Message}",
                    inner);
            }
        }

        private static string ResolveTableSchema(
            string existing, string ownerName, string ownerKind, bool isSchemaTemplate, Platform platform)
        {
            if (isSchemaTemplate)
            {
                if (string.IsNullOrWhiteSpace(existing) || existing == SchemaNameToken)
                    return SchemaNameToken;

                throw new InvalidOperationException(
                    $"The {ownerKind} '{ownerName}' in a schema template has an explicit Schema='{existing}'. " +
                    $"Schema templates require this field to be omitted, empty, or the literal '{SchemaNameToken}' " +
                    $"— the {ownerKind} lives in {SchemaNameToken} by construction. " +
                    $"Move shared {ownerKind}s to a non-schema-template (which runs first in TemplateOrder).");
            }

            return string.IsNullOrWhiteSpace(existing) ? platform.GetDefaultSchema() : existing;
        }

        private static string ResolveRelatedTableSchema(string existing, bool isSchemaTemplate, Platform platform)
        {
            if (isSchemaTemplate)
            {
                // FK RelatedTableSchema is permissive: unset → token; literal token → token (no-op);
                // literal hard value → preserved as a cross-schema reference.
                return string.IsNullOrWhiteSpace(existing) ? SchemaNameToken : existing;
            }

            return string.IsNullOrWhiteSpace(existing) ? platform.GetDefaultSchema() : existing;
        }
    }
}
