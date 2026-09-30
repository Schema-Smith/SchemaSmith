// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.Utility;
using Schema.Delivery;

namespace Schema.Domain
{
    public class Table : DynamicBase
    {
        // See Product.SchemaRef. Declared on Table rather than on DynamicBase deliberately: only a
        // root-of-file object may carry "$schema", so a stray one nested inside a Column, Index,
        // ForeignKey or CheckConstraint still fails loudly the way any other unknown key does.
        [SchemaProperty(Description = "Optional. Relative path to the generated JSON Schema, for editor validation and autocomplete. Ignored at deploy time.")]
        [JsonProperty("$schema", Order = 0, NullValueHandling = NullValueHandling.Ignore)]
        public string SchemaRef { get; set; }

        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 2)]
        public string Name { get; set; } = "";

        [JsonProperty(Order = 3)]
        public List<Column> Columns { get; set; } = [];

        [JsonProperty(Order = 4)]
        public List<Index> Indexes { get; set; } = [];

        [JsonProperty(Order = 5)]
        public List<ForeignKey> ForeignKeys { get; set; } = [];

        [JsonProperty(Order = 6)]
        public List<CheckConstraint> CheckConstraints { get; set; } = [];

        [JsonProperty(Order = 80)]
        public string ShouldApplyExpression { get; set; }

        // Labels a conditional variant: the intent behind its ShouldApplyExpression,
        // echoed in quench log messages when the variant applies.
        [SchemaProperty(MaxLength = 128, Description = "Optional label for a conditional variant — names the intent behind its ShouldApplyExpression and appears in deployment logging when the variant is applied.")]
        [JsonProperty(Order = 82)]
        public string VariantName { get; set; }

        [JsonProperty(Order = 81)]
        [JsonConverter(typeof(DataDeliveryListJsonConverter))]
        [SchemaProperty(SingleOrArray = true)]
        public List<DataDelivery> DataDelivery { get; set; } = [];

        public bool ShouldSerializeDataDelivery() => DataDelivery is { Count: > 0 };

        // Bridges the IDeliverableTable contract (plural DataDeliveries) to the domain model's
        // DataDelivery list. An implicit (non-explicit) member so it is inherited by every platform
        // subclass (SqlServerTable, PostgreSqlTable, MySqlTable) that declares IDeliverableTable —
        // an explicit interface member here would not compile, since Table itself does not
        // implement IDeliverableTable (only the platform subclasses do).
        [JsonIgnore]
        public IReadOnlyList<DataDelivery> DataDeliveries => DataDelivery ?? (IReadOnlyList<DataDelivery>)[];

        [SchemaProperty(AuthoredOnly = true, Description = "When set, overrides the template- and product-level DropColumnsRemovedFromProduct flag for this table only. Null inherits from the template (or product) setting.")]
        [JsonProperty(Order = 85)]
        public bool? DropColumnsRemovedFromProduct { get; set; }

        [SchemaProperty(AuthoredOnly = true, Description = "When set, overrides the template- and product-level DropForeignKeysRemovedFromProduct flag for this table only. Null inherits from the template (or product) setting.")]
        [JsonProperty(Order = 86)]
        public bool? DropForeignKeysRemovedFromProduct { get; set; }

        [SchemaProperty(AuthoredOnly = true, Description = "When set, overrides the template- and product-level DropCheckConstraintsRemovedFromProduct flag for this table only. Null inherits from the template (or product) setting.")]
        [JsonProperty(Order = 87)]
        public bool? DropCheckConstraintsRemovedFromProduct { get; set; }

        [SchemaProperty(AuthoredOnly = true, Platforms = [Platform.PostgreSQL], Description = "When set, overrides the template- and product-level DropExcludeConstraintsRemovedFromProduct flag for this table only. Null inherits from the template (or product) setting.")]
        [JsonProperty(Order = 88)]
        public bool? DropExcludeConstraintsRemovedFromProduct { get; set; }

        [SchemaProperty(AuthoredOnly = true, Platforms = [Platform.SqlServer, Platform.PostgreSQL], Description = "When set, overrides the template- and product-level DropStatisticsRemovedFromProduct flag for this table only. Null inherits from the template (or product) setting.")]
        [JsonProperty(Order = 89)]
        public bool? DropStatisticsRemovedFromProduct { get; set; }

        [SchemaProperty(AuthoredOnly = true, Description = "When set, overrides the template- and product-level DropIndexesRemovedFromProduct flag for this table only. Null inherits from the template (or product) setting.")]
        [JsonProperty(Order = 90)]
        public bool? DropIndexesRemovedFromProduct { get; set; }

        [SchemaProperty(Description = "When true, this table is protected from drop-by-absence and the protection is sticky: it is persisted in SchemaSmith's ownership tracking so a protected table that is later removed from the product package is skipped (never silently dropped) rather than reconciled away. Set false and re-deploy while the table is still in the package to clear the protection before an intended removal. Default false.")]
        [JsonProperty(Order = 91)]
        public bool PreventDrop { get; set; }

        // AuthoredOnly for the same reason the Drop*RemovedFromProduct family is: no catalog records the
        // intent behind an alter-vs-rebuild choice, so extraction has nothing to read it back from and a
        // re-extract would silently revert whatever was authored.
        [SchemaProperty(AuthoredOnly = true, Description = "When set, overrides the template-, product- and environment-level RebuildPolicy for this table only. Null inherits from the nearest level that declares one. The winning policy applies whole — a policy declared here does NOT pick up fields it omits from a higher level.")]
        [JsonProperty(Order = 92)]
        public RebuildPolicy RebuildPolicy { get; set; }

        [JsonProperty(Order = 90)]
        public string OldName { get; set; }

        /// <summary>
        /// Loads a Table from a JSON file using platform-aware deserialization.
        /// Returns the correct platform subclass (SqlServerTable, PostgreSqlTable, MySqlTable).
        /// </summary>
        public static Table Load(string filePath, Platform platform)
        {
            try
            {
                return JsonHelper.TableLoad(filePath, platform);
            }
            catch (Exception e)
            {
                throw new Exception($"Error loading table from {filePath}\r\n{e.Message}", e);
            }
        }

        public virtual void ResolveScriptTokensInTableComponentScripts(List<KeyValuePair<string, string>> tokens)
        {
            ShouldApplyExpression = TableTokenReplace(ShouldApplyExpression, tokens.Concat(GetCustomTokens(Extensions)).ToList());
            var tableTokens = tokens.Concat(GetCustomTokens(Extensions, "Table.")).ToList();
            foreach (var check in CheckConstraints)
            {
                var scriptTokens = tableTokens.Concat(GetCustomTokens(check.Extensions)).ToList();
                check.ShouldApplyExpression = TableTokenReplace(check.ShouldApplyExpression, scriptTokens);
                check.Expression = TableTokenReplace(check.Expression, scriptTokens);
            }
            foreach (var column in Columns)
            {
                var scriptTokens = tableTokens.Concat(GetCustomTokens(column.Extensions)).ToList();
                column.ShouldApplyExpression = TableTokenReplace(column.ShouldApplyExpression, scriptTokens);
                column.Default = TableTokenReplace(column.Default, scriptTokens);
            }
            foreach (var key in ForeignKeys)
            {
                var scriptTokens = tableTokens.Concat(GetCustomTokens(key.Extensions)).ToList();
                key.ShouldApplyExpression = TableTokenReplace(key.ShouldApplyExpression, scriptTokens);
            }
            foreach (var index in Indexes)
            {
                var scriptTokens = tableTokens.Concat(GetCustomTokens(index.Extensions)).ToList();
                index.ShouldApplyExpression = TableTokenReplace(index.ShouldApplyExpression, scriptTokens);
            }
        }

        internal static string TableTokenReplace(string script, List<KeyValuePair<string, string>> scriptTokens)
        {
            var tokensToReplace = TokenHelper.GetTokensFromString(script);
            if (tokensToReplace.Count == 0) return script;
            var replacableTokens = scriptTokens
                .Where(t => tokensToReplace.Any(tr => tr.EqualsIgnoringCase(t.Key)))
                .ToList();
            replacableTokens.ForEach(token => { script = Regex.Replace(script, $@"\{{\{{{token.Key}\}}\}}", token.Value, RegexOptions.IgnoreCase); });
            return script;
        }

        internal static List<KeyValuePair<string, string>> GetCustomTokens(JToken extensions, string baseName = "")
        {
            var tokens = new List<KeyValuePair<string, string>>();
            if (extensions is not JObject jObj) return tokens;

            foreach (var prop in jObj.Properties())
            {
                if (prop.Value.Type == JTokenType.Object)
                    ProcessJObject(prop.Value.ToObject<JObject>(), tokens, $"{baseName}{prop.Name}.");
                else if (prop.Value.Type == JTokenType.Array)
                    tokens.Add(new KeyValuePair<string, string>($"{baseName}{prop.Name}", string.Join(",", prop.Value.ToObject<JArray>().Select(x => x.ToString()))));
                else
                    tokens.Add(new KeyValuePair<string, string>($"{baseName}{prop.Name}", prop.Value.ToString()));
            }
            return tokens;
        }

        private static void ProcessJObject(JObject jObj, List<KeyValuePair<string, string>> tokens, string baseName)
        {
            foreach (var jProp in jObj.Properties())
            {
                if (jProp.Value.Type == JTokenType.Object)
                    ProcessJObject(jProp.Value.ToObject<JObject>(), tokens, $"{baseName}{jProp.Name}.");
                else if (jProp.Value.Type == JTokenType.Array)
                    tokens.Add(new KeyValuePair<string, string>($"{baseName}{jProp.Name}", string.Join(",", jProp.Value.ToObject<JArray>().Select(x => x.ToString()))));
                else
                    tokens.Add(new KeyValuePair<string, string>($"{baseName}{jProp.Name}", jProp.Value.ToString()));
            }
        }
    }
}
