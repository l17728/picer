using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Picer.Core.Identity;
using Picer.IndexService.Identity;

namespace Picer.Tests;

public sealed class IndexRelayDeviceCredentialStoreTests
{
    [Fact]
    public void CreatePersistsRelayOnlyIdentityAndOwnerOnlyFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-relay-credential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var discovery = new ServiceDiscovery(2, Guid.NewGuid().ToString("N"), "https://relay.example.test",
                Convert.ToBase64String(new byte[64]), DeviceAuthorization.ExportPublicKey(serverKey), "/identity/enrollments", "/identity/service");
            var path = Path.Combine(root, "private", "relay.json");
            var store = new IndexRelayDeviceCredentialStore(path);

            var created = store.Create(discovery, discovery.SpkiPin, "local-index-service");
            var loaded = store.Load();
            using var client = store.OpenClient();

            Assert.Equal("index-service", created.ClientKind);
            Assert.Equal(created, loaded);
            Assert.Equal("index-service", client.Connection.ClientKind);
            Assert.Null(client.Connection.Session);
            Assert.True(File.Exists(path));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.GetDirectoryName(path)!));
            }
            Assert.Throws<InvalidOperationException>(() => store.Create(discovery, discovery.SpkiPin, "duplicate"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RejectsCredentialWhenPrivateKeyBindingIsTampered()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-relay-credential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var discovery = new ServiceDiscovery(2, Guid.NewGuid().ToString("N"), "https://relay.example.test",
                Convert.ToBase64String(new byte[64]), DeviceAuthorization.ExportPublicKey(serverKey), "/identity/enrollments", "/identity/service");
            var path = Path.Combine(root, "relay.json");
            var store = new IndexRelayDeviceCredentialStore(path);
            store.Create(discovery, discovery.SpkiPin, "local-index-service");

            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            document["devicePublicKey"] = DeviceAuthorization.ExportPublicKey(otherKey);
            File.WriteAllText(path, document.ToJsonString());

            Assert.Throws<InvalidDataException>(() => store.Load());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
