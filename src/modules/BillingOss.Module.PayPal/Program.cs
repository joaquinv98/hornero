using System.Text;
using BillingOss.Module.PayPal;
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

var store = Path.GetFullPath(app.Configuration["Module:IdempotencyStorePath"] ?? "data/idempotency.db");
// Con el registro en PostgreSQL (Module:IdempotencyConnectionString, que lee la SDK) el archivo no se usa: sin el
// directorio, el módulo corre con el disco de solo lectura (docs/alta-disponibilidad/modulos.md).
if (string.IsNullOrEmpty(app.Configuration["Module:IdempotencyConnectionString"]))
{
    Directory.CreateDirectory(Path.GetDirectoryName(store)!);
}

// Cada pedido a PayPal tiene 25 segundos. Las operaciones que hacen más de uno lo declaran en el manifiesto.
// El tiempo para conectar es corto porque ahí el pedido todavía no salió.
using var http = new HttpClient(new SocketsHttpHandler
{
    // Sin redirecciones: un 307 mandaría el token de PayPal a otro destino.
    AllowAutoRedirect = false,
    ConnectTimeout = TimeSpan.FromSeconds(10),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
})
{
    Timeout = TimeSpan.FromSeconds(25),
};

app.MapModule(
    PayPalModule.Create(http, app.Logger, TimeProvider.System),
    new ModuleSdkOptions { SigningSecrets = secrets, IdempotencyStorePath = store });
app.Run();

string Required(string key) =>
    app.Configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Falta la configuración {key}. Se pasa por variable de entorno como {key.Replace(":", "__", StringComparison.Ordinal)}.");
