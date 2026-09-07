// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Schema.Isolators;
using Schema.Utility;
using Schema.Configuration;

namespace Schema.Domain
{
    public class Product
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string Name { get; set; } = "";

        // Recommended, not required: SchemaQuench runs it only when it is set
        // (ProductQuench: `if (!string.IsNullOrWhiteSpace(...))`), so marking it Required made
        // --Validate reject packages that deploy perfectly well.
        [SchemaProperty(Description = "Recommended. SQL run after deployment to validate the product; skipped when unset.")]
        [JsonProperty(Order = 2)]
        public string ValidationScript { get; set; }

        [JsonProperty(Order = 3)]
        public List<string> TemplateOrder { get; set; } = [];

        [JsonProperty(Order = 4)]
        public Dictionary<string, string> ScriptTokens { get; set; } = [];

        [JsonProperty(Order = 5)]
        public List<ProductFolder> ScriptFolders { get; set; } = [];

        [JsonProperty(Order = 6)]
        [DefaultValue("{{repo_path}}/.git/HEAD")]
        public string BranchNameFile { get; set; } = "{{repo_path}}/.git/HEAD";

        [JsonProperty(Order = 7)]
        [DefaultValue("ref: refs/heads/")]
        public string BeforeBranchNameMask { get; set; } = "ref: refs/heads/";

        [JsonProperty(Order = 8)]
        public string AfterBranchNameMask { get; set; } = "";

        [JsonProperty(Order = 9)]
        public bool? DropUnknownIndexes { get; set; }

        [JsonProperty(Order = 10)]
        public string BaselineValidationScript { get; set; }

        [JsonProperty(Order = 11)]
        public string VersionStampScript { get; set; }

        [JsonConverter(typeof(PlatformJsonConverter))]
        [JsonProperty(Order = 12)]
        public Platform Platform { get; set; }

        [JsonProperty(Order = 13)]
        public string MinimumVersion { get; set; }

        [SchemaProperty]
        [JsonProperty(Order = 14)]
        [JsonConverter(typeof(StringEnumConverter))]
        public CheckConstraintStyle CheckConstraintStyle { get; set; }

        [JsonProperty(Order = 15)]
        [DefaultValue(true)]
        public bool DropTablesRemovedFromProduct { get; set; } = true;

        [JsonProperty(Order = 16)]
        public bool? DropColumnsRemovedFromProduct { get; set; }

        [JsonProperty(Order = 17)]
        public bool? DropForeignKeysRemovedFromProduct { get; set; }

        [JsonProperty(Order = 18)]
        public bool? DropCheckConstraintsRemovedFromProduct { get; set; }

        [JsonProperty(Order = 19)]
        [SchemaProperty(Platforms = [Platform.PostgreSQL])]
        public bool? DropExcludeConstraintsRemovedFromProduct { get; set; }

        [JsonProperty(Order = 20)]
        [SchemaProperty(Platforms = [Platform.SqlServer, Platform.PostgreSQL])]
        public bool? DropStatisticsRemovedFromProduct { get; set; }

        [JsonProperty(Order = 21)]
        public bool? DropIndexesRemovedFromProduct { get; set; }

        [SchemaProperty(Platforms = [Platform.SqlServer])]
        public bool? DropSchemaBoundDependents { get; set; }

        /// <summary>
        /// Product tier of the rebuild-policy cascade. Null inherits from the environment. Unlike the
        /// <c>Drop*RemovedFromProduct</c> flags above it, the levels do not combine — the nearest
        /// declared policy wins whole (<c>ProductQuench.ResolveCascadedPolicy</c>).
        /// </summary>
        [JsonProperty(Order = 22)]
        public RebuildPolicy RebuildPolicy { get; set; }

        [JsonIgnore]
        public List<ProductFolder> BeforeFolders => ScriptFolders?.FindAll(f => f.QuenchSlot == ProductQuenchSlot.Before) ?? [];

        [JsonIgnore]
        public List<ProductFolder> AfterFolders => ScriptFolders?.FindAll(f => f.QuenchSlot == ProductQuenchSlot.After) ?? [];

        [JsonIgnore]
        public string FilePath { get; set; } = "";

        [JsonIgnore]
        public Dictionary<string, string> QueryTokens { get; set; } = [];

        [JsonIgnore]
        public Dictionary<string, string> NonQueryTokens { get; set; } = [];

        /// <summary>
        /// File-token resolution failures collected instead of thrown when <see cref="Load"/> is
        /// called with <c>tolerateFileTokenErrors: true</c> (--Validate's lenient load). Empty on
        /// the deploy path, which never tolerates an unresolvable file token. Mirrors
        /// <see cref="Template.FileTokenErrors"/> — PackageLoader turns each entry into an
        /// SS-TOK-004 finding.
        /// </summary>
        [JsonIgnore]
        public List<FileTokenError> FileTokenErrors { get; } = [];

