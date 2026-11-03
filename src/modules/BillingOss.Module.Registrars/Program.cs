using System.Text;
using BillingOss.Module.Registrars;
using BillingOss.Modules.Sdk;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Un proceso atiende un registrar: Module:Registrar elige cuál (internetbs, resellerclub, openprovider o namecheap).
// El secreto de firma es el mismo que tiene el núcleo en su instalación. Durante una rotación se aceptan dos.
var secrets = new List<byte[]> { Encoding.UTF8.GetBytes(Required("Module:SigningSecret")) };
if (app.Configuration["Module:PreviousSigningSecret"] is { Length: > 0 } previous)
{
    secrets.Add(Encoding.UTF8.GetBytes(previous));
}

var store = Path.GetFullPath(app.Configuration["Module:IdempotencyStorePath"] ?? "data/idempotency.db");
Directory.CreateDirectory(Path.GetDirectoryName(store)!);

// El núcleo espera 30 segundos por llamada y un registro hace hasta tres pedidos: cada uno tiene 9. Conectar es corto
// porque ahí el pedido todavía no salió. Sin redirecciones: llevarían las credenciales a otro destino.
using var http = new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    ConnectTimeout = TimeSpan.FromSeconds(5),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
})
{
    Timeout = TimeSpan.FromSeconds(9),
};

app.MapModule(
    RegistrarModule.Create(Required("Module:Registrar"), http, app.Logger),
    new ModuleSdkOptions { SigningSecrets = secrets, IdempotencyStorePath = store });
app.Run();

string Required(string key) =>
    app.Configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Falta la configuración {key}. Se pasa por variable de entorno como {key.Replace(":", "__", StringComparison.Ordinal)}.");
