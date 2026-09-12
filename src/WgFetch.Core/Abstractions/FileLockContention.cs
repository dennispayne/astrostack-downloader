namespace WgFetch.Core.Abstractions;

/// <summary>
/// Classifies <see cref="IOException"/>s raised while opening an exclusive lock file. Only genuine
/// sharing/lock violations are retryable; every other I/O failure (invalid path, full device, ...)
/// must surface immediately instead of looping until cancellation.
/// </summary>
internal static class FileLockContention
{
    private const int WindowsSharingViolation = unchecked((int)0x80070020);
    private const int WindowsLockViolation = unchecked((int)0x80070021);

    // On Unix an exclusive open that loses the advisory lock surfaces the native errno as HResult.
    // EAGAIN/EWOULDBLOCK is 11 on Linux and 35 on macOS, and EBUSY is 16 on both.
    private static readonly int[] UnixContentionErrnos = [11, 16, 35];

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

        if (OperatingSystem.IsWindows())
        {
            return exception.HResult is WindowsSharingViolation or WindowsLockViolation;
        }

        return Array.IndexOf(UnixContentionErrnos, exception.HResult) >= 0;
    }
}
