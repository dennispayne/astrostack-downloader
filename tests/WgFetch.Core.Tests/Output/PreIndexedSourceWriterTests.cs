using Microsoft.Data.Sqlite;
using WgFetch.Core.Output;

namespace WgFetch.Core.Tests.Output;

public sealed class PreIndexedSourceWriterTests : IDisposable
{
    private readonly string _dbPath;

    public PreIndexedSourceWriterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "wgfetch-tests", Path.GetRandomFileName(), "index.db");
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_CreatesStandardNormalizedTables()
    {
        var writer = new PreIndexedSourceWriter();
        await writer.BuildAsync(_dbPath, [], CancellationToken.None);

        Assert.True(File.Exists(_dbPath));

        await using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await connection.OpenAsync();

        var expectedTables = new[] { "ids", "names", "monikers", "versions", "channels", "pathparts", "manifest", "metadata" };
        foreach (var table in expectedTables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
            command.Parameters.AddWithValue("@name", table);
            var count = Convert.ToInt64(await command.ExecuteScalarAsync());
            Assert.True(count == 1, $"expected table '{table}' to exist");
        }
    }

    [Fact]
    public async Task BuildAsync_MetadataRecordsSchemaVersion()
    {
        var writer = new PreIndexedSourceWriter();
        await writer.BuildAsync(_dbPath, [], CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE name = 'MajorVersion'";
        var value = (string?)await command.ExecuteScalarAsync();
        Assert.Equal(PreIndexedSourceWriter.SchemaMajorVersion, value);
    }

    [Fact]
    public async Task BuildAsync_OneRowPerAcquiredVersion()
    {
        var manifests = new[]
        {
            new IndexedManifest
            {
                PackageIdentifier = "AstroStack.NINA",
                PackageName = "N.I.N.A.",
                Moniker = "nina",
                PackageVersion = "3.2.0.9001",
                RelativeManifestPath = "manifests/a/AstroStack/NINA/3.2.0.9001",
            },
            new IndexedManifest
            {
                PackageIdentifier = "AstroStack.PHD2",
                PackageName = "PHD2",
                Moniker = "phd2",
                PackageVersion = "2.6.13",
                RelativeManifestPath = "manifests/a/AstroStack/PHD2/2.6.13",
            },
        };

        var writer = new PreIndexedSourceWriter();
        await writer.BuildAsync(_dbPath, manifests, CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM manifest";
        var count = Convert.ToInt64(await command.ExecuteScalarAsync());
        Assert.Equal(2, count);

        await using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT COUNT(*) FROM ids";
        Assert.Equal(2, Convert.ToInt64(await idCommand.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task BuildAsync_UnacquiredTargetsContributeNothing()
    {
        // An empty manifest set represents a repo where every target is listed/resolved but not
        // acquired (docs/REQUIREMENTS.md: "manifests/, index.db and rest/ contain acquired artifacts only").
        var writer = new PreIndexedSourceWriter();
        await writer.BuildAsync(_dbPath, [], CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await connection.OpenAsync();

        foreach (var table in new[] { "ids", "names", "monikers", "versions", "manifest" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            var count = Convert.ToInt64(await command.ExecuteScalarAsync());
            Assert.Equal(0, count);
        }
    }

    [Fact]
    public async Task BuildAsync_IsDeterministicForTheSameInputSet()
    {
        var manifests = new[]
        {
            new IndexedManifest
            {
                PackageIdentifier = "AstroStack.NINA",
                PackageName = "N.I.N.A.",
                PackageVersion = "3.2.0.9001",
                RelativeManifestPath = "manifests/a/AstroStack/NINA/3.2.0.9001",
            },
        };

        var writer = new PreIndexedSourceWriter();
        await writer.BuildAsync(_dbPath, manifests, CancellationToken.None);
        var first = await File.ReadAllBytesAsync(_dbPath);

        await writer.BuildAsync(_dbPath, manifests, CancellationToken.None);
        var second = await File.ReadAllBytesAsync(_dbPath);

        Assert.Equal(first, second);
    }
}
