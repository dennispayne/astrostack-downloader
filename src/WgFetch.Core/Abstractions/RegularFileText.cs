using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WgFetch.Core.Abstractions;

/// <summary>
/// Reads a small text file that the user controls but the tool does not trust, without ever blocking
/// on a special file and without letting an endless one (such as <c>/dev/zero</c>) grow in memory.
/// On Unix the path is opened once with <c>O_NONBLOCK</c> and every later decision is made about
/// that one open description, so a path swapped between the check and the read cannot smuggle a FIFO
/// past the check; on Windows the equivalent handle is inspected before reading. Anything that cannot
/// be shown to be a regular file is rejected rather than read.
/// </summary>
internal static class RegularFileText
{
    private const int ChunkBytes = 64 * 1024;

    /// <summary>
    /// Reads <paramref name="path"/> as text, failing closed with an <see cref="IOException"/> when the
    /// path is not a readable regular file or holds more than <paramref name="maxBytes"/>.
    /// </summary>
    internal static Task<string> ReadAllTextAsync(string path, int maxBytes, CancellationToken cancellationToken) =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
            ? UnixReader.ReadAllTextAsync(path, maxBytes, cancellationToken)
            : OperatingSystem.IsWindows()
                ? WindowsReader.ReadAllTextAsync(path, maxBytes, cancellationToken)
                : Task.FromException<string>(new IOException("unable to verify that the path is a regular file on this platform."));

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> from a handle that has already been proven regular.
    /// </summary>
    private static async Task<string> ReadBoundedAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        var chunk = new byte[ChunkBytes];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (content.Length + read > maxBytes)
            {
                throw new IOException($"file is larger than the {maxBytes} byte limit.");
            }

            content.Write(chunk, 0, read);
        }

        content.Position = 0;
        using var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static class UnixReader
    {
        private const int ReadOnly = 0x0000;
        private static readonly int NonBlocking = OperatingSystem.IsMacOS() ? 0x0004 : 0x0800;
        private static readonly int CloseOnExec = OperatingSystem.IsMacOS() ? 0x01000000 : 0x80000;

        private const int NoSuchFileOrDirectory = 2;
        private const int NotADirectory = 20;
        private const int PermissionDenied = 13;
        private const int IsADirectory = 21;

        internal static async Task<string> ReadAllTextAsync(string path, int maxBytes, CancellationToken cancellationToken)
        {
            SafeFileHandle handle;
            try
            {
                handle = Open(path);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Without a usable libc entry point there is no way to open this path without risking a
                // block on a FIFO, and the managed reader offers no protection, so fail closed instead.
                throw new IOException("unable to open the file without blocking on this platform.", ex);
            }

            FileStream stream;
            try
            {
                stream = new FileStream(handle, FileAccess.Read);
            }
            catch
            {
                handle.Dispose();
                throw;
            }

            // From here the stream owns the descriptor and closes it on dispose.
            await using (stream.ConfigureAwait(false))
            {
                EnsureRegularFile(handle);
                return await ReadBoundedAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
            }
        }

        private static SafeFileHandle Open(string path)
        {
            var utf8Path = new byte[Encoding.UTF8.GetByteCount(path) + 1];
            Encoding.UTF8.GetBytes(path, utf8Path);

            var descriptor = OpenNative(utf8Path, ReadOnly | NonBlocking | CloseOnExec);
            if (descriptor >= 0)
            {
                return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            }

            var error = Marshal.GetLastPInvokeError();
            throw error switch
            {
                NoSuchFileOrDirectory or NotADirectory => new FileNotFoundException(null, path),
                PermissionDenied => new UnauthorizedAccessException($"Access to '{path}' is denied."),
                IsADirectory => new IOException("path is not a regular file."),
                _ => new IOException($"unable to open the file (errno {error})."),
            };
        }

        /// <summary>
        /// Rejects anything not shown to be a regular file. <c>fstat</c> inspects the opened descriptor,
        /// so a rename after <see cref="Open"/> cannot invalidate the result.
        /// </summary>
        private static void EnsureRegularFile(SafeFileHandle handle)
        {
            if (!UnixFileType.TryGetIsRegular(handle, out var isRegular) || !isRegular)
            {
                throw new IOException("path is not a regular file.");
            }
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int OpenNative(byte[] path, int flags);
    }

    /// <summary>
    /// Answers "is this open file description a regular file?" using <c>fstat</c> against the handle
    /// itself, never the path, so the answer cannot be invalidated by a rename.
    /// </summary>
    private static class UnixFileType
    {
        private const ushort FileTypeMask = 0xF000;
        private const ushort RegularFile = 0x8000;

        internal static bool TryGetIsRegular(SafeFileHandle handle, out bool isRegular)
        {
            isRegular = false;
            var referenced = false;
            try
            {
                handle.DangerousAddRef(ref referenced);
                var stat = new byte[256];
                if (Fstat((int)handle.DangerousGetHandle(), stat) != 0)
                {
                    return false;
                }

                var modeOffset = OperatingSystem.IsMacOS() ? 4 : 24;
                var mode = BitConverter.ToUInt16(stat, modeOffset);
                isRegular = (mode & FileTypeMask) == RegularFile;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
            finally
            {
                if (referenced)
                {
                    handle.DangerousRelease();
                }
            }
        }

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int Fstat(int fileDescriptor, byte[] stat);
    }

    private static class WindowsReader
    {
        private const uint GenericRead = 0x80000000;
        private const uint ShareReadWriteDelete = 0x00000007;
        private const uint OpenExisting = 3;
        private const uint FileFlagOverlapped = 0x40000000;
        private const uint FileTypeDisk = 1;

        internal static async Task<string> ReadAllTextAsync(string path, int maxBytes, CancellationToken cancellationToken)
        {
            using var handle = CreateFile(path, GenericRead, ShareReadWriteDelete, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new IOException($"unable to open the file (Win32 error {Marshal.GetLastPInvokeError()}).");
            }

            if (GetFileType(handle) != FileTypeDisk)
            {
                throw new IOException("path is not a regular file.");
            }

            await using var stream = new FileStream(handle, FileAccess.Read);
            return await ReadBoundedAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string path,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(SafeFileHandle handle);
    }
}
