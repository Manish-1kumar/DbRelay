using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DbRelay.Internal;

/// <summary>
/// Generates RFC 9562 version 7 UUIDs (48-bit Unix millisecond timestamp + random bits). Time-ordered IDs keep B-tree inserts
/// local in PostgreSQL. Implemented here because <c>Guid.CreateVersion7</c> is not available on .NET 8.
/// </summary>
internal static class Uuid7
{
    public static Guid NewGuid(DateTimeOffset now)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes[6..]);

        var milliseconds = now.ToUnixTimeMilliseconds();
        Span<byte> timestamp = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(timestamp, milliseconds);
        timestamp[2..].CopyTo(bytes);

        bytes[6] = (byte)(0x70 | (bytes[6] & 0x0F)); // version 7
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F)); // RFC 9562 variant

        return new Guid(bytes, bigEndian: true);
    }
}
