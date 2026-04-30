using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace LambdaRag.Core.Hashing;

/// <summary>
/// Content-addressed identifier. SHA-256 over canonical UTF-8 bytes,
/// rendered as lower-hex with a short "lr1:" prefix so we can evolve the
/// hashing scheme without breaking ids.
/// </summary>
public readonly record struct ContentHash(string Value)
{
    public const string Prefix = "lr1:";
    public override string ToString() => Value;

    public static ContentHash OfBytes(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return new ContentHash(Prefix + Convert.ToHexStringLower(hash));
    }

    public static ContentHash OfString(string value)
        => OfBytes(Encoding.UTF8.GetBytes(value));

    public static ContentHash OfFile(string path)
    {
        using var fs = File.OpenRead(path);
        var hash = SHA256.HashData(fs);
        return new ContentHash(Prefix + Convert.ToHexStringLower(hash));
    }

    /// <summary>
    /// Stable composite hash — used to derive cache keys.
    /// Order of parts is significant; pre-hash any large inputs.
    /// </summary>
    public static ContentHash Compose(params string[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            sb.Append(p ?? string.Empty);
            sb.Append('\u001f'); // ASCII unit separator — never appears in valid input
        }
        return OfString(sb.ToString());
    }
}
