// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Schema.Utility;

namespace Schema.Isolators;

public class ZipFileWrapper : IFile, IDisposable
{
    public List<IZipEntry> ZipEntries { get; private set; }

    private string _zipFilePath = "";
    private ZipArchive _archive;
    private readonly object _lockObject = new();

    public bool Exists(string path)
    {
        if (string.IsNullOrEmpty(NormalizePath(path)))
            return false;

        return ZipEntries!.Any(e => NormalizePath(e.FullName).EqualsIgnoringCase(NormalizePath(path)));
    }

    public string ReadAllText(string path)
    {
        lock (_lockObject)
        {
            using var stream = OpenEntry(path);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public byte[] ReadAllBytes(string path)
    {
        lock (_lockObject)
        {
            using var stream = OpenEntry(path);
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        }
    }

    public static bool IsValidZipFile(string filename)
    {
        var file = FileWrapper.GetFromFactory();
        if (string.IsNullOrEmpty(filename) || !file.Exists(filename))
            return false;

        try
        {
            using var fileStream = file.OpenRead(filename);
            if (fileStream.Length == 0) return false;
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false);
            _ = archive.Entries;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // The factory hands out one wrapper and asks it again for each file a load reads: the archive already open is kept, and
    // one it replaces is closed, so the zip is never left locked behind the run.
    private void Initialize(string zipFileName)
    {
        lock (_lockObject)
        {
            if (_archive != null && _zipFilePath == zipFileName) return;
            _archive?.Dispose();
            _zipFilePath = zipFileName;
            _archive = ZipFile.OpenRead(zipFileName);
            ZipEntries = _archive.Entries.Select(e => (IZipEntry)new ZipEntryWrapper(e)).ToList();
        }
    }

    private static string NormalizePath(string path)
    {
        return path?.Replace('\\', '/').Trim('/');
    }

    private Stream OpenEntry(string path)
    {
        if (string.IsNullOrEmpty(NormalizePath(path)))
            throw new FileNotFoundException($"Invalid entry requested: '{path}'");

        var entry = ZipEntries!.FirstOrDefault(e => NormalizePath(e.FullName).EqualsIgnoringCase(NormalizePath(path)));
        if (entry == null)
            throw new FileNotFoundException($"Entry '{path}' not found in zip file '{_zipFilePath}'.");

        return entry.Open();
    }

    public static IFile GetFromFactory(string zipFileName)
    {
        var zipFile = FactoryContainer.ResolveOrCreate<ZipFileWrapper>(true);
        zipFile.Initialize(zipFileName);
        return zipFile;
    }

    internal void SetZipEntries(List<IZipEntry> entries)
    {
        ZipEntries = entries;
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            ZipEntries = null;
            _archive?.Dispose();
            _archive = null;
        }
    }

    // Other IFile methods not used for zip access
    public Stream OpenRead(string path) => throw new NotImplementedException();
    public void WriteAllText(string path, string contents) => throw new NotImplementedException();
    public void Copy(string source, string destination, bool overwrite = false) => throw new NotImplementedException();
    public void Move(string sourceFileName, string destFileName) => throw new NotImplementedException();
    public void Delete(string path) => throw new NotImplementedException();
    public string[] ReadAllLines(string path) => throw new NotImplementedException();
    public DateTime GetLastWriteTimeUtc(string path) => throw new NotImplementedException();
}
