using System.Globalization;
using System.Security.Cryptography;
using WgFetch.Core.Prereqs;
using WgFetch.Core.Tests.Support;
using Xunit;

namespace WgFetch.Core.Tests.Prereqs;

/// <summary>
/// Invariants the compiled-in model pins must hold. These are deliberately hermetic — they assert on
/// the shape of the constants, never on the network (docs/REQUIREMENTS.md, "Prerequisites — frictionless
/// first run" and "Known conflicts" §1).
/// </summary>
public sealed class PinnedModelsTests
{
    public static TheoryData<string> ModelIds() =>
        new(PinnedModels.All.Select(m => m.Id));

    [Fact]
    public void Both_models_are_fully_pinned_so_the_installer_no_longer_fails_closed()
    {
        Assert.All(PinnedModels.All, model => Assert.True(model.FullyPinned, $"{model.Id} has an unpinned asset."));
    }

    [Theory]
    [MemberData(nameof(ModelIds))]
    public void Every_asset_carries_a_lowercase_64_hex_digest_and_a_positive_size(string modelId)
    {
        var model = PinnedModels.Find(modelId);
        Assert.NotNull(model);

        Assert.NotEmpty(model.Assets);
        foreach (var asset in model.Assets)
        {
            Assert.NotNull(asset.Sha256);
            Assert.Equal(64, asset.Sha256.Length);
            Assert.All(asset.Sha256, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c), $"'{c}' is not lowercase hex."));
            Assert.True(asset.SizeBytes > 0, $"{asset.RelativePath} has no expected size.");
        }
    }

    [Theory]
    [MemberData(nameof(ModelIds))]
    public void Revision_is_an_immutable_commit_sha_and_every_url_resolves_against_it(string modelId)
    {
        var model = PinnedModels.Find(modelId);
        Assert.NotNull(model);

        // A digest pinned against a moving ref such as 'main' is not a pin at all.
        Assert.Equal(40, model.Revision.Length);
        Assert.All(model.Revision, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c), $"'{c}' is not lowercase hex."));

        foreach (var asset in model.Assets)
        {
            Assert.Contains($"/resolve/{model.Revision}/", asset.Url, StringComparison.Ordinal);
            Assert.Contains($"/{model.Repository}/", asset.Url, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(ModelIds))]
    public void Every_url_is_absolute_https_to_an_allowlisted_host(string modelId)
    {
        var model = PinnedModels.Find(modelId);
        Assert.NotNull(model);

        foreach (var asset in model.Assets)
        {
            Assert.True(Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri), $"{asset.Url} is not absolute.");
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.Contains(uri.Host, PinnedModels.DownloadHosts, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Download_hosts_cover_the_cdn_hugging_face_redirects_large_blobs_to()
    {
        // huggingface.co only serves the metadata hop; LFS/Xet-backed weights come from a CDN host.
        Assert.Contains("huggingface.co", PinnedModels.DownloadHosts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            PinnedModels.DownloadHosts,
            host => host.EndsWith("hf.co", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("cdn-lfs", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(ModelIds))]
    public void Relative_paths_are_unique_and_stay_inside_the_model_directory(string modelId)
    {
        var model = PinnedModels.Find(modelId);
        Assert.NotNull(model);

        var paths = model.Assets.Select(a => a.RelativePath).ToList();
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wgfetch-pin-probe"));
        foreach (var relativePath in paths)
        {
            Assert.False(Path.IsPathRooted(relativePath), $"{relativePath} is rooted.");
            Assert.StartsWith(
                root + Path.DirectorySeparatorChar,
                Path.GetFullPath(Path.Combine(root, relativePath)),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_embedding_model_avoids_the_variant_that_needs_avx512_vnni()
    {
        // The N5105 reference box has no AVX512-VNNI, so the int8 build would not load there.
        var weights = Assert.Single(PinnedModels.Embedding.Assets, a => a.RelativePath == "model.onnx");

        Assert.DoesNotContain("vnni", weights.Url, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("/onnx/model_O4.onnx", weights.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_is_case_insensitive_and_returns_null_for_unknown_ids()
    {
        Assert.Same(PinnedModels.Embedding, PinnedModels.Find(PinnedModels.EmbeddingModelId.ToUpperInvariant()));
        Assert.Same(PinnedModels.LanguageModel, PinnedModels.Find(PinnedModels.LanguageModelId));
        Assert.Null(PinnedModels.Find("not-a-model"));
    }

    [Fact]
    public void Only_the_language_model_is_flagged_as_such_and_its_message_names_the_opt_in_flag()
    {
        Assert.False(PinnedModels.Embedding.IsLanguageModel);
        Assert.True(PinnedModels.LanguageModel.IsLanguageModel);

        Assert.Contains(
            "--include-llm",
            PrereqInstaller.MissingModelMessage(PinnedModels.LanguageModel),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--include-llm",
            PrereqInstaller.MissingModelMessage(PinnedModels.Embedding),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_missing_model_message_names_every_file_and_its_expected_size()
    {
        var message = PrereqInstaller.MissingModelMessage(PinnedModels.Embedding);

        foreach (var asset in PinnedModels.Embedding.Assets)
        {
            Assert.Contains(asset.RelativePath, message, StringComparison.Ordinal);
            Assert.Contains(
                asset.SizeBytes!.Value.ToString(CultureInfo.InvariantCulture),
                message,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_asset_without_a_digest_is_not_pinned()
    {
        var unpinned = new ModelAsset("model.onnx", "https://huggingface.co/x/y/resolve/abc/model.onnx", 1, null);
        var blank = unpinned with { Sha256 = "   " };

        Assert.False(unpinned.IsPinned);
        Assert.False(blank.IsPinned);
        Assert.False((PinnedModels.Embedding with { Assets = [unpinned] }).FullyPinned);
    }

    [Fact]
    public async Task Selective_dry_run_only_reports_the_requested_pinned_model()
    {
        using var temp = new TempDirectory();
        var installer = new PrereqInstaller(new StubHttpGateway());

        var selected = new HashSet<string>([PinnedModels.EmbeddingModelId], StringComparer.Ordinal);
        var result = await installer.InstallAsync(temp.Path, selected, dryRun: true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(result.Messages, message => message.Contains(PinnedModels.EmbeddingModelId, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Messages, message => message.Contains(PinnedModels.LanguageModelId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Selective_install_ignores_unknown_model_ids()
    {
        using var temp = new TempDirectory();
        var installer = new PrereqInstaller(new StubHttpGateway());

        var result = await installer.InstallAsync(temp.Path, new HashSet<string>(["unknown"]), dryRun: true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public async Task Selective_install_downloads_and_records_only_the_selected_model()
    {
        using var temp = new TempDirectory();
        var selectedBytes = "selected model"u8.ToArray();
        var otherBytes = "other model"u8.ToArray();
        var selected = TestModel("selected", "https://models.example/selected.bin", selectedBytes);
        var unselected = TestModel("unselected", "https://models.example/unselected.bin", otherBytes);
        var http = new StubHttpGateway().Map(selected.Assets[0].Url, StubResponse.Binary(selectedBytes));
        var installer = new PrereqInstaller(http, models: [selected, unselected]);

        var result = await installer.InstallAsync(
            temp.Path,
            new HashSet<string>([selected.Id], StringComparer.Ordinal),
            dryRun: false,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal([selected.Assets[0].Url], http.Requests.Select(request => request.Url.ToString()));
        Assert.True(File.Exists(Path.Combine(temp.Path, selected.Id, selected.Assets[0].RelativePath)));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, unselected.Id)));
        var manifest = await File.ReadAllTextAsync(Path.Combine(temp.Path, "install-manifest.json"), CancellationToken.None);
        Assert.Contains("\"id\": \"selected\"", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("unselected", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selective_install_merges_with_a_previously_installed_model_in_the_manifest()
    {
        using var temp = new TempDirectory();
        var selectedBytes = "selected model"u8.ToArray();
        var otherBytes = "other model"u8.ToArray();
        var selected = TestModel("selected", "https://models.example/selected.bin", selectedBytes);
        var unselected = TestModel("unselected", "https://models.example/unselected.bin", otherBytes);
        var http = new StubHttpGateway()
            .Map(selected.Assets[0].Url, StubResponse.Binary(selectedBytes))
            .Map(unselected.Assets[0].Url, StubResponse.Binary(otherBytes));
        var installer = new PrereqInstaller(http, models: [selected, unselected]);

        var first = await installer.InstallAsync(
            temp.Path, new HashSet<string>([selected.Id], StringComparer.Ordinal), dryRun: false, CancellationToken.None);
        Assert.True(first.Success);

        var second = await installer.InstallAsync(
            temp.Path, new HashSet<string>([unselected.Id], StringComparer.Ordinal), dryRun: false, CancellationToken.None);
        Assert.True(second.Success);

        var manifest = await File.ReadAllTextAsync(Path.Combine(temp.Path, "install-manifest.json"), CancellationToken.None);
        Assert.Contains("\"id\": \"selected\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"id\": \"unselected\"", manifest, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(temp.Path, selected.Id, selected.Assets[0].RelativePath)));
        Assert.True(File.Exists(Path.Combine(temp.Path, unselected.Id, unselected.Assets[0].RelativePath)));
    }

    [Fact]
    public async Task Selective_install_tolerates_a_null_entry_in_an_arbitrary_manifest()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "install-manifest.json"),
            """{"installedUtc":"2024-01-01T00:00:00Z","models":[null]}""",
            CancellationToken.None);

        var bytes = "selected model"u8.ToArray();
        var selected = TestModel("selected", "https://models.example/selected.bin", bytes);
        var http = new StubHttpGateway().Map(selected.Assets[0].Url, StubResponse.Binary(bytes));
        var installer = new PrereqInstaller(http, models: [selected]);

        var result = await installer.InstallAsync(
            temp.Path, new HashSet<string>([selected.Id], StringComparer.Ordinal), dryRun: false, CancellationToken.None);

        Assert.True(result.Success);
        var manifest = await File.ReadAllTextAsync(Path.Combine(temp.Path, "install-manifest.json"), CancellationToken.None);
        Assert.Contains("\"id\": \"selected\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_selective_installs_into_the_same_root_never_drop_each_others_manifest_entry()
    {
        using var temp = new TempDirectory();
        const int modelCount = 8;
        var models = new List<PinnedModel>();
        var http = new StubHttpGateway();
        for (var i = 0; i < modelCount; i++)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"model-{i}");
            var model = TestModel($"model-{i}", $"https://models.example/model-{i}.bin", bytes);
            models.Add(model);
            http.Map(model.Assets[0].Url, StubResponse.Binary(bytes));
        }

        var installer = new PrereqInstaller(http, models: models);

        await Parallel.ForEachAsync(models, async (model, cancellationToken) =>
        {
            var result = await installer.InstallAsync(
                temp.Path, new HashSet<string>([model.Id], StringComparer.Ordinal), dryRun: false, cancellationToken);
            Assert.True(result.Success);
        });

        var manifest = await File.ReadAllTextAsync(Path.Combine(temp.Path, "install-manifest.json"), CancellationToken.None);
        Assert.All(models, model => Assert.Contains($"\"id\": \"{model.Id}\"", manifest, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_a_manifest_lock_releases_its_reference()
    {
        using var temp = new TempDirectory();
        var manifestPath = temp.Combine("install-manifest.json");
        var held = await PrereqInstaller.AcquireManifestLockAsync(manifestPath, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = PrereqInstaller.AcquireManifestLockAsync(manifestPath, cancellation.Token);
        Assert.Equal(2, PrereqInstaller.GetManifestLockReferenceCount(manifestPath));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();

        Assert.False(PrereqInstaller.IsManifestLockTracked(manifestPath));
    }

    private static PinnedModel TestModel(string id, string url, byte[] bytes) =>
        new(id, id, "test/repository", "revision", IsLanguageModel: false,
        [new ModelAsset("model.bin", url, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))]);
}