        /// <summary>
        /// Loads a Product from a Product.json file path for display purposes only.
        /// Does NOT resolve tokens or load scripts. Use Load() (config-based) for runtime use.
        /// </summary>
        public static Product LoadForDisplay(string productFilePath)
        {
            var product = JsonHelper.ProductLoad(productFilePath);
            product.FilePath = Path.GetDirectoryName(productFilePath) ?? "";
            return product;
        }

        /// <summary>
        /// Loads a Product from the configured SchemaPackagePath (config or zip).
        /// Unified: no platform validation — accepts all platforms.
        /// </summary>
        /// <param name="missingMemberHandling">
        /// Deploy path leaves this at the default (Error) so an unrecognised property still stops
        /// the run. `--Validate` (PackageLoader) passes Ignore instead — it needs Product.Platform
        /// to run its checks at all, and a single misnamed property in Product.json shouldn't take
        /// down every other finding the run would otherwise report; JsonSchemaCheck independently
        /// re-validates the raw Product.json against products.*.schema and reports SS-JSON-001 for
        /// the property regardless of which way this loaded.
        /// </param>
        /// <param name="tolerateFileTokenErrors">
        /// Deploy path leaves this false: an unresolvable <c>ScriptTokens</c> file reference
        /// throws immediately and aborts the run, same as always. `--Validate` (PackageLoader)
        /// passes true so the failure lands in <see cref="FileTokenErrors"/> as a reportable
        /// finding instead of aborting the whole load.
        /// </param>
        public static Product Load(MissingMemberHandling missingMemberHandling = MissingMemberHandling.Error, bool tolerateFileTokenErrors = false)
        {
            var config = FactoryContainer.ResolveOrCreate<IConfigurationRoot>();
            var schemaPackagePath = config[SettingsKeys.SchemaPackagePath] ?? "";

            if (ZipFileWrapper.IsValidZipFile(schemaPackagePath))
            {
                var zipFileWrapper = ZipFileWrapper.GetFromFactory(schemaPackagePath) as ZipFileWrapper;
                _ = ZipDirectoryWrapper.GetFromFactory(zipFileWrapper.ZipEntries);
                schemaPackagePath = ""; // use root of zip
            }
            else if (!DirectoryWrapper.GetFromFactory().Exists(schemaPackagePath))
                throw new Exception($"SchemaPackagePath not found '{schemaPackagePath}'");

            var productFilePath = Path.Combine(schemaPackagePath, "Product.json");
            var product = JsonHelper.ProductLoad(productFilePath, missingMemberHandling);
            product.FilePath = productFilePath;
            OverrideProductScriptTokens(config, product);
            var tokenErrors = TokenHelper.ResolveFileTokens(product.ScriptTokens, schemaPackagePath, product.Platform, tolerateFileTokenErrors);
            foreach (var tokenError in tokenErrors)
                product.FileTokenErrors.Add(new FileTokenError(product.FilePath, tokenError));
            product.ScriptTokens.Add("ProductName", product.Name);

            product.InstanceLoad();

            return product;
        }

        /// <summary>
        /// Saves a Product to a JSON file using canonical platform serialization.
        /// </summary>
        public static void Save(string filePath, Product product)
        {
            JsonHelper.Write(filePath, product);
        }

        private static void OverrideProductScriptTokens(IConfigurationRoot config, Product product)
        {
            foreach (var token in GetScriptTokensFromAppConfig(config)
                         .Where(token => product.ScriptTokens.ContainsKey(token.Key) && !string.IsNullOrEmpty(token.Value)))
                product.ScriptTokens[token.Key] = token.Value;
        }

        private static IEnumerable<KeyValuePair<string, string>> GetScriptTokensFromAppConfig(IConfigurationRoot config)
        {
            return config.GetSection(SettingsKeys.ScriptTokens)
                .AsEnumerable()
                .Where(x => x.Value != null)
                .Select(x => new KeyValuePair<string, string>(x.Key.Replace("ScriptTokens:", ""), x.Value ?? ""));
        }

        internal void InstanceLoad()
        {
            QueryTokens = TokenHelper.SplitOutQueryTokens(ScriptTokens);
            NonQueryTokens = ScriptTokens;
            var scriptTokens = ScriptTokens.ToList();
            foreach (var folder in ScriptFolders)
                folder.LoadSqlFiles(Path.GetDirectoryName(FilePath) ?? "", scriptTokens, Platform);
            ValidationScript = SqlScript.TokenReplace(ValidationScript ?? "", scriptTokens);
            BaselineValidationScript = SqlScript.TokenReplace(BaselineValidationScript ?? "", scriptTokens);
            VersionStampScript = SqlScript.TokenReplace(VersionStampScript ?? "", scriptTokens);
        }
    }
}
