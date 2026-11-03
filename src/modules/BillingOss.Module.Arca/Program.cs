using System.Text;
using BillingOss.Module.Arca;
using BillingOss.Modules.Sdk;

var builder = WebApplication.CreateBuilder(args);

// Secretos como archivos (Docker secrets): /run/secrets/Module__SigningSecret es Module:SigningSecret.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);
var app = builder.Build();

// El secreto de firma es el mismo que tiene el núcleo en su instalación de este módulo. Durante una rotación
// se aceptan el nuevo y el anterior a la vez.
var secrets = new List<byte[]> { Encoding.UTF8.GetBytes(Required("Module:SigningSecret")) };
if (app.Configuration["Module:PreviousSigningSecret"] is { Length: > 0 } previous)
{
    secrets.Add(Encoding.UTF8.GetBytes(previous));
}

// Con varias réplicas, el registro de idempotencia va en PostgreSQL (docs/alta-disponibilidad/modulos.md). Si no,
// en un archivo SQLite. Una ruta relativa se toma desde el directorio del módulo, no desde donde se lo haya lanzado.
var pg = app.Configuration["Module:IdempotencyConnectionString"];
string? store = null;
if (string.IsNullOrEmpty(pg))
{
    store = Path.GetFullPath(app.Configuration["Module:IdempotencyStorePath"] ?? "data/idempotency.db", app.Environment.ContentRootPath);
    Directory.CreateDirectory(Path.GetDirectoryName(store)!);
}

// ARCA a veces tarda: 30 segundos por pedido. El tiempo para conectar es corto porque ahí el pedido todavía no salió.
using var http = new HttpClient(new SocketsHttpHandler
{
    // Sin redirecciones: un 307 mandaría el ticket y la firma a otro destino.
    AllowAutoRedirect = false,
    ConnectTimeout = TimeSpan.FromSeconds(10),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
})
{
    Timeout = TimeSpan.FromSeconds(30),
};

// Traba de producción en tres posiciones (Arca:Production): off por defecto, read deja pasar solo la lista cerrada
// de consultas y on deja emitir. Arca:AllowProduction en true vale como on, para las recetas anteriores.
var production = ProductionModes.Parse(app.Configuration["Arca:Production"], app.Configuration.GetValue<bool>("Arca:AllowProduction"));

app.MapModule(
    ArcaModule.Create(http, TimeProvider.System, app.Logger, production),
    new ModuleSdkOptions { SigningSecrets = secrets, IdempotencyStorePath = store, IdempotencyConnectionString = pg });
app.Run();

string Required(string key) =>
    app.Configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Falta la configuración {key}. Se pasa por variable de entorno como {key.Replace(":", "__", StringComparison.Ordinal)}.");
