// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.Domain
{
    public class ScriptFolder
    {
        [SchemaProperty(Required = true)]
        [JsonProperty(Order = 1)]
        public string FolderPath { get; set; } = "";

        [SchemaProperty(Description = "Optional SQL predicate evaluated against the target at deployment time. Blank deploys the folder; a non-blank expression must return a boolean — true deploys the folder, false skips it (logged). Evaluated per target in the target engine's SQL; may read server properties, an environment-type function, a control table, or resolved tokens.")]
        [JsonProperty(Order = 11)]
        public string ShouldApplyExpression { get; set; }

        [SchemaProperty]
        [JsonProperty(Order = 10)]
        [JsonConverter(typeof(StringEnumConverter))]
        public ScriptObjectType ObjectType { get; set; }

        [JsonIgnore]
        public List<SqlScript> Scripts { get; } = [];

        public void LoadSqlFiles(string basePath, List<KeyValuePair<string, string>> scriptTokens, Platform platform = Platform.SqlServer)
        {
            // The folder's gate expression takes the same script-token pass as its scripts (#260) — and
            // independently of whether the folder has files — so a gate may reference any resolved token,
            // not just literal SQL. SchemaName stays per-iteration (resolved at quench time).
            if (!string.IsNullOrWhiteSpace(ShouldApplyExpression))
                ShouldApplyExpression = SqlScript.TokenReplace(ShouldApplyExpression, scriptTokens, platform);

            var sqlFilePath = Path.Combine(basePath, FolderPath);
            if (!ProductDirectoryWrapper.GetFromFactory().Exists(sqlFilePath)) return;
            var files = ProductDirectoryWrapper.GetFromFactory()
                .GetFiles(sqlFilePath, "*.sql", SearchOption.AllDirectories)
                .OrderBy(x => x)
                .ToList();

            // Parallel file load — mostly I/O bound (file reads + token resolution)
            var loaded = new ConcurrentDictionary<string, SqlScript>();
            var queue = new TaskQueueManager<string>(Environment.ProcessorCount * 2);
            files.ForEach(f => queue.AddToQueue(f, file => loaded[file] = SqlScript.Load(file, scriptTokens, platform)));
            queue.WaitForAll();
            foreach (var script in loaded.Values) script.ObjectType = ObjectType;

            // Preserve original file ordering
            Scripts.AddRange(files.Select(f => loaded[f]));
        }

        protected ScriptFolder DeepClone(ScriptFolder clone)
        {
            clone.FolderPath = FolderPath;
            clone.ObjectType = ObjectType;
            clone.ShouldApplyExpression = ShouldApplyExpression;
            clone.Scripts.AddRange(Scripts.Select(s => s.Clone()));
            return clone;
        }
    }
}
