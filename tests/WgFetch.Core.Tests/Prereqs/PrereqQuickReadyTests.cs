using WgFetch.Core.Prereqs;
using WgFetch.Core.Tests.Support;
using Xunit;

namespace WgFetch.Core.Tests.Prereqs;

/// <summary>
/// The hashing-free readiness probe used by the cosmetic no-command landing view. It must answer from
/// file metadata alone — a bare <c>wgfetch</c> may never hash the pinned model assets — while still
/// refusing to call a truncated or missing install "installed".
/// </summary>
public sealed class PrereqQuickReadyTests
{
    private static readonly PinnedModel Model = new(
        "test-model",
        "Test model",
        "example/test-model",
        "0123456789abcdef0123456789abcdef01234567",
        IsLanguageModel: false,
        [new ModelAsset("weights.bin", "https://example.invalid/weights.bin", 4, new string('a', 64))]);

    [Fact]
    public void MissingAsset_IsNotReady()
    {
        using var temp = new TempDirectory();

        Assert.False(PrereqInstaller.QuickReady(temp.Path, [Model]));
    }

    [Fact]
    public void PresentAssetOfPinnedSize_IsReady()
    {
        using var temp = new TempDirectory();
        WriteAsset(temp.Path, new byte[4]);

        Assert.True(PrereqInstaller.QuickReady(temp.Path, [Model]));
    }

    [Fact]
    public void TruncatedAsset_IsNotReady()
    {
        using var temp = new TempDirectory();
        WriteAsset(temp.Path, new byte[3]);

        Assert.False(PrereqInstaller.QuickReady(temp.Path, [Model]));
    }

    [Fact]
    public void UnpinnedAsset_IsNotReady()
    {
        using var temp = new TempDirectory();
        var unpinned = Model with
        {
            Assets = [new ModelAsset("weights.bin", "https://example.invalid/weights.bin", 4, null)],
        };
        WriteAsset(temp.Path, new byte[4]);

        Assert.False(PrereqInstaller.QuickReady(temp.Path, [unpinned]));
    }

    [Fact]
    public void EmptyModelList_IsNotReady()
    {
        using var temp = new TempDirectory();

        Assert.False(PrereqInstaller.QuickReady(temp.Path, []));
    }

    private static void WriteAsset(string modelsRoot, byte[] content)
    {
        var directory = PrereqInstaller.ModelDirectory(modelsRoot, Model);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "weights.bin"), content);
    }
}
