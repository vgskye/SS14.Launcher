using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using RocksDbSharp;
using Serilog;
using SpaceWizards.Sodium;
using SS14.Launcher.Models.ContentManagement;
using SS14.Launcher.Utility;

namespace SS14.Launcher.Models;

//
// Logic for updater zip downloads.
// Mostly legacy now, but keeping support is good.
//

public sealed partial class Updater
{
    [SuppressMessage("ReSharper", "MethodHasAsyncOverload")]
    [SuppressMessage("ReSharper", "MethodHasAsyncOverloadWithCancellation")]
    [SuppressMessage("ReSharper", "UseAwaitUsing")]
    private async Task<byte[]> ZipDownloadNewVersion(
        ServerBuildInformation buildInfo,
        SqliteConnection con,
        long versionId,
        CancellationToken cancel)
    {
        // Temp file to download zip into.
        await using var tempFile = TempFile.CreateTempFile();

        var zipHash = await ZipUpdateDownloadContent(tempFile, buildInfo, cancel);

        con.Execute("UPDATE ContentVersion SET ZipHash=@ZipHash WHERE Id=@Version",
            new { ZipHash = zipHash, Version = versionId });

        Status = UpdateStatus.LoadingIntoDb;

        tempFile.Seek(0, SeekOrigin.Begin);

        // File downloaded, time to dump this into the DB.

        var zip = new ZipArchive(tempFile, ZipArchiveMode.Read, leaveOpen: true);

        return ZipIngest(zip, null, cancel);
    }

    /// <summary>
    /// Download content zip to the specified file and verify hash.
    /// </summary>
    /// <returns>
    /// File hash in case the server didn't provide one.
    /// </returns>
    private async Task<byte[]> ZipUpdateDownloadContent(
        Stream file,
        ServerBuildInformation buildInformation,
        CancellationToken cancel)
    {
        Status = UpdateStatus.DownloadingClientUpdate;

        Log.Information("Downloading content update from {ContentDownloadUrl}", buildInformation.DownloadUrl);

        await _http.DownloadToStream(
            buildInformation.DownloadUrl!,
            file,
            DownloadProgressCallback,
            cancel);

        file.Position = 0;

        Progress = null;

        Status = UpdateStatus.Verifying;

        var hash = await Task.Run(() => HashFileSha256(file), cancel);
        file.Position = 0;

        var newFileHashString = Convert.ToHexString(hash);
        if (buildInformation.Hash is { } expectHash)
        {
            if (!expectHash.Equals(newFileHashString, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Hash mismatch. Expected: {expectHash}, got: {newFileHashString}");
            }
        }

        Log.Verbose("Done downloading zip. Hash: {DownloadHash}", newFileHashString);

        return hash;
    }

    private byte[] ZipIngest(
        ZipArchive zip,
        FetchedContentManifestData? underlay,
        CancellationToken cancel)
    {
        using var con = ContentManager.GetRocksDbConnection();
        var totalSize = 0L;
        var sw = new Stopwatch();

        var newFileCount = 0;

        var underlayEntries = underlay?.Entries.Select(entry => entry.Path).ToHashSet();

        // Re-use compression buffer and compressor for all files, creating/freeing them is expensive.
        var readBuffer = new MemoryStream();

        var entries = new List<ContentManifestEntry>();

        if (underlay != null)
        {
            entries.AddRange(underlay.Entries);
        }

        var count = 0;
        foreach (var entry in zip.Entries)
        {
            cancel.ThrowIfCancellationRequested();

            if (count++ % 100 == 0)
                Progress = (count++, zip.Entries.Count, ProgressUnit.None);

            // Ignore directory entries.
            if (entry.Name == "")
                continue;

            if (underlayEntries != null)
            {
                if (underlayEntries.Contains(entry.FullName))
                    continue;
            }

            // Log.Verbose("Storing file {EntryName}", entry.FullName);

            byte[] hash;
            using (var stream = entry.Open())
            {
                hash = Blake2B.HashStream(stream, 32);
            }

            if (!con.HasKey(hash))
            {
                newFileCount += 1;

                // Don't have this content blob yet, insert it into the database.
                using var entryStream = entry.Open();

                entryStream.CopyTo(readBuffer);

                con.Put(hash, readBuffer.GetBuffer().AsSpan(0, (int)readBuffer.Length));

                readBuffer.Position = 0;
                readBuffer.SetLength(0);
            }

            entries.Add(new ContentManifestEntry
            {
                Hash = hash, Path = entry.FullName
            });
        }

        var manifest = GenerateContentManifest(entries);
        var manifestHash = new byte[256 / 8];
        CryptoGenericHashBlake2B.Hash(manifestHash, manifest, ReadOnlySpan<byte>.Empty);

        con.Put(manifestHash, manifest);

        Log.Debug("Compression report: {ElapsedMs} ms elapsed, {TotalSize} B total size", sw.ElapsedMilliseconds,
            totalSize);
        Log.Debug("New files: {NewFilesCount}", newFileCount);
        
        con.Flush(new FlushOptions().SetWaitForFlush(true));

        return manifestHash;
    }
}
