using System.Security.Cryptography;
using System.Text.Json;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

/// <summary>
/// Linux service credential store. The device private key and refresh token are held in one
/// owner-only file and atomically replaced so a power loss cannot publish a partial grant.
/// </summary>
public sealed class IndexRelayDeviceCredentialStore(string path)
{
    private sealed record Document(int Version, DeviceConnection Connection, string DevicePublicKey, string PrivateKey);
    private readonly string _path = Path.GetFullPath(path);
    public bool Exists => File.Exists(_path);

    public DeviceConnection Load() => ReadDocument().Connection;

    public async Task<EnrollmentStarted> BeginEnrollmentAsync(CancellationToken cancellationToken = default)
    {
        using var client = OpenClient();
        return await client.BeginEnrollmentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportAuthorizationAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken = default)
    {
        using var client = OpenClient();
        await client.ImportAsync(document, cancellationToken).ConfigureAwait(false);
    }

    public DeviceConnection Create(ServiceDiscovery discovery, string trustedSpkiPin, string deviceName)
    {
        if (Exists) throw new InvalidOperationException("device_connection_already_exists");
        if (discovery.Version != 2 || !Guid.TryParseExact(discovery.ServiceId, "N", out _))
            throw new ArgumentException("invalid_service_identity", nameof(discovery));
        var pin = Convert.FromBase64String(trustedSpkiPin);
        if (pin.Length is < 64 or > 2048) throw new ArgumentException("invalid_service_pin", nameof(trustedSpkiPin));
        using var signing = DeviceAuthorization.ReadPublicKey(discovery.SigningKey);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var connection = new DeviceConnection(DeviceAuthorization.NormalizeOrigin(discovery.Origin), discovery.ServiceId,
            trustedSpkiPin, discovery.SigningKey, "Picer.IndexRelay." + Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"), deviceName, "index-service");
        Save(connection, key);
        return connection;
    }

    public void Save(DeviceConnection connection, ECDsa privateKey)
    {
        ValidateConnection(connection);

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, connection,
            DeviceAuthorization.ExportPublicKey(privateKey),
            Convert.ToBase64String(privateKey.ExportPkcs8PrivateKey())), DeviceAuthorization.Json);
        if (bytes.Length > 128 * 1024) throw new InvalidDataException("credential_file_too_large");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, fileOptions))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, _path, overwrite: true);
            RestrictFile(_path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public DeviceAccountClient OpenClient()
    {
        var document = ReadDocument();
        var key = ECDsa.Create();
        try
        {
            var privateKey = Convert.FromBase64String(document.PrivateKey);
            try { key.ImportPkcs8PrivateKey(privateKey, out var read); if (read != privateKey.Length) throw new InvalidDataException("invalid_device_private_key"); }
            finally { CryptographicOperations.ZeroMemory(privateKey); }
            return new DeviceAccountClient(document.Connection, key, updated => Save(updated, key), reload: Load,
                acquireRefreshLease: AcquireRefreshLeaseAsync);
        }
        catch { key.Dispose(); throw; }
    }

    private Document ReadDocument()
    {
        using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is <= 0 or > 128 * 1024) throw new InvalidDataException("credential_file_size");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(_path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new UnauthorizedAccessException("credential_file_permissions_too_open");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        var document = DeviceAuthorization.ReadDocument<Document>(bytes, 128 * 1024);
        if (document.Version != 1) throw new InvalidDataException("invalid_index_relay_credential");
        ValidateConnection(document.Connection);
        using var key = ECDsa.Create();
        var privateKey = Convert.FromBase64String(document.PrivateKey);
        try { key.ImportPkcs8PrivateKey(privateKey, out var read); if (read != privateKey.Length) throw new InvalidDataException("invalid_device_private_key"); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
        using var publicKey = DeviceAuthorization.ReadPublicKey(document.DevicePublicKey);
        if (!CryptographicOperations.FixedTimeEquals(key.ExportSubjectPublicKeyInfo(), publicKey.ExportSubjectPublicKeyInfo()))
            throw new InvalidDataException("device_private_key_mismatch");
        return document;
    }

    private static void ValidateConnection(DeviceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.ClientKind != "index-service" || !connection.KeyName.StartsWith("Picer.IndexRelay.", StringComparison.Ordinal) ||
            !Guid.TryParseExact(connection.KeyName[17..], "N", out _) ||
            !Guid.TryParseExact(connection.ServiceId, "N", out _) || !Guid.TryParseExact(connection.RequestId, "N", out _) ||
            DeviceAuthorization.NormalizeOrigin(connection.Origin) != connection.Origin)
            throw new InvalidDataException("invalid_index_relay_credential");
        var pin = Convert.FromBase64String(connection.SpkiPin);
        if (pin.Length is < 64 or > 2048) throw new InvalidDataException("invalid_service_pin");
        using var serviceSigningKey = DeviceAuthorization.ReadPublicKey(connection.SigningKey);
        if (connection.Session is not { } session) return;
        if (session.Scope != "relay.connect" || session.ServiceId != connection.ServiceId ||
            !Guid.TryParseExact(session.SpaceId, "N", out _) || !Guid.TryParseExact(session.DeviceId, "N", out _) ||
            !Guid.TryParseExact(session.GrantId, "N", out _) || DeviceAuthorization.DecodeBase64Url(session.AccessToken).Length != 32 ||
            DeviceAuthorization.DecodeBase64Url(session.RefreshToken).Length != 32)
            throw new InvalidDataException("invalid_index_relay_session");
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

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
