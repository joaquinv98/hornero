using System.Text;
using BillingOss.Module.TacticalRmm;
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

var store = Path.GetFullPath(app.Configuration["Module:IdempotencyStorePath"] ?? "data/idempotency.db");
// Con el registro en PostgreSQL (Module:IdempotencyConnectionString, que lee la SDK) el archivo no se usa: sin el
// directorio, el módulo corre con el disco de solo lectura (docs/alta-disponibilidad/modulos.md).
if (string.IsNullOrEmpty(app.Configuration["Module:IdempotencyConnectionString"]))
{
    Directory.CreateDirectory(Path.GetDirectoryName(store)!);
}

// Una operación hace hasta tres pedidos a Tactical y el manifiesto le da al núcleo 60 segundos como máximo:
// 15 segundos por pedido deja margen para contestar antes que él.
using var http = new HttpClient(new SocketsHttpHandler
{
    // Sin redirecciones: un 30x mandaría la API key a otro destino.
    AllowAutoRedirect = false,
    ConnectTimeout = TimeSpan.FromSeconds(5),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
})
{
    Timeout = TimeSpan.FromSeconds(15),
};

app.MapModule(
    TacticalRmmModule.Create(http, app.Logger),
    new ModuleSdkOptions { SigningSecrets = secrets, IdempotencyStorePath = store });
app.Run();

string Required(string key) =>
    app.Configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Falta la configuración {key}. Se pasa por variable de entorno como {key.Replace(":", "__", StringComparison.Ordinal)}.");
