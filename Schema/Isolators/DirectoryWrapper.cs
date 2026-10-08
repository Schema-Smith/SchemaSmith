// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Schema.Utility;

namespace Schema.Isolators;

public class DirectoryWrapper : IDirectory
{
    public bool Exists(string path)
    {
        return Directory.Exists(LongPathSupport.MakeSafeLongFilePath(path));
    }

    public IDirectoryInfo CreateDirectory(string path)
    {
        return new DirectoryInfoWrapper(Directory.CreateDirectory(LongPathSupport.MakeSafeLongFilePath(path)));
    }

    // The directory-listing members long-path-prefix the search root (so listing works on long
    // paths) and then strip the "\\?\" prefix from every returned path, so callers get clean,
    // caller-facing paths that are safe to string-manipulate (e.g. Path.GetRelativePath) or to pass
    // straight back to an isolator call. No-op on Linux, where MakeSafeLongFilePath /
    // StripLongPathPrefix leave the path untouched.
    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
    {
        return Directory.GetFiles(LongPathSupport.MakeSafeLongFilePath(path), searchPattern, Options(searchOption))
            .Select(LongPathSupport.StripLongPathPrefix).ToArray();
    }

    public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption)
    {
        return Directory.GetDirectories(LongPathSupport.MakeSafeLongFilePath(path), searchPattern, Options(searchOption))
            .Select(LongPathSupport.StripLongPathPrefix).ToArray();
    }

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
    {
        return Directory.EnumerateFiles(LongPathSupport.MakeSafeLongFilePath(path), searchPattern, Options(searchOption))
            .Select(LongPathSupport.StripLongPathPrefix);
    }

    public IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern, SearchOption searchOption)
    {
        return Directory.EnumerateFileSystemEntries(LongPathSupport.MakeSafeLongFilePath(path), searchPattern, Options(searchOption))
            .Select(LongPathSupport.StripLongPathPrefix);
    }

    public IEnumerable<TimestampedFile> EnumerateFilesWithTimestamps(string path, string searchPattern, SearchOption searchOption)
    {
        // DirectoryInfo.EnumerateFiles, not Directory.EnumerateFiles: the FileInfo it yields already
        // carries LastWriteTimeUtc from the directory walk, so reading it costs no further syscall.
        // The string overload throws that away and forces the caller to stat each entry back.
        //
        // Select keeps this deferred -- EnumerateFiles is lazy and projection does not change that, so
        // a caller taking the first twenty of fifty thousand walks twenty.
        return new DirectoryInfo(LongPathSupport.MakeSafeLongFilePath(path))
            .EnumerateFiles(searchPattern, Options(searchOption))
            .Select(f => new TimestampedFile(LongPathSupport.StripLongPathPrefix(f.FullName), f.LastWriteTimeUtc));
    }

    public void Delete(string path, bool recursive = false)
    {
        Directory.Delete(LongPathSupport.MakeSafeLongFilePath(path), recursive);
    }

    public void Move(string sourceDirName, string destDirName)
    {
        Directory.Move(LongPathSupport.MakeSafeLongFilePath(sourceDirName), LongPathSupport.MakeSafeLongFilePath(destDirName));
    }

    public string GetCurrentDirectory()
    {
        return Directory.GetCurrentDirectory();
    }

    // The options the SearchOption overloads use, except that a pattern ignores case on every OS, as it always has
    // on Windows: a package's "status.SQL" or "Orders.JSON" was read on Windows and silently skipped elsewhere.
    private static EnumerationOptions Options(SearchOption searchOption) => new()
    {
        MatchType = MatchType.Win32,
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = searchOption == SearchOption.AllDirectories,
        AttributesToSkip = 0,
        IgnoreInaccessible = false
    };

    public static IDirectory GetFromFactory()
    {
        return FactoryContainer.ResolveOrCreate<IDirectory, DirectoryWrapper>();
    }
}
