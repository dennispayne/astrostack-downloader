using WgFetch.Core.Abstractions;
using WgFetch.Core.Tests.Support;

namespace WgFetch.Core.Tests.Abstractions;

/// <summary>
/// Lock acquisition retries only while another holder owns the lock file; every other I/O failure must
/// surface to the caller instead of looping until cancellation.
/// </summary>
public sealed class FileLockContentionTests
{
    [Fact]
    public async Task A_lost_exclusive_open_is_contention()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("holder.lock");
        await using var holder = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var exception = Assert.Throws<IOException>(() =>
            new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));

        Assert.True(FileLockContention.IsContention(exception));
    }

    [Fact]
    public void A_generic_io_failure_is_not_contention()
    {
        Assert.False(FileLockContention.IsContention(new IOException("disk full")));
    }

    [Fact]
    public void A_derived_io_failure_is_not_contention()
    {
        Assert.False(FileLockContention.IsContention(new DirectoryNotFoundException("missing")));
        Assert.False(FileLockContention.IsContention(new FileNotFoundException("missing")));
        Assert.False(FileLockContention.IsContention(new PathTooLongException("too long")));
    }

    [Fact]
    public void Win32_sharing_and_lock_violations_are_contention()
    {
        Assert.True(FileLockContention.IsContention(new IOException("sharing") { HResult = unchecked((int)0x80070020) }));
        Assert.True(FileLockContention.IsContention(new IOException("lock") { HResult = unchecked((int)0x80070021) }));
        Assert.False(FileLockContention.IsContention(new IOException("disk full") { HResult = unchecked((int)0x80070070) }));
    }

    [Fact]
    public void Unix_lock_errnos_are_contention()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(FileLockContention.IsContention(new IOException("would block") { HResult = 11 }));
        Assert.True(FileLockContention.IsContention(new IOException("busy") { HResult = 16 }));
        Assert.True(FileLockContention.IsContention(new IOException("would block") { HResult = 35 }));
        Assert.False(FileLockContention.IsContention(new IOException("no space") { HResult = 28 }));
    }

    [Fact]
    public void A_missing_directory_open_is_not_contention()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "absent", "holder.lock");

        var exception = Assert.Throws<DirectoryNotFoundException>(() =>
            new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));

        Assert.False(FileLockContention.IsContention(exception));
    }
}
