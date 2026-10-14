using Picer.IndexService;
using Picer.IndexService.Identity;

var dataRoot = Environment.GetEnvironmentVariable("PICER_INDEX_DATA_ROOT");
if (string.IsNullOrWhiteSpace(dataRoot))
    dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Picer", "service-data");
if (args.Length > 0)
{
    if (args[0] is "--migrate-legacy" or "--reset-account-password")
        Environment.ExitCode = AccountAdministration.RunMigration(args, dataRoot);
    else
    {
        Console.Error.WriteLine("用法：Picer.IndexService --migrate-legacy --database <旧picer.db> --owner <账号> [--space <空间名称>] [--password-stdin]");
        Environment.ExitCode = args[0] is "--help" or "-h" ? 0 : 2;
    }
    return;
}
var port = int.TryParse(Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_PORT"), out var parsedPort) ? parsedPort : 8445;
var publicOrigin = Environment.GetEnvironmentVariable("PICER_INDEX_PUBLIC_ORIGIN");
if (!string.IsNullOrWhiteSpace(publicOrigin))
{
    var accountInstance = AccountServiceHost.Build(dataRoot, publicOrigin, port, System.Net.IPAddress.Any,
        allowRegistration: Environment.GetEnvironmentVariable("PICER_INDEX_ALLOW_REGISTRATION") != "0");
    try { await accountInstance.App.RunAsync(); }
    finally { await accountInstance.App.DisposeAsync(); accountInstance.Certificate.Dispose(); }
    return;
}
var pipeName = $"picer-index-{Environment.MachineName.ToLowerInvariant()}-{Environment.ProcessId}";
var token = ServiceSecurity.ResolveToken(dataRoot);
var instance = IndexServiceHost.Build(dataRoot, port, token);
var app = instance.App;
var pipeServer = new NamedPipeIndexServer(pipeName, token, instance.Handlers, instance.Engine, instance.Jobs);
var pipeTask = pipeServer.RunAsync();
app.Lifetime.ApplicationStarted.Register(() =>
    ServiceSecurity.WriteEndpointFile(dataRoot, IndexServiceHost.GetBaseUrl(app), ServiceSecurity.ComputeSpkiPin(instance.Certificate), token, pipeName));
await app.RunAsync();
pipeServer.Dispose();
await pipeTask;
