// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.Domain.MariaDb;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;

namespace Schema.Domain
{
    public static class PlatformDeserializer
    {
        // Two of three surfaces (generated .json-schemas' additionalProperties:false, --Validate)
        // already reject an unrecognised package property; Newtonsoft's own default
        // (MissingMemberHandling.Ignore) was the odd one out, silently discarding a typo'd
        // property at deploy time. Never trips on Extensions: DynamicBase declares it as a real
        // property, so Newtonsoft always matches it as a member and parses its content as an
        // opaque JToken with no member-checking inside.
        internal static readonly JsonSerializerSettings StrictSettings = new()
        {
            MissingMemberHandling = MissingMemberHandling.Error,
            DateParseHandling = DateParseHandling.None
        };

        public static Table DeserializeTable(string json, Platform platform)
        {
            return Deserialize<Table>(json, platform, nameof(Table));
        }

        public static Column DeserializeColumn(string json, Platform platform)
        {
            return Deserialize<Column>(json, platform, nameof(Column));
        }

        public static Index DeserializeIndex(string json, Platform platform)
        {
            return Deserialize<Index>(json, platform, nameof(Index));
        }

        public static ForeignKey DeserializeForeignKey(string json, Platform platform)
        {
            return Deserialize<ForeignKey>(json, platform, nameof(ForeignKey));
        }

        public static CheckConstraint DeserializeCheckConstraint(string json, Platform platform)
        {
            return Deserialize<CheckConstraint>(json, platform, nameof(CheckConstraint));
        }

        // missingMemberHandling defaults to Error (unchanged deploy behavior) — --Validate's
        // lenient load path (PackageLoader) is the only caller that passes Ignore, mirroring the
        // same leniency Product.Load already has for Product.json. See Template.Load's own doc
        // comment for why: a parseable-but-wrong Template.json can now load fully instead of
        // excluding the whole template over one bad property.
        public static Template DeserializeTemplate(string json, Platform platform, MissingMemberHandling missingMemberHandling = MissingMemberHandling.Error)
        {
            return Deserialize<Template>(json, platform, nameof(Template), missingMemberHandling);
        }

        public static PostgreSqlMaterializedView DeserializeMaterializedView(string json, Platform platform)
        {
            if (platform != Platform.PostgreSQL)
                throw new ArgumentException("Materialized views are only supported on PostgreSQL", nameof(platform));
            return JsonConvert.DeserializeObject<PostgreSqlMaterializedView>(json, StrictSettings)
                ?? throw new JsonSerializationException("Failed to deserialize materialized view");
        }

        public static SqlServerIndexedView DeserializeIndexedView(string json, Platform platform)
        {
            if (platform != Platform.SqlServer)
                throw new ArgumentException("Indexed views are only supported on SQL Server", nameof(platform));
            return JsonConvert.DeserializeObject<SqlServerIndexedView>(json, StrictSettings)
                ?? throw new JsonSerializationException("Failed to deserialize indexed view");
        }

        // missingMemberHandling defaults to Error for every caller except DeserializeTemplate,
        // which is the only one that ever passes Ignore through (--Validate only). Table/Column/
        // Index/ForeignKey/CheckConstraint deserialization stays hardcoded strict regardless.
        private static T Deserialize<T>(string json, Platform platform, string typeName, MissingMemberHandling missingMemberHandling = MissingMemberHandling.Error) where T : class
        {
            var type = GetType<T>(typeName, platform);
            var settings = GetSerializerSettings(platform, missingMemberHandling);
            return JsonConvert.DeserializeObject(json, type, settings) as T
                ?? throw new JsonSerializationException($"Failed to deserialize {typeName} for platform {platform}");
        }

        private static JsonSerializerSettings GetSerializerSettings(Platform platform, MissingMemberHandling missingMemberHandling = MissingMemberHandling.Error)
        {
            return new JsonSerializerSettings
            {
                MissingMemberHandling = missingMemberHandling,
                DateParseHandling = DateParseHandling.None,
                Converters = new List<JsonConverter>
                {
                    new PlatformItemConverter(typeof(Column), GetColumnType(platform)),
                    new PlatformItemConverter(typeof(Index), GetIndexType(platform)),
                    new PlatformItemConverter(typeof(ForeignKey), GetForeignKeyType(platform)),
                    new PlatformItemConverter(typeof(CheckConstraint), GetCheckConstraintType(platform))
                }
            };
        }

