using WgFetch.Core.Prereqs;
using WgFetch.Core.Tests.Support;

namespace WgFetch.Core.Tests.Prereqs;

public sealed class PrereqInstallerTests
{
    [Fact]
    public async Task Install_follows_an_allowlisted_hugging_face_cdn_redirect_before_downloading()
    {
        var asset = PinnedModels.Embedding.Assets[0];
        const string cdnUrl = "https://us.aws.cdn.hf.co/models/model.onnx";
        var http = new StubHttpGateway()
            .Map(asset.Url, StubResponse.Redirect(cdnUrl))
            .Map(cdnUrl, StubResponse.Binary(FakeInstaller.PortableExecutable()));
        using var directory = new TempDirectory();

        var result = await new PrereqInstaller(http).InstallAsync(
            directory.Path,
            includeLanguageModel: false,
            dryRun: false,
            CancellationToken.None);

        Assert.False(result.Success); // The synthetic payload intentionally does not match the compiled-in hash.
        Assert.Equal([asset.Url, cdnUrl], http.Requests.Select(request => request.Url.ToString()));
        Assert.False(File.Exists(directory.Combine(PinnedModels.EmbeddingModelId, asset.RelativePath)));
    }

    [Fact]
    public async Task Install_rejects_a_hugging_face_redirect_outside_the_pinned_download_allowlist()
    {
        var asset = PinnedModels.Embedding.Assets[0];
        var http = new StubHttpGateway()
            .Map(asset.Url, StubResponse.Redirect("https://malicious.example.net/model.onnx"));
        using var directory = new TempDirectory();

        var result = await new PrereqInstaller(http).InstallAsync(
            directory.Path,
            includeLanguageModel: false,
            dryRun: false,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal([asset.Url], http.Requests.Select(request => request.Url.ToString()));
        Assert.False(File.Exists(directory.Combine(PinnedModels.EmbeddingModelId, asset.RelativePath)));
    }
}
