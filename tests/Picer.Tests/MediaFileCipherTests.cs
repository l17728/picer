using System.Security.Cryptography;
using Picer.Core.Imaging;
using Picer.Core.Security;
using Picer.Core.Thumbnails;
using Picer.IndexService;

namespace Picer.Tests;

public sealed class MediaFileCipherTests
{
    [Fact]
    public void MediaCacheKeysAreStablePerTenantAndProtectedAtRest()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-media-keys-" + Guid.NewGuid().ToString("N"));
        var firstRoot = Path.Combine(root, "space-a");
        var secondRoot = Path.Combine(root, "space-b");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        try
        {
            var first = MediaCacheKeyStore.LoadOrCreate(firstRoot, "space-a");
            var reopened = MediaCacheKeyStore.LoadOrCreate(firstRoot, "space-a");
            var isolated = MediaCacheKeyStore.LoadOrCreate(secondRoot, "space-b");
            Assert.Equal(32, first.Length);
            Assert.Equal(first, reopened);
            Assert.NotEqual(first, isolated);
            Assert.NotEqual(first, File.ReadAllBytes(Path.Combine(firstRoot, "media-cache-key.protected")));
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(reopened);
            CryptographicOperations.ZeroMemory(isolated);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ClientMediaCacheKeyIsStableAndBoundToCurrentWindowsUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "picer-client-media-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = ClientMediaCacheKeyStore.LoadOrCreate(root);
            var second = ClientMediaCacheKeyStore.LoadOrCreate(root);
            Assert.Equal(32, first.Length);
            Assert.Equal(first, second);
            Assert.NotEqual(first, File.ReadAllBytes(Path.Combine(root, "media-cache-key.dpapi")));
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ProtectUsesAuthenticatedEncryptionAndBindsCacheIdentity()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var cipher = new MediaFileCipher(key);
        var plaintext = "private-thumbnail-marker"u8.ToArray();

        var protectedA = cipher.Protect(plaintext, "thumbs/1.jpg");
        var protectedB = cipher.Protect(plaintext, "thumbs/1.jpg");

        Assert.NotEqual(plaintext, protectedA);
        Assert.NotEqual(protectedA, protectedB);
        Assert.True(cipher.IsEncrypted(protectedA));
        Assert.Equal(plaintext, cipher.UnprotectOrReadLegacy(protectedA, "thumbs/1.jpg", out var encrypted));
        Assert.True(encrypted);
        Assert.ThrowsAny<CryptographicException>(() => cipher.UnprotectOrReadLegacy(protectedA, "thumbs/2.jpg", out _));

        protectedA[^1] ^= 0x40;
        Assert.ThrowsAny<CryptographicException>(() => cipher.UnprotectOrReadLegacy(protectedA, "thumbs/1.jpg", out _));
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public void HChaCha20MatchesPublishedTestVector()
    {
        var key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        var nonce = Convert.FromHexString("000000090000004a0000000031415927");
        var expected = Convert.FromHexString("82413b4227b27bfed30e42508a877d73a0f9e4d58a74a853c12ec41326d3ecdc");

        var actual = MediaFileCipher.DeriveXChaChaSubkey(key, nonce);

        Assert.Equal(expected, actual);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(actual);
    }

    [Fact]
    public void XChaChaSuiteRoundTripsAndAuthenticatesWhenSupported()
    {
        if (!ChaCha20Poly1305.IsSupported) return;
        var key = RandomNumberGenerator.GetBytes(32);
        using var cipher = new MediaFileCipher(key);
        var plaintext = "xchacha-cache-payload"u8.ToArray();
        var ciphertext = cipher.ProtectWithSuite(plaintext, "thumbs/x.jpg", MediaFileCipher.XChaCha20Poly1305SuiteId);

        Assert.True(cipher.IsEncrypted(ciphertext));
        Assert.Equal(plaintext, cipher.UnprotectOrReadLegacy(ciphertext, "thumbs/x.jpg", out var encrypted));
        Assert.True(encrypted);
        Assert.ThrowsAny<CryptographicException>(() => cipher.UnprotectOrReadLegacy(ciphertext, "thumbs/y.jpg", out _));
        ciphertext[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => cipher.UnprotectOrReadLegacy(ciphertext, "thumbs/x.jpg", out _));
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public void ThumbnailCacheEncryptsNewFilesAndLazilyMigratesLegacyFilesWithoutChangingPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-encrypted-thumbs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var source = Path.Combine(root, "source.png");
            using (var image = new RasterImage(32, 16, new RgbPixel(90, 40, 180))) image.Save(source);
            var cache = Path.Combine(root, "thumbs");
            string encryptedPath;
            using (var thumbnails = new ThumbnailService(cache, 32, new MediaFileCipher(key)))
            {
                encryptedPath = Assert.IsType<string>(thumbnails.GetOrCreate(source, 1));
                var stored = File.ReadAllBytes(encryptedPath);
                Assert.NotEqual(new byte[] { 0xFF, 0xD8 }, stored[..2]);
                using (var check = new MediaFileCipher(key)) Assert.True(check.IsEncrypted(stored));
                using var decrypted = thumbnails.OpenRead(encryptedPath);
                using var decoded = RasterImage.Load(decrypted);
                Assert.Equal(32, decoded.Width);
                Assert.Equal(16, decoded.Height);
            }

            string legacyPath;
            using (var legacy = new ThumbnailService(cache, 32))
                legacyPath = Assert.IsType<string>(legacy.GetOrCreate(source, 2));
            var legacyBytes = File.ReadAllBytes(legacyPath);
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, legacyBytes[..2]);
            var originalLegacyPath = legacyPath;
            using (var encrypted = new ThumbnailService(cache, 32, new MediaFileCipher(key)))
            {
                var migration = encrypted.ProtectExistingCacheFiles();
                Assert.Equal((1, 0), migration);
                using var check = new MediaFileCipher(key);
                Assert.True(check.IsEncrypted(File.ReadAllBytes(legacyPath)));
                using var decrypted = encrypted.OpenRead(legacyPath);
                var header = new byte[2];
                decrypted.ReadExactly(header);
                Assert.Equal(new byte[] { 0xFF, 0xD8 }, header);
                Assert.Equal((0, 0), encrypted.ProtectExistingCacheFiles());
            }
            Assert.Equal(originalLegacyPath, legacyPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }
}