        private static Type GetType<T>(string typeName, Platform platform)
        {
            if (typeof(T) == typeof(Table)) return GetTableType(platform);
            if (typeof(T) == typeof(Column)) return GetColumnType(platform);
            if (typeof(T) == typeof(Index)) return GetIndexType(platform);
            if (typeof(T) == typeof(ForeignKey)) return GetForeignKeyType(platform);
            if (typeof(T) == typeof(CheckConstraint)) return GetCheckConstraintType(platform);
            if (typeof(T) == typeof(Template)) return GetTemplateType(platform);
            throw new ArgumentException($"Unsupported type: {typeName}");
        }

        public static Type GetTableType(Platform platform)
        {
            return platform switch
            {
                Platform.SqlServer => typeof(SqlServerTable),
                Platform.PostgreSQL => typeof(PostgreSqlTable),
                Platform.MySQL => typeof(MySqlTable),
                Platform.MariaDb => typeof(MariaDbTable),
                _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, $"Unsupported platform: {platform}")
            };
        }

        public static Type GetColumnType(Platform platform)
        {
            return platform switch
            {
                Platform.SqlServer => typeof(SqlServerColumn),
                Platform.PostgreSQL => typeof(PostgreSqlColumn),
                Platform.MySQL => typeof(MySqlColumn),
                Platform.MariaDb => typeof(MariaDbColumn),
                _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, $"Unsupported platform: {platform}")
            };
        }

        public static Type GetIndexType(Platform platform)
        {
            return platform switch
            {
                Platform.SqlServer => typeof(SqlServerIndex),
                Platform.PostgreSQL => typeof(PostgreSqlIndex),
                Platform.MySQL => typeof(MySqlIndex),
                Platform.MariaDb => typeof(MySqlIndex),
                _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, $"Unsupported platform: {platform}")
            };
        }

        public static Type GetForeignKeyType(Platform platform)
        {
            return platform switch
            {
                Platform.SqlServer => typeof(SqlServerForeignKey),
                Platform.PostgreSQL => typeof(PostgreSqlForeignKey),
                Platform.MySQL => typeof(MySqlForeignKey),
                Platform.MariaDb => typeof(MySqlForeignKey),
                _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, $"Unsupported platform: {platform}")
            };
        }

        public static Type GetCheckConstraintType(Platform platform)
        {
            return platform switch
            {
                Platform.SqlServer => typeof(CheckConstraint),
                Platform.PostgreSQL => typeof(PostgreSqlCheckConstraint),
                Platform.MySQL => typeof(CheckConstraint),
                Platform.MariaDb => typeof(CheckConstraint),
                _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, $"Unsupported platform: {platform}")
            };
        }

        public static Type GetTemplateType(Platform platform)
        {
            return platform switch
            {
                Platform.SqlServer => typeof(SqlServerTemplate),
                Platform.PostgreSQL => typeof(PostgreSqlTemplate),
                Platform.MySQL => typeof(MySqlTemplate),
                Platform.MariaDb => typeof(MySqlTemplate),
                _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, $"Unsupported platform: {platform}")
            };
        }
    }

    /// <summary>
    /// Redirects deserialization of base domain types to platform-specific subclass types.
    /// When Newtonsoft deserializes a List&lt;Column&gt; property, it would normally create base Column instances.
    /// This converter intercepts and creates the correct platform subclass (e.g., MySqlColumn) instead.
    /// </summary>
    internal class PlatformItemConverter : JsonConverter
    {
        private readonly Type _baseType;
        private readonly Type _targetType;

        public PlatformItemConverter(Type baseType, Type targetType)
        {
            _baseType = baseType;
            _targetType = targetType;
        }

        public override bool CanConvert(Type objectType) => objectType == _baseType;

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;
            var jObj = JObject.Load(reader);
            // Built from StrictSettings alone (no PlatformItemConverter registered) — avoids the
            // infinite recursion a full settings copy would cause, since the target type is a
            // subclass of the base type this converter intercepts, while still carrying the
            // unknown-property check onto the nested Column/Index/ForeignKey/CheckConstraint.
            var cleanSerializer = JsonSerializer.Create(PlatformDeserializer.StrictSettings);
            return jObj.ToObject(_targetType, cleanSerializer);
        }

        public override bool CanWrite => false;
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            => throw new NotImplementedException();
    }
}
