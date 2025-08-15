-- Describes a single server version currently stored in this database.
CREATE TABLE ContentVersion(
    -- Autoincrement to avoid use-after-free type stuff if somebody clears the DB while the game is running.
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    -- BLAKE2b hash of the FULL manifest for this version.
    Hash BLOB NOT NULL,
    -- Fork & version ID reported by server.
    -- This is exclusively used to improve heuristics for which versions to evict from the download cache.
    -- It should not be trusted for security reasons.
    ForkId TEXT NULL,
    ForkVersion TEXT NULL,
    -- Last time this version was used, so we cull old ones.
    LastUsed DATE NOT NULL,
    -- If this version was downloaded via a non-delta zip file, the SHA256 hash of the zip file.
    -- This is used to provide backwards compatibility to servers
    -- that do not support the infrastructure for delta updates.
    ZipHash BLOB NULL
    -- Used engine version is stored in "ContentEngineDependency" table as 'Robust' module.
);

-- Engine dependencies needed by a specified ContentVersion.
-- This includes both the base engine version (stored as the Robust module).
-- And any extra modules such as Robust.Client.WebView.
-- This does NOT describe the actual on-disk available modules and engines, only the ones which are *necessary*.
-- This is intended to be checked before the game is launched and while updating.
-- (the reason for this is so that updating doesn't have to be considered uninterruptible
-- a *massive* 'transaction' of content + engine + modules).
CREATE TABLE ContentEngineDependency(
    Id INTEGER PRIMARY KEY,
    -- Reference to ContentVersion to see which server version this belongs to.
    VersionId INTEGER NOT NULL REFERENCES ContentVersion(Id) ON DELETE CASCADE,
    -- The name of the module. 'Robust' means this module is actually the base server version.
    ModuleName TEXT NOT NULL,
    -- The version of the module.
    ModuleVersion TEXT NOT NULL
);

-- Cannot have multiple versions of the same module for a single installed version.
CREATE UNIQUE INDEX ContentEngineModuleUniqueIndex ON ContentEngineDependency(VersionId, ModuleName);

-- Represents a content download that was interrupted.
-- Used to reference the content blobs that were successfully downloaded,
-- so that they are not immediately garbage collected.
CREATE TABLE InterruptedDownload(
                                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    -- Date the download was interrupted at.
    -- Used to eventually remove this from the database if unused.
                                    Added DATE NOT NULL
);

-- A single content blob of which the download was interrupted.
CREATE TABLE InterruptedDownloadContent(
                                           Id INTEGER PRIMARY KEY AUTOINCREMENT,
    -- The ID of the interrupted download row.
                                           InterruptedDownloadId INTEGER NOT NULL REFERENCES InterruptedDownload(Id) ON DELETE CASCADE,
    -- The ID of the content blob that was downloaded.
    -- This value is unique: a new download shouldn't be re-downloading a blob if we already have it.
    -- Also, we need the index for GC purposes.
                                           ContentId BLOB NOT NULL UNIQUE
)
