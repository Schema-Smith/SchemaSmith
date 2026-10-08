// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Schema.Isolators;

public class ZipDirectoryWrapper : IDirectory
{
    private List<IZipEntry> _zipEntries;

    public bool Exists(string path)
    {
        if (_zipEntries == null || string.IsNullOrEmpty(path)) return false;

        var normalizedPath = NormalizePath(path);
        return _zipEntries.Any(e =>
            e.FullName.Replace('\\', '/').StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
    {
        if (_zipEntries == null || string.IsNullOrEmpty(path)) return [];

        var normalizedPath = NormalizePath(path);
        return _zipEntries
            .Where(e =>
                e.FullName.Replace('\\', '/').StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                !e.FullName.Replace('\\', '/').EndsWith("/") &&
                !(searchOption == SearchOption.TopDirectoryOnly && e.FullName.Replace('\\', '/').Substring(normalizedPath.Length).TrimStart('/').Contains("/")) &&
                ((searchPattern ?? "*") == "*" || Regex.IsMatch(Path.GetFileName(e.FullName), $"^{Regex.Escape(searchPattern!).Replace(@"\*", ".*").Replace(@"\?", ".")}$", RegexOptions.IgnoreCase))
                )
            .Select(e => e.FullName)
            .ToArray();
    }

    // Immediate subfolders only, which is all a package read asks for (the template folders). A zip has no
    // directory entries of its own to rely on, so a folder is any first path segment below the given path.
    public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption)
    {
        if (searchOption != SearchOption.TopDirectoryOnly || (searchPattern ?? "*") != "*")
            throw new NotImplementedException("A zip-backed package lists only immediate subfolders.");
        if (_zipEntries == null) return [];

        var normalizedPath = NormalizePath(path);
        return _zipEntries
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(name => name.StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase))
            .Select(name => name.Substring(normalizedPath.Length))
            .Where(rest => rest.Contains('/'))
            .Select(rest => rest.Substring(0, rest.IndexOf('/')))
            .Where(folder => folder.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(folder => normalizedPath + folder)
            .ToArray();
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(normalized) ? "" : normalized + "/";
    }

    public static IDirectory GetFromFactory(List<IZipEntry> zipEntries)
    {
        var zipDir = FactoryContainer.ResolveOrCreate<ZipDirectoryWrapper>(true);
        zipDir._zipEntries = zipEntries;
        return zipDir;
    }

    // Other IDirectory methods not used for zip access
    IDirectoryInfo IDirectory.CreateDirectory(string path) => throw new NotImplementedException();
    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) => throw new NotImplementedException();
    public IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern, SearchOption searchOption) => throw new NotImplementedException();
    // Not implemented for the same reason as EnumerateFiles above: a zip-backed package is not walked
    // this way. Kept in the same shape as its siblings rather than returning empty, so a caller that
    // reaches it fails loudly instead of silently seeing no files.
    public IEnumerable<TimestampedFile> EnumerateFilesWithTimestamps(string path, string searchPattern, SearchOption searchOption) => throw new NotImplementedException();
    public void Delete(string path, bool recursive = false) => throw new NotImplementedException();
    public void Move(string sourceDirName, string destDirName) => throw new NotImplementedException();
    public string GetCurrentDirectory() => throw new NotImplementedException();
}
