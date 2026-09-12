using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WgFetch.Core.Abstractions;

/// <summary>
/// Reads a small text file that the user controls but the tool does not trust, without ever blocking
/// on a special file and without letting an endless one (such as <c>/dev/zero</c>) grow in memory.
/// On Linux the path is opened once with <c>O_NONBLOCK</c> and every later decision is made about
/// that one open description, so a path swapped between the check and the read cannot smuggle a FIFO
/// past the check; anything that cannot be shown to be a regular file is rejected rather than read.
/// Other platforms have no comparable special-file hazard for these paths and use the managed reader.
/// </summary>
internal static class RegularFileText
{
    private const int ChunkBytes = 64 * 1024;

    /// <summary>
    /// Reads <paramref name="path"/> as text, failing closed with an <see cref="IOException"/> when the
    /// path is not a readable regular file or holds more than <paramref name="maxBytes"/>.
    /// </summary>
    internal static Task<string> ReadAllTextAsync(string path, int maxBytes, CancellationToken cancellationToken) =>
        OperatingSystem.IsLinux()
            ? LinuxReader.ReadAllTextAsync(path, maxBytes, cancellationToken)
            : File.ReadAllTextAsync(path, cancellationToken);

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/>. A file type this build could not identify must still
    /// not be able to feed an endless stream into memory, so the cap — not the type check — is what
    /// bounds the read.
    /// </summary>
    private static async Task<string> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        var chunk = new byte[ChunkBytes];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (content.Length + read > maxBytes)
            {
                throw new IOException($"file is larger than the {maxBytes / (1024 * 1024)} MiB limit.");
            }

            content.Write(chunk, 0, read);
        }

        content.Position = 0;
        using var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static class LinuxReader
    {
        private const int ReadOnly = 0x0000;
        private const int NonBlocking = 0x0800;
        private const int CloseOnExec = 0x80000;

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
                EnsureRegularFile(handle, stream);
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
        /// Rejects anything not shown to be a regular file. <c>statx</c> answers directly; when it is
        /// missing or refused, seekability stands in for it — a FIFO or socket is never seekable, and an
        /// unseekable handle is exactly the case that could otherwise report an empty document or block.
        /// </summary>
        private static void EnsureRegularFile(SafeFileHandle handle, FileStream stream)
        {
            var regular = LinuxFileType.TryGetIsRegular(handle, out var isRegular)
                ? isRegular
                : stream.CanSeek;

            if (!regular)
            {
                throw new IOException("path is not a regular file.");
            }
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int OpenNative(byte[] path, int flags);
    }

    /// <summary>
    /// Answers "is this open file description a regular file?" using <c>statx</c> against the handle
    /// itself (<c>AT_EMPTY_PATH</c>), never the path, so the answer cannot be invalidated by a rename.
    /// Kernels and libc versions without <c>statx</c> report "unknown" instead of failing: callers must
    /// stay safe without an answer.
    /// </summary>
    private static class LinuxFileType
    {
        private const int AtEmptyPath = 0x1000;
        private const uint FileTypeMaskRequest = 1;
        private const ushort FileTypeMask = 0xF000;
        private const ushort RegularFile = 0x8000;

        private static readonly byte[] EmptyPath = [0];

        internal static bool TryGetIsRegular(SafeFileHandle handle, out bool isRegular)
        {
            isRegular = false;
            var referenced = false;
            try
            {
                handle.DangerousAddRef(ref referenced);
                if (Statx((int)handle.DangerousGetHandle(), EmptyPath, AtEmptyPath, FileTypeMaskRequest, out var stat) != 0)
                {
                    return false;
                }

                isRegular = (stat.Mode & FileTypeMask) == RegularFile;
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

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        private static extern int Statx(int directoryFileDescriptor, byte[] path, int flags, uint mask, out LinuxStatx stat);

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct LinuxStatx
        {
            [FieldOffset(28)]
            internal ushort Mode;
        }
    }
}
