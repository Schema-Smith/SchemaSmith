// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using log4net;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs;

public class ExtractionFileIndex
{
    private readonly ILog _log = LogFactory.GetLogger("ProgressLog");
    private readonly Dictionary<string, List<string>> _fileIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _writtenFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedFromOrphans = new(StringComparer.OrdinalIgnoreCase);

    public void BuildIndex(string baseFolderPath)
    {
        var directory = DirectoryWrapper.GetFromFactory();
        if (!directory.Exists(baseFolderPath)) return;

        var allFiles = directory.GetFiles(baseFolderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".sqlerror", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

        foreach (var filePath in allFiles)
        {
            var fileName = Path.GetFileName(filePath);
            if (!_fileIndex.TryGetValue(fileName, out var paths))
            {
                paths = [];
                _fileIndex[fileName] = paths;
            }
            paths.Add(filePath);
        }
    }

    public string FindExistingPath(string fileName)
    {
        if (!_fileIndex.TryGetValue(fileName, out var paths))
        {
            if (fileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                var errorVariant = Path.ChangeExtension(fileName, ".sqlerror");
                if (_fileIndex.TryGetValue(errorVariant, out var errorPaths) && errorPaths.Count == 1)
                    return errorPaths[0];
            }
            return null;
        }

        if (paths.Count == 1) return paths[0];

        var folders = string.Join(", ", paths.Select(Path.GetDirectoryName));
        _log.Warn($"Found {fileName} in multiple subfolders: {folders} — writing to base folder");
        return null;
    }

    public void MarkWritten(string filePath)
    {
        _writtenFiles.Add(filePath);
    }

    public bool WasWritten(string filePath) => _writtenFiles.Contains(filePath);

    public void ExcludeFromOrphans(string fileName)
    {
        _excludedFromOrphans.Add(fileName);
    }

    public List<string> GetOrphans()
    {
        return _fileIndex.Values
            .SelectMany(paths => paths)
            .Where(path => !_writtenFiles.Contains(path)
                        && !_excludedFromOrphans.Contains(Path.GetFileName(path)))
            .ToList();
    }
}
