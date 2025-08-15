using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using RocksDbSharp;
using Serilog;
using SS14.Launcher.Models.Data;
using SS14.Launcher.Utility;

namespace SS14.Launcher.Models.ContentManagement;

public sealed class ContentManager
{
    public void Initialize()
    {
        using var con = GetSqliteConnection();

        // I tried to set this from inside the migrations but didn't work, rip.
        // Anyways: enabling WAL mode here so that downloading new files doesn't lock up if your game is running.
        con.Execute("PRAGMA journal_mode=WAL");

        Log.Debug("Migrating content database...");

        var sw = Stopwatch.StartNew();
        var success = Migrator.Migrate(con, "SS14.Launcher.Models.ContentManagement.Migrations");
        if (!success)
            throw new Exception("Migrations failed!");

        Log.Debug("Did content DB migrations in {MigrationTime}", sw.Elapsed);
    }

    /// <summary>
    /// Clear ALL installed server content and try to truncate the DB.
    /// </summary>
    public void ClearAll()
    {
        Task.Run(() =>
        {
            try
            {
                using var con = GetSqliteConnection();

                using var transact = con.BeginTransaction();
                con.Execute("DELETE FROM InterruptedDownload");
                con.Execute("DELETE FROM ContentVersion");
                con.Execute("DELETE FROM Content");
                transact.Commit();

                con.Execute("VACUUM");
            }
            catch (Exception e)
            {
                Log.Error(e, "Error while truncating content DB!");
            }
        });
    }

    /// <summary>
    /// Open a blob in a manifest version for reading.
    /// </summary>
    /// <returns>null if the file does not exist.</returns>
    public static byte[]? OpenBlob(byte[] manifestHash, string fileName)
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
        using var con = RocksDb.OpenReadOnly(options, LauncherPaths.PathContentDataDb, false);
        var manifest = Updater.ParseContentManifest(con.Get(manifestHash));
        var blob = con.Get(manifest?.Find(entry => entry.Path == fileName).Hash);
        return blob;
    }

    /// <summary>
    /// Open a blob in the RocksDB database by hash.
    /// </summary>
    /// <returns>null if the file does not exist.</returns>
    public static byte[]? OpenBlob(byte[] hash)
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
        using var con = RocksDb.OpenReadOnly(options, LauncherPaths.PathContentDataDb, false);
        return con.Get(hash);
    }

    public static SqliteConnection GetSqliteConnection()
    {
        var con = new SqliteConnection(GetContentDbConnectionString());
        con.Open();
        return con;
    }

    public static RocksDb GetRocksDbConnection()
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
        return RocksDb.Open(options, LauncherPaths.PathContentDataDb);
    }

    private static string GetContentDbConnectionString()
    {
        // Disable pooling: interactions with the content DB are relatively infrequent
        // This means that ALL connections get closed in most cases (between committing download and starting client)
        // Which in turn means that the WAL file gets truncated.
        // The WAL file can get quite large in some cases (100+ MB),
        // especially as some codebases keep growing,
        // so not keeping it around for any longer than necessary is good in my book.
        //
        // (also it means that hitting the "clear server content" button in settings IMMEDIATELY truncates the DB file
        // instead of waiting for the launcher to exit, at least if the client isn't running so it can checkpoint)
        return $"Data Source={LauncherPaths.PathContentMetaDb};Mode=ReadWriteCreate;Pooling=False;Foreign Keys=True";
    }
}
