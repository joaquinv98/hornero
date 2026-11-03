using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BillingOss.Updates.Tests;

internal static class Repo
{
    /// <summary>La raíz del repo: la carpeta que tiene BillingOss.slnx.</summary>
    public static string Root { get; } = FindRoot();

    public static string File(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public static string Read(string relative) => System.IO.File.ReadAllText(File(relative));

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "BillingOss.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No se encontró la raíz del repo.");
    }
}

/// <summary>Carpeta temporal que se borra al terminar la prueba.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("billing-oss-act-").FullName;

    public string File(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string Write(string relative, string content)
    {
        var file = File(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        System.IO.File.WriteAllText(file, content);
        return file;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Un proceso hijo que todavía no soltó un archivo: la carpeta queda en el temporal.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class Dotnet
{
    /// <summary>Corre el mismo dotnet que corre las pruebas, con el entorno de más que se pase.</summary>
    public static async Task<(int ExitCode, string Output)> Run(string workingDirectory, IReadOnlyDictionary<string, string> environment, params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output + await error);
    }
}

/// <summary>Un servidor HTTP falso en 127.0.0.1 con un puerto libre, para los sistemas de afuera de las pruebas.</summary>
internal sealed class FakeServer : IAsyncDisposable
{
    private readonly WebApplication app;

    private FakeServer(WebApplication app, Uri url)
    {
        this.app = app;
        Url = url;
    }

    public Uri Url { get; }

    public static async Task<FakeServer> Start(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new FakeServer(app, new Uri(address + "/"));
    }

    public Uri At(string path) => new(Url, path);

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}

internal static class Http
{
    public static Task Json(HttpContext http, string json, int status = 200)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(json, Encoding.UTF8);
    }
}
