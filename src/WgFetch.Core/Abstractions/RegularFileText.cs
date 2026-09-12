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
    /// path is not a readable regular file, holds more than <paramref name="maxBytes"/>, or runs on an
    /// unsupported platform.
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
        // This buffer exceeds sizeof(struct stat) on supported Unix ABIs; only st_mode is inspected.
        private const int StatBufferBytes = 256;
        private const int FileTypeMask = 0xF000;
        private const int RegularFile = 0x8000;
        // st_mode is a ushort at offset 4 on macOS and a uint at these offsets on Linux x64/arm/x86.
        private const int MacOsModeOffset = 4;
        private const int Linux64ModeOffset = 24;
        private const int LinuxOtherModeOffset = 16;

        internal static bool TryGetIsRegular(SafeFileHandle handle, out bool isRegular)
        {
            isRegular = false;
            var referenced = false;
            try
            {
                handle.DangerousAddRef(ref referenced);
                var stat = new byte[StatBufferBytes];
                var descriptor = (int)handle.DangerousGetHandle();
                if (!TryFstat(descriptor, stat))
                {
                    return false;
                }

                if (!TryGetModeOffset(out var modeOffset))
                {
                    return false;
                }

                var mode = OperatingSystem.IsMacOS()
                    ? BitConverter.ToUInt16(stat, modeOffset)
                    : BitConverter.ToInt32(stat, modeOffset);
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

        private static bool TryFstat(int descriptor, byte[] stat)
        {
            try
            {
                return Fstat(descriptor, stat) == 0;
            }
            catch (EntryPointNotFoundException)
            {
                return TryGetFxstatVersion(out var version) && Fxstat(version, descriptor, stat) == 0;
            }
        }

        private static bool TryGetModeOffset(out int modeOffset)
        {
            if (OperatingSystem.IsMacOS())
            {
                modeOffset = MacOsModeOffset;
                return true;
            }

            modeOffset = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => Linux64ModeOffset,
                Architecture.X86 or Architecture.Arm or Architecture.Arm64 => LinuxOtherModeOffset,
                _ => 0,
            };
            return modeOffset != 0;
        }

        private static bool TryGetFxstatVersion(out int version)
        {
            version = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => 1,
                Architecture.X86 or Architecture.Arm => 3,
                _ => 0,
            };
            return version != 0;
        }

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int Fstat(int fileDescriptor, byte[] stat);

        [DllImport("libc", EntryPoint = "__fxstat", SetLastError = true)]
        private static extern int Fxstat(int version, int fileDescriptor, byte[] stat);
    }

    private static class WindowsReader
    {
        private const uint GenericRead = 0x80000000;
        private const uint ShareReadWriteDelete = 0x00000007;
        private const uint OpenExisting = 3;
        private const uint FileFlagOverlapped = 0x40000000;
        private const uint FileTypeDisk = 1;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeDevice = 0x00000040;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const int Win32ErrorFileNotFound = 2;
        private const int Win32ErrorPathNotFound = 3;
        private const int FileInformationClassAttributeTag = 9;

        internal static async Task<string> ReadAllTextAsync(string path, int maxBytes, CancellationToken cancellationToken)
        {
            using var handle = CreateFile(path, GenericRead, ShareReadWriteDelete, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                throw error switch
                {
                    Win32ErrorFileNotFound => new FileNotFoundException($"Could not find file '{path}'.", path),
                    Win32ErrorPathNotFound => new DirectoryNotFoundException($"Could not find a part of the path '{path}'."),
                    _ => new IOException($"unable to open the file (Win32 error {error})."),
                };
            }

            if (GetFileType(handle) != FileTypeDisk ||
                !GetFileInformationByHandleEx(
                    handle,
                    FileInformationClassAttributeTag,
                    out var attributes,
                    (uint)Marshal.SizeOf<FileAttributeTagInformation>()) ||
                (attributes.FileAttributes & (FileAttributeDirectory | FileAttributeDevice | FileAttributeReparsePoint)) != 0)
            {
                throw new IOException("path is not a regular file.");
            }

            await using var stream = new FileStream(handle, FileAccess.Read, ChunkBytes, isAsync: true);
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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle handle,
            int fileInformationClass,
            out FileAttributeTagInformation fileInformation,
            uint bufferSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInformation
        {
            internal uint FileAttributes;
            internal uint ReparseTag;
        }
    }
}
