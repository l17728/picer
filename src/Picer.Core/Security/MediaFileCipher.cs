using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Picer.Core.Security;

/// <summary>Authenticated encryption for regenerable media cache files.</summary>
public sealed class MediaFileCipher : IDisposable
{
    private static readonly byte[] Magic = "PICERENC2"u8.ToArray();
    private readonly byte[] _key;
    private const int TagLength = 16;
    private const byte AesGcmSuite = 1;
    private const byte XChaCha20Poly1305Suite = 2;
    internal const byte XChaCha20Poly1305SuiteId = XChaCha20Poly1305Suite;
    private const int AesNonceLength = 12;
    private const int XChaChaNonceLength = 24;

    public MediaFileCipher(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new ArgumentException("media_key_must_be_256_bits", nameof(key));
        _key = key.ToArray();
        if (!AesGcm.IsSupported && !ChaCha20Poly1305.IsSupported)
            throw new PlatformNotSupportedException("media_cache_aead_unavailable");
    }

    private static bool HasMagic(ReadOnlySpan<byte> content) => content.Length >= Magic.Length
        && CryptographicOperations.FixedTimeEquals(content[..Magic.Length], Magic);

    private static int NonceLength(byte suite) => suite switch
    {
        AesGcmSuite => AesNonceLength,
        XChaCha20Poly1305Suite => XChaChaNonceLength,
        _ => 0
    };

    public bool IsEncrypted(ReadOnlySpan<byte> content)
    {
        if (!HasMagic(content) || content.Length <= Magic.Length) return false;
        var nonceLength = NonceLength(content[Magic.Length]);
        return nonceLength != 0 && content.Length >= Magic.Length + 1 + nonceLength + TagLength;
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var suite = AesGcm.IsSupported ? AesGcmSuite : XChaCha20Poly1305Suite;
        return ProtectWithSuite(plaintext, context, suite);
    }

