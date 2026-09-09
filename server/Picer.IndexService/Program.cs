using Picer.IndexService;

var dataRoot = Environment.GetEnvironmentVariable("PICER_INDEX_DATA_ROOT");
if (string.IsNullOrWhiteSpace(dataRoot))
    dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Picer", "service-data");
var port = int.TryParse(Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_PORT"), out var parsedPort) ? parsedPort : 8445;
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
