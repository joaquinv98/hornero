using System.Text;
using BillingOss.Module.Proxmox;
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

// El gateway de consola tiene un certificado público: vale el almacén del sistema. Sin redirecciones, porque el grant
// de revocación no tiene que ir a otro destino.
using var gateway = new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    ConnectTimeout = TimeSpan.FromSeconds(5),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
})
{
    Timeout = TimeSpan.FromSeconds(10),
};

app.MapModule(
    ProxmoxModule.Create(Transport.Handler, gateway, TimeProvider.System, app.Logger),
    new ModuleSdkOptions { SigningSecrets = secrets, IdempotencyStorePath = store });
app.Run();

string Required(string key) =>
    app.Configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Falta la configuración {key}. Se pasa por variable de entorno como {key.Replace(":", "__", StringComparison.Ordinal)}.");
