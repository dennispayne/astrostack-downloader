using System.Text;

namespace WgFetch.Core.Abstractions;

internal static class SafeUserFile
{
    internal static void ThrowIfPathContainsNul(string path)
    {
        if (path.Contains('\0'))
        {
            throw new IOException("path contains an embedded NUL character.");
        }
    }

    internal static int GetUtf8ByteCountWithinLimit(string text, int maxBytes, string description)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        ThrowIfTooLarge(byteCount, maxBytes, description);
        return byteCount;
    }

    internal static MemoryStream CreateBoundedBuffer(int maxBytes, string description) =>
        new SizeLimitedMemoryStream(maxBytes, description);

    private static void ThrowIfTooLarge(long byteCount, int maxBytes, string description)
    {
        if (byteCount > maxBytes)
        {
            throw new IOException($"{description} is larger than the {maxBytes} byte limit.");
        }
    }

    private sealed class SizeLimitedMemoryStream(int maxBytes, string description) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCanWrite(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCanWrite(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCanWrite(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            EnsureCanWrite(1);
            base.WriteByte(value);
        }

        private void EnsureCanWrite(int count) =>
            ThrowIfTooLarge(Position + count, maxBytes, description);
    }
}
