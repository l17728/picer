using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace Picer.Core.Identity;

[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceCredentialStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);
    public bool Exists => File.Exists(_path);

    public DeviceConnection Load()
    {
        using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 128 * 1024) throw new InvalidDataException("credential_file_too_large");
        var cipher = new byte[(int)input.Length];
        input.ReadExactly(cipher);
        var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
        try { return DeviceAuthorization.ReadDocument<DeviceConnection>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Save(DeviceConnection connection)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(connection, DeviceAuthorization.Json);
        byte[] cipher;
        try { cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(cipher); output.Flush(true); }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public DeviceConnection Create(ServiceDiscovery discovery, string trustedSpkiPin, string deviceName, string kind)
    {
        if (Exists) throw new InvalidOperationException("device_connection_already_exists");
        if (discovery.Version != 2 || !Guid.TryParseExact(discovery.ServiceId, "N", out _) || kind is not ("gui" or "agent"))
            throw new ArgumentException("invalid_service_identity");
        using var signing = DeviceAuthorization.ReadPublicKey(discovery.SigningKey);
        var keyName = "Picer.Device." + Guid.NewGuid().ToString("N");
        using var key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters
        { Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing });
        var connection = new DeviceConnection(DeviceAuthorization.NormalizeOrigin(discovery.Origin), discovery.ServiceId, trustedSpkiPin,
            discovery.SigningKey, keyName, Guid.NewGuid().ToString("N"), deviceName, kind);
        try { Save(connection); return connection; }
        catch { key.Delete(); throw; }
    }

    public DeviceAccountClient OpenClient()
    {
        var connection = Load();
        return new(connection, OpenKey(connection), Save, Load, AcquireRefreshLeaseAsync);
    }

    public async Task ForgetAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await AcquireRefreshLeaseAsync(cancellationToken).ConfigureAwait(false);
        DeviceConnection? previous = null;
        try { if (Exists) previous = Load(); }
        catch (Exception exception) when (exception is CryptographicException or InvalidDataException or JsonException) { }
        File.Delete(_path);
        if (previous is not null && previous.KeyName.StartsWith("Picer.Device.", StringComparison.Ordinal)
            && Guid.TryParseExact(previous.KeyName[13..], "N", out _))
        {
            try
            {
                using var key = CngKey.Open(previous.KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
                key.Delete();
            }
            catch (CryptographicException) { /* A missing key is already unusable. */ }
        }
    }

    private async Task<IDisposable> AcquireRefreshLeaseAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { return new FileStream(_path + ".refresh-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, deadline.Token).ConfigureAwait(false); }
        }
    }

    public static ECDsa OpenKey(DeviceConnection connection)
    {
        if (!connection.KeyName.StartsWith("Picer.Device.", StringComparison.Ordinal) ||
            !Guid.TryParseExact(connection.KeyName[13..], "N", out _)) throw new InvalidDataException("invalid_device_key_name");
        using var key = CngKey.Open(connection.KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        return new ECDsaCng(key);
    }
}
