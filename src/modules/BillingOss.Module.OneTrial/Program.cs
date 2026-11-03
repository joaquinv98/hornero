using System.Text;
using BillingOss.Module.OneTrial;
using BillingOss.Modules.Sdk;

var builder = WebApplication.CreateBuilder(args);

// Secretos como archivos (Docker secrets): /run/secrets/Module__SigningSecret es Module:SigningSecret.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);
var app = builder.Build();

// El secreto de firma es el mismo que se carga en el núcleo al agregar la extensión. Durante una rotación se aceptan
// el nuevo y el anterior a la vez.
var secrets = new List<byte[]> { Encoding.UTF8.GetBytes(Required("Module:SigningSecret")) };
if (app.Configuration["Module:PreviousSigningSecret"] is { Length: > 0 } previous)
{
    secrets.Add(Encoding.UTF8.GetBytes(previous));
}

// Con varias réplicas, el registro de idempotencia va en PostgreSQL (Module:IdempotencyConnectionString). Si no, en
// un archivo SQLite que sobrevive a los reinicios.
var pg = app.Configuration["Module:IdempotencyConnectionString"];
string? store = null;
if (string.IsNullOrEmpty(pg))
{
    store = Path.GetFullPath(app.Configuration["Module:IdempotencyStorePath"] ?? "data/idempotency.db", app.Environment.ContentRootPath);
    Directory.CreateDirectory(Path.GetDirectoryName(store)!);
}

app.MapModule(OneTrialModule.Create(app.Logger), new ModuleSdkOptions { SigningSecrets = secrets, IdempotencyStorePath = store, IdempotencyConnectionString = pg });
app.Run();

string Required(string key) =>
    app.Configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Falta la configuración {key}. Se pasa por variable de entorno como {key.Replace(":", "__", StringComparison.Ordinal)}.");
