using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Hestia.Tests;

/// <summary>
/// Ciclo completo contra un Hestia de laboratorio, nunca contra uno productivo. Solo corre con estas variables:
/// HESTIA_TEST_HOST, HESTIA_TEST_ACCESS_KEY_ID y HESTIA_TEST_SECRET_KEY. Opcionales: HESTIA_TEST_PORT (8083),
/// HESTIA_TEST_FINGERPRINT, HESTIA_TEST_PACKAGE (default), HESTIA_TEST_PACKAGE_2 para probar el cambio de plan, y
/// HESTIA_TEST_READONLY_ACCESS_KEY_ID con HESTIA_TEST_READONLY_SECRET_KEY, una access key con el perfil de solo lectura
/// del paralelo, para probar que Hestia le rechaza los mutadores. Sin las obligatorias queda inconclusa. Crea una cuenta
/// con un dominio .test inventado y la borra al final.
/// </summary>
[TestClass]
public sealed class RealHestiaTests
{
    [TestMethod]
    [TestCategory("HestiaReal")]
    public async Task HestiaDeLaboratorio_AltaSuspensionReactivacionCambiosYBaja()
    {
        var hostName = Environment.GetEnvironmentVariable("HESTIA_TEST_HOST");
        var accessKey = Environment.GetEnvironmentVariable("HESTIA_TEST_ACCESS_KEY_ID");
        var secretKey = Environment.GetEnvironmentVariable("HESTIA_TEST_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(hostName) || string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            Assert.Inconclusive("Faltan HESTIA_TEST_HOST, HESTIA_TEST_ACCESS_KEY_ID o HESTIA_TEST_SECRET_KEY: no hay un Hestia de laboratorio para probar.");
        }

        var config = new JsonObject
        {
            ["host"] = hostName,
            ["port"] = int.Parse(Environment.GetEnvironmentVariable("HESTIA_TEST_PORT") ?? "8083", CultureInfo.InvariantCulture),
            ["access_key_id"] = accessKey,
            ["secret_key"] = secretKey,
            ["username_prefix"] = "prueba",
        };
        if (Environment.GetEnvironmentVariable("HESTIA_TEST_FINGERPRINT") is { Length: > 0 } fingerprint)
        {
            config["tls_fingerprint"] = fingerprint;
        }

        var package = Environment.GetEnvironmentVariable("HESTIA_TEST_PACKAGE") is { Length: > 0 } p ? p : "default";
        var id = Random.Shared.NextInt64(100_000, 999_999);
        var domain = $"bo-{id}.test";

        // Con el mismo cliente HTTP que usa el módulo de verdad, y con la Hestia falsa sin usar.
        await using var host = await HestiaHost.Start(new FakeHestia(), HestiaHttp.For);
        var context = HestiaHost.NewService(domain: domain, package: package, id: id);

        // 1. Configuración y validación sin efectos.
        var (status, body) = await host.Send("/v0/instance/test", [], config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        (status, body) = await host.Op("validate", context, config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));

        // 2. Alta con una contraseña como las del núcleo, y la misma clave otra vez.
        var password = CorePassword();
        var key = $"svc-{id}-create-1";
        (status, body) = await host.Op("create", context, HestiaHost.Credentials(password), key: key, config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        Assert.AreEqual($"prueba{id}", body!["result"]!["external_ids"]!["username"]!.GetValue<string>());
        (status, _) = await host.Op("create", context, HestiaHost.Credentials(password), key: key, config: config);
        Assert.AreEqual(HttpStatusCode.OK, status);
        var existing = HestiaHost.Existing($"prueba{id}", domain, package, id);

        try
        {
            (status, _) = await host.Op("get_status", existing, config: config);
            Assert.AreEqual(HttpStatusCode.OK, status);

            // 3. Suspensión y reactivación verificadas, dos veces cada una.
            foreach (var (operation, state) in new[] { ("suspend", "suspended"), ("suspend", "suspended"), ("unsuspend", "active"), ("unsuspend", "active") })
            {
                (status, body) = await host.Op(operation, existing, config: config);
                Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
                Assert.AreEqual(state, HestiaHost.Result(body, "observed_state"));
            }

            // 4. Contraseña nueva, vista y cambio de plan si hay un segundo paquete.
            var change = new JsonObject { ["action"] = "change_password", ["credentials"] = new JsonObject { ["password"] = CorePassword() } };
            (status, body) = await host.Op("run_action", existing, change, config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            (status, body) = await host.Op("view", existing, config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            if (Environment.GetEnvironmentVariable("HESTIA_TEST_PACKAGE_2") is { Length: > 0 } other)
            {
                (status, body) = await host.Op("change_plan", HestiaHost.Existing($"prueba{id}", domain, other, id), config: config);
                Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            }

            // 5. Con la access key de solo lectura del paralelo se lee, y Hestia rechaza el mutador fuera del perfil.
            if (Environment.GetEnvironmentVariable("HESTIA_TEST_READONLY_ACCESS_KEY_ID") is { Length: > 0 } readOnlyKey
                && Environment.GetEnvironmentVariable("HESTIA_TEST_READONLY_SECRET_KEY") is { Length: > 0 } readOnlySecret)
            {
                var readOnly = config.DeepClone().AsObject();
                readOnly["access_key_id"] = readOnlyKey;
                readOnly["secret_key"] = readOnlySecret;
                (status, body) = await host.Op("get_status", existing, config: readOnly);
                Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
                (_, body) = await host.Op("suspend", existing, config: readOnly);
                Assert.AreEqual(ErrorCodes.InvalidConfig, HestiaHost.Code(body), Failure(body));
                (_, body) = await host.Op("get_status", existing, config: config);
                Assert.AreEqual("active", HestiaHost.Result(body, "observed_state"));
            }
        }
        finally
        {
            // 6. Baja, y la ausencia probada.
            (status, body) = await host.Op("terminate", existing, config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            (_, body) = await host.Op("get_status", existing, config: config);
            Assert.AreEqual("not_found", HestiaHost.Result(body, "observed_state"));
        }
    }

    private static string Failure(JsonNode? body) => body?["error"]?.ToJsonString() ?? "(sin error en la respuesta)";

    /// <summary>
    /// Una contraseña como las de Passwords.New del núcleo: 20 caracteres de su alfabeto, con un guion al principio, un
    /// guion bajo y un punto, que son los que más fácil rompen un panel.
    /// </summary>
    private static string CorePassword()
    {
        var chars = RandomNumberGenerator.GetItems<char>("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_.", 20);
        chars[0] = '-';
        chars[7] = '_';
        chars[13] = '.';
        return new string(chars);
    }
}
