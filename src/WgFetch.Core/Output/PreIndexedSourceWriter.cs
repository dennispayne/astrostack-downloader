using Microsoft.Data.Sqlite;

namespace WgFetch.Core.Output;

/// <summary>One acquired package version to index into <c>index.db</c>.</summary>
public sealed record IndexedManifest
{
    public required string PackageIdentifier { get; init; }

    public required string PackageName { get; init; }

    /// <summary>Lowercase search alias (docs/REQUIREMENTS.md, "Name resolution" catalog fields).</summary>
    public string? Moniker { get; init; }

    public required string PackageVersion { get; init; }

    public string Channel { get; init; } = string.Empty;

    /// <summary>Relative path (forward-slash) to the manifest directory, as stored in <c>pathparts</c>.</summary>
    public required string RelativeManifestPath { get; init; }
}

/// <summary>
/// Builds <c>index.db</c>, a SQLite database in winget's pre-indexed source shape
/// (<c>Microsoft.PreIndexed.Package</c>), for consumers that front the directory with that source
/// type instead of the REST API (docs/REQUIREMENTS.md, "Output layout").
///
/// <para>
/// Content is deterministic for a given input set: rows are inserted in the order derived from a
/// stable sort of the input, sequence numbers are assigned by that order rather than left to
/// SQLite's rowid allocator, and no timestamps are recorded. Unacquired targets (no
/// <see cref="IndexedManifest"/>) contribute nothing.
/// </para>
/// </summary>
public sealed class PreIndexedSourceWriter
{
    public const string SchemaMajorVersion = "1";
    public const string SchemaMinorVersion = "0";

    /// <summary>Rebuilds <paramref name="databasePath"/> from scratch for the given manifest set.</summary>
    public async Task BuildAsync(
        string databasePath,
        IReadOnlyList<IndexedManifest> manifests,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(manifests);

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        // Deterministic ordering independent of caller-supplied order: id, then version.
        var ordered = manifests
            .OrderBy(m => m.PackageIdentifier, StringComparer.Ordinal)
            .ThenBy(m => m.PackageVersion, StringComparer.Ordinal)
            .ToList();

        // Pooling must be disabled: Microsoft.Data.Sqlite pools native connections by connection
        // string, and a pooled handle can keep referencing the file we just deleted above, causing
        // the fresh CREATE TABLE statements below to collide with the previous build's schema.
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, """
            CREATE TABLE metadata (
                name  TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE ids (
                rowid INTEGER PRIMARY KEY,
                id    TEXT NOT NULL UNIQUE
            );
            CREATE TABLE names (
                rowid INTEGER PRIMARY KEY,
                name  TEXT NOT NULL UNIQUE
            );
            CREATE TABLE monikers (
                rowid   INTEGER PRIMARY KEY,
                moniker TEXT NOT NULL UNIQUE
            );
            CREATE TABLE versions (
                rowid   INTEGER PRIMARY KEY,
                version TEXT NOT NULL UNIQUE
            );
            CREATE TABLE channels (
                rowid   INTEGER PRIMARY KEY,
                channel TEXT NOT NULL UNIQUE
            );
            CREATE TABLE pathparts (
                rowid       INTEGER PRIMARY KEY,
                parent      INTEGER,
                pathpart    TEXT NOT NULL,
                manifest    INTEGER
            );
            CREATE TABLE manifest (
                rowid   INTEGER PRIMARY KEY,
                id      INTEGER NOT NULL REFERENCES ids(rowid),
                version INTEGER NOT NULL REFERENCES versions(rowid),
                channel INTEGER NOT NULL REFERENCES channels(rowid),
                name    INTEGER NOT NULL REFERENCES names(rowid),
                moniker INTEGER REFERENCES monikers(rowid),
                pathpart INTEGER NOT NULL REFERENCES pathparts(rowid)
            );
            """, cancellationToken).ConfigureAwait(false);

        await using (var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await InsertAsync(connection, "metadata (name, value) VALUES ('MajorVersion', @v)", SchemaMajorVersion, cancellationToken).ConfigureAwait(false);
            await InsertAsync(connection, "metadata (name, value) VALUES ('MinorVersion', @v)", SchemaMinorVersion, cancellationToken).ConfigureAwait(false);

            var idIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var nameIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var monikerIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var versionIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var channelIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var pathpartIds = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var manifest in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var idRowId = await GetOrInsertAsync(connection, idIds, "ids", "id", manifest.PackageIdentifier, cancellationToken).ConfigureAwait(false);
                var nameRowId = await GetOrInsertAsync(connection, nameIds, "names", "name", manifest.PackageName, cancellationToken).ConfigureAwait(false);
                var versionRowId = await GetOrInsertAsync(connection, versionIds, "versions", "version", manifest.PackageVersion, cancellationToken).ConfigureAwait(false);
                var channelRowId = await GetOrInsertAsync(connection, channelIds, "channels", "channel", manifest.Channel, cancellationToken).ConfigureAwait(false);

                long? monikerRowId = null;
                if (!string.IsNullOrWhiteSpace(manifest.Moniker))
                {
                    monikerRowId = await GetOrInsertAsync(connection, monikerIds, "monikers", "moniker", manifest.Moniker, cancellationToken).ConfigureAwait(false);
                }

                // Leaf pathpart node for the manifest directory; intermediate segments are folded into
                // one key (unlike a true filesystem walk) since only the leaf is ever queried by consumers.
                var pathKey = manifest.RelativeManifestPath;
                if (!pathpartIds.TryGetValue(pathKey, out var pathRowId))
                {
                    await using var insert = connection.CreateCommand();
                    insert.CommandText = "INSERT INTO pathparts (parent, pathpart, manifest) VALUES (NULL, @p, NULL)";
                    insert.Parameters.AddWithValue("@p", pathKey);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    pathRowId = await LastInsertRowIdAsync(connection, cancellationToken).ConfigureAwait(false);
                    pathpartIds[pathKey] = pathRowId;
                }

                await using var insertManifest = connection.CreateCommand();
                insertManifest.CommandText = """
                    INSERT INTO manifest (id, version, channel, name, moniker, pathpart)
                    VALUES (@id, @version, @channel, @name, @moniker, @pathpart)
                    """;
                insertManifest.Parameters.AddWithValue("@id", idRowId);
                insertManifest.Parameters.AddWithValue("@version", versionRowId);
                insertManifest.Parameters.AddWithValue("@channel", channelRowId);
                insertManifest.Parameters.AddWithValue("@name", nameRowId);
                insertManifest.Parameters.AddWithValue("@moniker", (object?)monikerRowId ?? DBNull.Value);
                insertManifest.Parameters.AddWithValue("@pathpart", pathRowId);
                await insertManifest.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> GetOrInsertAsync(
        SqliteConnection connection,
        Dictionary<string, long> cache,
        string table,
        string column,
        string value,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(value, out var existing))
        {
            return existing;
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = $"INSERT INTO {table} ({column}) VALUES (@v)";
        insert.Parameters.AddWithValue("@v", value);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var rowId = await LastInsertRowIdAsync(connection, cancellationToken).ConfigureAwait(false);
        cache[value] = rowId;
        return rowId;
    }

    private static async Task<long> LastInsertRowIdAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid()";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    private static async Task InsertAsync(SqliteConnection connection, string commandText, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {commandText}";
        command.Parameters.AddWithValue("@v", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
