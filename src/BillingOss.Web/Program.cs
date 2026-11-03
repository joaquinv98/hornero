using BillingOss.Web;

// Chequeo de salud del contenedor, que no trae curl: "probe <url>" sale con 0 si la URL responde 2xx.
if (args is ["probe", var url])
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        using var response = await http.GetAsync(new Uri(url));
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

// Secretos como archivos (Docker secrets o credenciales de systemd): cada archivo de /run/secrets es una clave,
// con "__" como separador. Por ejemplo, ConnectionStrings__Main.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

var app = AppHost.Build(builder);
if (args.Contains("migrate"))
{
    await AppHost.Migrate(app);
    return 0;
}

if (args is ["staff", ..])
{
    return await BillingOss.Web.Identity.StaffCommand.Run(app, args[1..]);
}

// Conciliación con el fisco, las pasarelas y los módulos después de restaurar o promover con pérdida (D-14).
if (args is ["reconcile", ..])
{
    return await ReconcileCommand.Run(app, args[1..]);
}

await app.RunAsync();
return 0;
