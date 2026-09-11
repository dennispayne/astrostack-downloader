using System.Globalization;
using WgFetch.Core.Prereqs;
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
}
