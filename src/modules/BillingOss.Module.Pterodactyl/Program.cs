using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using BillingOss.Module.Pterodactyl;
using BillingOss.Modules.Sdk;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// El secreto de firma es el mismo que tiene el núcleo en su instalación de este módulo. Durante una rotación
// se aceptan el nuevo y el anterior a la vez.
var secrets = new List<byte[]> { Encoding.UTF8.GetBytes(Required("Module:SigningSecret")) };
if (app.Configuration["Module:PreviousSigningSecret"] is { Length: > 0 } previous)
{
    secrets.Add(Encoding.UTF8.GetBytes(previous));
}

// Una ruta relativa se toma desde el directorio del módulo, no desde donde se lo haya lanzado.
var store = Path.GetFullPath(app.Configuration["Module:IdempotencyStorePath"] ?? "data/idempotency.db", app.Environment.ContentRootPath);
// Con el registro en PostgreSQL (Module:IdempotencyConnectionString, que lee la SDK) el archivo no se usa: sin el
// directorio, el módulo corre con el disco de solo lectura (docs/alta-disponibilidad/modulos.md).
if (string.IsNullOrEmpty(app.Configuration["Module:IdempotencyConnectionString"]))
{
    Directory.CreateDirectory(Path.GetDirectoryName(store)!);
}

// 20 segundos por llamada al panel: con 5, el alta daba timeout y se perdía la confirmación. El certificado del
// panel siempre se valida y no hay opción para no hacerlo.
using var http = new HttpClient(new SocketsHttpHandler
{
    // Sin redirecciones: un 30x mandaría la application_key a otro destino.
    AllowAutoRedirect = false,
    ConnectTimeout = TimeSpan.FromSeconds(10),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 },
})
{
    Timeout = TimeSpan.FromSeconds(20),
};

app.MapModule(
    PterodactylModule.Create(http, app.Logger),
    new ModuleSdkOptions { SigningSecrets = secrets, IdempotencyStorePath = store });
app.Run();

string Required(string key) =>
    app.Configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Falta la configuración {key}. Se pasa por variable de entorno como {key.Replace(":", "__", StringComparison.Ordinal)}.");
