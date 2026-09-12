namespace WgFetch.Core.Abstractions;

/// <summary>
/// Classifies <see cref="IOException"/>s raised while opening an exclusive lock file. Only genuine
/// sharing/lock violations are retryable; every other I/O failure (invalid path, full device, ...)
/// must surface immediately instead of looping until cancellation.
/// </summary>
internal static class FileLockContention
{
    // Win32 sharing/lock violations, as surfaced by FileStream on Windows.
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int EBusy = 16;
    private const int EAgainLinux = 11;
    private const int EAgainBsd = 35;

    /// <summary>
    /// Returns <see langword="true"/> when the exception means another holder owns the lock file.
    /// </summary>
    internal static bool IsContention(IOException exception)
    {
        // Derived types (DirectoryNotFoundException, FileNotFoundException, PathTooLongException, ...)
        // are permanent failures and are never contention.
        if (exception.GetType() != typeof(IOException))
        {
            return false;
        }

        // The Win32-shaped codes are accepted everywhere: a runtime that maps a Unix lock errno onto
        // them rather than reporting the raw errno must still be treated as contention.
        if (exception.HResult is SharingViolation or LockViolation)
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        // On Unix an exclusive open that loses the advisory lock surfaces the native errno as HResult.
        // EBUSY is 16 on Linux/BSD/macOS, while EAGAIN/EWOULDBLOCK is 11 on Linux and 35 on BSD/macOS.
        var eAgain = (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) ? EAgainBsd : EAgainLinux;
        return exception.HResult is EBusy || exception.HResult == eAgain;
    }
}
