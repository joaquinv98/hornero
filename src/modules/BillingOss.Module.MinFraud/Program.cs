using System.Text;
using BillingOss.Module.MinFraud;
using BillingOss.Modules.Sdk;

var builder = WebApplication.CreateBuilder(args);

// Secretos como archivos (Docker secrets): /run/secrets/Module__SigningSecret es Module:SigningSecret.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);
var app = builder.Build();

// El secreto de firma es el mismo que tiene el núcleo en su instalación de este módulo. Durante una rotación se aceptan
// el nuevo y el anterior a la vez.
var secrets = new List<byte[]> { Encoding.UTF8.GetBytes(Required("Module:SigningSecret")) };
if (app.Configuration["Module:PreviousSigningSecret"] is { Length: > 0 } previous)
{
    secrets.Add(Encoding.UTF8.GetBytes(previous));
}

// Con varias réplicas, el registro de idempotencia va en PostgreSQL. Si no, en un archivo SQLite dentro del directorio
// del módulo.
var pg = app.Configuration["Module:IdempotencyConnectionString"];
string? store = null;
if (string.IsNullOrEmpty(pg))
{
    store = Path.GetFullPath(app.Configuration["Module:IdempotencyStorePath"] ?? "data/idempotency.db", app.Environment.ContentRootPath);
    Directory.CreateDirectory(Path.GetDirectoryName(store)!);
}

// El núcleo espera 15 segundos por evaluación (timeouts del manifiesto): minFraud tiene menos para contestar antes.
using var http = new HttpClient(new SocketsHttpHandler
{
    // Sin redirecciones: llevarían la license key a otro destino.
    AllowAutoRedirect = false,
    ConnectTimeout = TimeSpan.FromSeconds(5),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
})
{
    Timeout = TimeSpan.FromSeconds(10),
};

app.MapModule(
    MinFraudModule.Create(http, app.Logger),
    new ModuleSdkOptions { SigningSecrets = secrets, IdempotencyStorePath = store, IdempotencyConnectionString = pg });
app.Run();

string Required(string key) =>
    app.Configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Falta la configuración {key}. Se pasa por variable de entorno como {key.Replace(":", "__", StringComparison.Ordinal)}.");