    internal byte[] ProtectWithSuite(ReadOnlySpan<byte> plaintext, string context, byte suite)
    {
        ArgumentNullException.ThrowIfNull(context);
        var nonceLength = NonceLength(suite);
        if (nonceLength == 0) throw new InvalidDataException("media_cache_cipher_suite_invalid");
        if (suite == AesGcmSuite && !AesGcm.IsSupported)
            throw new PlatformNotSupportedException("media_cache_aes_gcm_unavailable");
        if (suite == XChaCha20Poly1305Suite && !ChaCha20Poly1305.IsSupported)
            throw new PlatformNotSupportedException("media_cache_xchacha20_unavailable");

        var nonce = RandomNumberGenerator.GetBytes(nonceLength);
        var headerLength = Magic.Length + 1 + nonceLength;
        var result = new byte[headerLength + TagLength + plaintext.Length];
        Magic.CopyTo(result, 0);
        result[Magic.Length] = suite;
        nonce.CopyTo(result, Magic.Length + 1);
        var ciphertext = result.AsSpan(headerLength + TagLength);
        var tag = result.AsSpan(headerLength, TagLength);
        var aad = AssociatedData(context, suite);
        if (suite == AesGcmSuite)
        {
            using var aes = new AesGcm(_key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }
        else
        {
            using var chacha = new XChaCha20Poly1305(_key);
            chacha.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }
        CryptographicOperations.ZeroMemory(aad);
        CryptographicOperations.ZeroMemory(nonce);
        return result;
    }

    /// <summary>Legacy plaintext is accepted for lazy cache migration; callers must rewrite it before returning.</summary>
    public byte[] UnprotectOrReadLegacy(ReadOnlySpan<byte> content, string context, out bool wasEncrypted)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (HasMagic(content) && !IsEncrypted(content)) throw new InvalidDataException("media_cache_ciphertext_truncated_or_invalid");
        wasEncrypted = IsEncrypted(content);
        if (!wasEncrypted) return content.ToArray();

        var suite = content[Magic.Length];
        var nonceLength = NonceLength(suite);
        var headerLength = Magic.Length + 1 + nonceLength;
        var nonce = content.Slice(Magic.Length + 1, nonceLength);
        var tag = content.Slice(headerLength, TagLength);
        var ciphertext = content[(headerLength + TagLength)..];
        var plaintext = new byte[ciphertext.Length];
        var aad = AssociatedData(context, suite);
        if (suite == AesGcmSuite)
        {
            using var aes = new AesGcm(_key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        else
        {
            using var chacha = new XChaCha20Poly1305(_key);
            chacha.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        CryptographicOperations.ZeroMemory(aad);
        return plaintext;
    }

    private static byte[] AssociatedData(string context, byte suite) =>
        Encoding.UTF8.GetBytes($"picer-media-cache-v2\0{suite}\0{context}");

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);

    internal static byte[] DeriveXChaChaSubkey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce16) =>
        XChaCha20Poly1305.HChaCha20(key, nonce16);

    private sealed class XChaCha20Poly1305 : IDisposable
    {
        private readonly byte[] _masterKey;

        public XChaCha20Poly1305(ReadOnlySpan<byte> key) => _masterKey = key.ToArray();

        public void Encrypt(ReadOnlySpan<byte> nonce24, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext,
            Span<byte> tag, ReadOnlySpan<byte> associatedData)
        {
            ValidateNonce(nonce24);
            var subkey = HChaCha20(_masterKey, nonce24[..16]);
            Span<byte> nonce12 = stackalloc byte[12];
            nonce24[16..].CopyTo(nonce12[4..]);
            try
            {
                using var chacha = new ChaCha20Poly1305(subkey);
                chacha.Encrypt(nonce12, plaintext, ciphertext, tag, associatedData);
            }
            finally { CryptographicOperations.ZeroMemory(subkey); }
        }

        public void Decrypt(ReadOnlySpan<byte> nonce24, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag,
            Span<byte> plaintext, ReadOnlySpan<byte> associatedData)
        {
            ValidateNonce(nonce24);
            var subkey = HChaCha20(_masterKey, nonce24[..16]);
            Span<byte> nonce12 = stackalloc byte[12];
            nonce24[16..].CopyTo(nonce12[4..]);
            try
            {
                using var chacha = new ChaCha20Poly1305(subkey);
                chacha.Decrypt(nonce12, ciphertext, tag, plaintext, associatedData);
            }
            finally { CryptographicOperations.ZeroMemory(subkey); }
        }

        internal static byte[] HChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce16)
        {
            if (key.Length != 32 || nonce16.Length != 16) throw new ArgumentException("xchacha_input_size_invalid");
            uint[] state =
            [
                0x61707865, 0x3320646e, 0x79622d32, 0x6b206574,
                BinaryPrimitives.ReadUInt32LittleEndian(key), BinaryPrimitives.ReadUInt32LittleEndian(key[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(key[8..]), BinaryPrimitives.ReadUInt32LittleEndian(key[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(key[16..]), BinaryPrimitives.ReadUInt32LittleEndian(key[20..]),
                BinaryPrimitives.ReadUInt32LittleEndian(key[24..]), BinaryPrimitives.ReadUInt32LittleEndian(key[28..]),
                BinaryPrimitives.ReadUInt32LittleEndian(nonce16), BinaryPrimitives.ReadUInt32LittleEndian(nonce16[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(nonce16[8..]), BinaryPrimitives.ReadUInt32LittleEndian(nonce16[12..])
            ];

            for (var i = 0; i < 10; i++)
            {
                QuarterRound(state, 0, 4, 8, 12); QuarterRound(state, 1, 5, 9, 13);
                QuarterRound(state, 2, 6, 10, 14); QuarterRound(state, 3, 7, 11, 15);
                QuarterRound(state, 0, 5, 10, 15); QuarterRound(state, 1, 6, 11, 12);
                QuarterRound(state, 2, 7, 8, 13); QuarterRound(state, 3, 4, 9, 14);
            }

            var subkey = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(subkey, state[0]);
            BinaryPrimitives.WriteUInt32LittleEndian(subkey.AsSpan(4), state[1]);
            BinaryPrimitives.WriteUInt32LittleEndian(subkey.AsSpan(8), state[2]);
            BinaryPrimitives.WriteUInt32LittleEndian(subkey.AsSpan(12), state[3]);
            BinaryPrimitives.WriteUInt32LittleEndian(subkey.AsSpan(16), state[12]);
            BinaryPrimitives.WriteUInt32LittleEndian(subkey.AsSpan(20), state[13]);
            BinaryPrimitives.WriteUInt32LittleEndian(subkey.AsSpan(24), state[14]);
            BinaryPrimitives.WriteUInt32LittleEndian(subkey.AsSpan(28), state[15]);
            Array.Clear(state);
            return subkey;
        }

        private static void QuarterRound(uint[] s, int a, int b, int c, int d)
        {
            s[a] = unchecked(s[a] + s[b]); s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 16);
            s[c] = unchecked(s[c] + s[d]); s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 12);
            s[a] = unchecked(s[a] + s[b]); s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 8);
            s[c] = unchecked(s[c] + s[d]); s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 7);
        }

        private static void ValidateNonce(ReadOnlySpan<byte> nonce)
        {
            if (nonce.Length != XChaChaNonceLength) throw new ArgumentException("xchacha_nonce_size_invalid", nameof(nonce));
        }

        public void Dispose() => CryptographicOperations.ZeroMemory(_masterKey);
    }
}
