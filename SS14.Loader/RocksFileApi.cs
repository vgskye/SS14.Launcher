using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Robust.LoaderApi;
using RocksDbSharp;

namespace SS14.Loader;

internal sealed class RocksFileApi : IDisposableFileApi
{
    private readonly Dictionary<string, byte[]> _files = new();
    private readonly RocksDb _db;

    public RocksFileApi(string contentDbPath, byte[] manifestHash)
    {
        var tableOptions = new BlockBasedTableOptions()
            .SetBlockSize(16 * 1024)
            .SetCacheIndexAndFilterBlocks(true)
            .SetPinL0FilterAndIndexBlocksInCache(true)
            .SetFormatVersion(6)
            .SetBlockCache(Cache.CreateLru(128 << 20))
            .SetFilterPolicy(BloomFilterPolicy.Create(10, false));
        var options = new DbOptions()
            .SetCreateIfMissing(true)
            .SetLevelCompactionDynamicLevelBytes(true)
            .SetBytesPerSync(1048576)
            .SetBlockBasedTableFactory(tableOptions)
            .SetCompression(Compression.Zstd);
        _db = RocksDb.OpenReadOnly(options, contentDbPath, false);
        LoadManifest(_db.Get(manifestHash));
    }

    public bool TryOpen(string path, [NotNullWhen(true)] out Stream? stream)
    {
        if (!_files.TryGetValue(path, out var hash))
        {
            stream = null;
            return false;
        }

        var data = _db.Get(hash);
        if (data == null)
        {
            stream = null;
            return false;
        }
        stream = new MemoryStream(data, writable: false);
        return true;
    }

    public IEnumerable<string> AllFiles => _files.Keys;

    public void Dispose()
    {
        _db.Dispose();
    }

    private void LoadManifest(byte[] manifest)
    {
        using var stream = new MemoryStream(manifest);
        using var sr = new StreamReader(stream);

        if (sr.ReadLine() != "Robust Content Manifest 1")
            return;

        while (sr.ReadLine() is { } manifestLine)
        {
            try
            {
                var sep = manifestLine.IndexOf(' ');
                var hash = Convert.FromHexString(manifestLine.AsSpan(0, sep));
                var filename = manifestLine.Substring(sep + 1);

                _files.Add(filename, hash);
            }
            catch (FormatException e) { }
            catch (ArgumentOutOfRangeException e) { }
        }
    }
}
