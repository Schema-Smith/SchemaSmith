// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Schema.Isolators;
using Schema.DataAccess;
using Schema.Utility;

namespace Schema.Domain
{
    public class SqlScript
    {
        [JsonProperty(Order = 1)]
        public string Name { get; set; } = "";

        [JsonIgnore]
        public string FilePath { get; set; } = "";

        [JsonProperty(Order = 3)]
        public List<string> Batches { get; private set; } = [];

        [JsonIgnore]
        public bool HasBeenQuenched { get; set; }

        [JsonIgnore]
        public Exception Error { get; set; }

        [JsonIgnore]
        public ScriptOutcome Outcome { get; set; } = ScriptOutcome.Applied;

        [JsonProperty(Order = 2)]
        public string LogPath => LongPathSupport.StripLongPathPrefix(FilePath);

        [JsonIgnore]
        public List<string> RemainingTokens = [];

        public static SqlScript Load(string filePath, List<KeyValuePair<string, string>> scriptTokens, Platform platform = Platform.SqlServer)
        {
            if (!ProductFileWrapper.GetFromFactory().Exists(filePath))
                throw new RunFailedException($"File {LongPathSupport.StripLongPathPrefix(filePath)} does not exist");

            try
            {
                var script = new SqlScript { Name = Path.GetFileName(filePath), FilePath = filePath };
                var rawContent = ProductFileWrapper.GetFromFactory().ReadAllText(filePath);
                // Split batches BEFORE token resolution so the batch splitter only
                // processes the small template text, not expanded multi-MB token values
                var batches = SplitBatches(rawContent, platform);
                script.Batches.AddRange(batches.Select(b => TokenReplace(b, scriptTokens, platform, script.RemainingTokens)));
                return script;
            }
            catch (Exception e)
            {
                throw new RunFailedException($"Error loading {LongPathSupport.StripLongPathPrefix(filePath)}\r\n{e.Message}", e);
            }
        }

        public SqlScript Clone()
        {
            var clone = new SqlScript { Name = Name, FilePath = FilePath };
            clone.Batches.AddRange(Batches);
            return clone;
        }

        public void CheckForUnresolvedTokens(string dbName, string serverMsg, Action<string> logLine)
        {
            foreach (var batch in Batches)
                LogUnresolvedTokens(dbName, batch, serverMsg, logLine);
        }

        public void ReplaceQueryTokens(List<KeyValuePair<string, string>> queryTokens)
        {
            if (queryTokens.Count == 0) return;
            Batches = Batches.Select(batch => TokenReplace(batch, queryTokens)).ToList();
        }

        public static string TokenReplace(string script, List<KeyValuePair<string, string>> scriptTokens,
            Platform platform = Platform.SqlServer, List<string> remainingTokens = null)
        {
            var tokensToReplace = TokenHelper.GetTokensFromString(script);
            if (tokensToReplace.Count == 0) return script;

            // Build a case-insensitive lookup for O(1) token resolution instead of O(n) scanning
            var tokenLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in scriptTokens)
                tokenLookup.TryAdd(t.Key, t.Value);

            foreach (var token in tokensToReplace)
            {
                if (!tokenLookup.TryGetValue(token, out var rawValue)) { remainingTokens?.Add(token); continue; }
                var placeholder = $"{{{{{token}}}}}";
                var sb = new StringBuilder();
                int pos = 0, idx;
                while ((idx = script.IndexOf(placeholder, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    sb.Append(script, pos, idx - pos);
                    var v = rawValue;
                    if (idx > 0 && script[idx - 1] == '\'')
                    {
                        // MySQL interprets backslashes as escape sequences in string literals
                        if (platform.GetBasePlatform() == Platform.MySQL)
                            v = v.Replace("\\", "\\\\");
                        v = v.Replace("'", "''");
                    }
                    sb.Append(v);
                    pos = idx + placeholder.Length;
                }
                sb.Append(script, pos, script.Length - pos);
                script = sb.ToString();
            }

            return script;
        }

        internal static List<string> SplitBatches(string content, Platform platform)
        {
            return platform.GetBasePlatform() switch
            {
                Platform.SqlServer => SqlHelpers.SplitIntoBatches(content),
                Platform.MySQL => BatchSplitter.Split(content),
                Platform.PostgreSQL => PostgreSqlStatementSplitter.Split(content),
                _ => [content]
            };
        }

        private void LogUnresolvedTokens(string dbName, string batch, string serverMsg, Action<string> logLine)
        {
            var unresolvedTokens = TokenHelper.GetTokensFromString(batch);
            foreach (var token in unresolvedTokens)
                logLine?.Invoke($"{serverMsg}[{dbName}]      Unresolved token: {{{{{token}}}}}");
        }
    }
}
