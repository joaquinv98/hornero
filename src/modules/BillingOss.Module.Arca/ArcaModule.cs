using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Arca;

/// <summary>
/// Traba de producción del proceso (Arca:Production). Con off toda instancia de producción se rechaza antes de leer el
/// certificado. Con read pasan solo las operaciones de la lista cerrada de <see cref="ReadOnlyOperations"/>, que no
/// autorizan nada: así la prueba sin emitir corre contra producción antes del corte. Con on se emite.
/// </summary>
internal enum ProductionMode
{
    Off,
    Read,
    On,
}

internal static class ProductionModes
{
    /// <summary>Lee Arca:Production. Sin valor, Arca:AllowProduction en true vale como on, para las recetas anteriores.</summary>
    public static ProductionMode Parse(string? value, bool legacyAllow = false) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => legacyAllow ? ProductionMode.On : ProductionMode.Off,
        "off" => ProductionMode.Off,
        "read" => ProductionMode.Read,
        "on" => ProductionMode.On,
        _ => throw new InvalidOperationException($"Arca:Production vale off, read u on. Recibido: {value}."),
    };
}

/// <summary>
/// Las operaciones de ARCA que solo consultan, según sus manuales: el login de WSAA, FEDummy, las tablas FEParamGet*,
/// el último autorizado y la consulta de comprobantes de WSFEv1, sus equivalentes de WSFEX y el padrón. Es una lista
/// de permitidos y no de prohibidos: una operación nueva (CAEA, por ejemplo) no pasa en modo read hasta que alguien
/// la agregue acá, y un test pide authorize contra producción en read y espera el rechazo.
/// </summary>
internal static class ReadOnlyOperations
{
    private static readonly string[] Exact = ["loginCms", "FEDummy", "FECompUltimoAutorizado", "FECompConsultar", "FEXDummy", "FEXGetLast_CMP", "FEXGetLast_ID", "FEXGetCMP", "dummy", "getPersona_v2"];

    private static readonly string[] Prefixes = ["FEParamGet", "FEXGetPARAM"];

    public static bool Allows(string operation) =>
        Exact.Contains(operation, StringComparer.Ordinal) || Prefixes.Any(p => operation.StartsWith(p, StringComparison.Ordinal));
}

/// <summary>Módulo fiscal de ARCA: manifiesto y operaciones (sección 9.4 del protocolo).</summary>
internal static class ArcaModule
{
    /// <summary>
    /// <paramref name="production"/> es la traba del módulo: con off, toda instancia con environment = production se
    /// rechaza con invalid_config antes de tocar el certificado o ARCA. Se cambia con Arca:Production.
    /// </summary>
    public static ModuleDefinition Create(HttpClient http, TimeProvider time, ILogger logger, ProductionMode production = ProductionMode.Off)
    {
        var operations = new ArcaOperations(http, time, logger, production);
        return new ModuleDefinition(Manifest())
            .Operation("fiscal", "parameters", operations.Parameters, mutating: false)
            .Operation("fiscal", "last_number", operations.LastNumber, mutating: false)
            .Operation("fiscal", "authorize", operations.Authorize)
            .Operation("fiscal", "find", operations.Find, mutating: false)
            .Operation("fiscal", "exchange_rate", operations.ExchangeRate, mutating: false)
            .Operation("fiscal", "taxpayer", operations.Taxpayer, mutating: false)
            .InstanceTest(operations.Test);
    }

    public static JsonObject Manifest() => new()
    {
        ["protocol"] = new JsonArray("0"),
        ["id"] = "arca",
        ["name"] = "ARCA, factura electrónica",
        ["version"] = "0.1.0",
        ["vendor"] = "Hornero",
        ["license"] = "Apache-2.0",
        ["kinds"] = new JsonArray("fiscal"),
        ["capabilities"] = new JsonArray("fiscal.core", "fiscal.exchange_rate", "fiscal.taxpayer", "instance_state"),
        ["config_schema"] = JsonNode.Parse("""
            {
              "type": "object",
              "required": ["cuit", "environment", "certificate_pem", "private_key_pem"],
              "properties": {
                "cuit": { "type": "string", "pattern": "^[0-9]{11}$", "description": "CUIT del emisor, 11 dígitos sin guiones" },
                "environment": { "type": "string", "enum": ["homologation", "production"] },
                "certificate_pem": { "type": "string", "x-secret": true, "description": "Certificado X.509 en PEM" },
                "private_key_pem": { "type": "string", "x-secret": true, "description": "Clave privada del certificado en PEM, sin contraseña" }
              }
            }
            """),
        ["data_scopes"] = new JsonArray(),

        // Un login de WSAA más el pedido a WSFEv1 pueden pasar de los 30 segundos por defecto. exchange_rate puede probar hasta
        // 11 fechas seguidas con un mismo ticket, así que lleva 120, que es lo máximo que el núcleo respeta.
        ["timeouts"] = new JsonObject { ["authorize"] = 90, ["parameters"] = 120, ["last_number"] = 60, ["find"] = 60, ["exchange_rate"] = 120, ["taxpayer"] = 60 },

        // El estado de la instancia guarda los tickets y no tolera dos llamadas pisándose.
        ["max_in_flight"] = 1,
    };
}

internal sealed record ArcaEndpoints(Uri Wsaa, Uri Wsfe, Uri Wsfex, Uri Padron)
{
    /// <summary>
    /// Direcciones de ARCA. Son fijas a propósito: así la clave privada nunca viaja a un destino que alguien configure.
    /// Salen de los manuales y de los WSDL publicados el 2026-10-26: WSAA y producción siguen en afip.gov.ar y los
    /// servicios de homologación de WSFEv1 y WSFEX declaran afip.gob.ar como su dirección. ARCA está migrando
    /// dominios (página "Servicios migrados"), así que acá es donde se corrige si cambian. El padrón (constancia de
    /// inscripción, ws_sr_constancia_inscripcion) comparte el servicio personaServiceA5 según su manual, a confirmar
    /// en homologación.
    /// </summary>
    public static ArcaEndpoints For(string environment) => environment == "production"
        ? new(
            new("https://wsaa.afip.gov.ar/ws/services/LoginCms"),
            new("https://servicios1.afip.gov.ar/wsfev1/service.asmx"),
            new("https://servicios1.afip.gov.ar/wsfexv1/service.asmx"),
            new("https://aws.afip.gov.ar/sr-padron/webservices/personaServiceA5"))
        : new(
            new("https://wsaahomo.afip.gov.ar/ws/services/LoginCms"),
            new("https://wswhomo.afip.gob.ar/wsfev1/service.asmx"),
            new("https://wswhomo.afip.gob.ar/wsfexv1/service.asmx"),
            new("https://awshomo.afip.gov.ar/sr-padron/webservices/personaServiceA5"));
}

/// <summary>Configuración, certificado y tickets de una llamada. Si cambian los tickets, salen como instance_state.</summary>
internal sealed class ArcaSession : IDisposable
{
    private ArcaSession(string cuit, string environment, X509Certificate2 certificate, Dictionary<string, Ticket> tickets, ProductionMode mode)
    {
        Cuit = cuit;
        Environment = environment;
        Certificate = certificate;
        Endpoints = ArcaEndpoints.For(environment);
        Tickets = tickets;
        Mode = mode;
    }

    public string Cuit { get; }

    public string Environment { get; }

    public X509Certificate2 Certificate { get; }

    public ArcaEndpoints Endpoints { get; }

    /// <summary>Tickets vigentes por servicio (wsfe, wsfex, el padrón).</summary>
    public Dictionary<string, Ticket> Tickets { get; }

    public ProductionMode Mode { get; }

    /// <summary>Si esta instancia puede emitir: siempre en homologación, y en producción solo con la traba en on.</summary>
    public bool AuthorizeEnabled => Environment != "production" || Mode == ProductionMode.On;

    public bool Changed { get; set; }

    /// <summary>Motivo del rechazo de una instancia de producción mientras el proceso no la habilita.</summary>
    public const string ProductionDisabled = "El ambiente de producción está apagado en este módulo: se habilita con Arca:Production (read para las consultas, on para emitir).";

    /// <summary>
    /// En modo read contra producción solo pasan las operaciones de consulta de la lista cerrada. Se mira antes de
    /// pedir el ticket y otra vez antes de mandar cada pedido, así nada sale por omisión.
    /// </summary>
    public void Guard(string operation)
    {
        if (Environment == "production" && Mode == ProductionMode.Read && !ReadOnlyOperations.Allows(operation))
        {
            throw Fail.InvalidConfig(["environment"], $"El módulo está en modo de solo lectura contra producción (Arca:Production=read) y {operation} no es una consulta: se habilita con Arca:Production=on.");
        }
    }

    public static ArcaSession Open(ModuleCall call, ProductionMode mode)
    {
        ArcaConfig config;
        try
        {
            config = call.Config<ArcaConfig>();
        }
        catch (JsonException)
        {
            throw Fail.InvalidConfig(["config"]);
        }

        var cuit = config.Cuit ?? string.Empty;
        var environment = config.Environment ?? string.Empty;
        var bad = new List<string>();
        if (cuit.Length != 11 || !cuit.All(char.IsAsciiDigit))
        {
            bad.Add("cuit");
        }

        if (environment is not ("homologation" or "production"))
        {
            bad.Add("environment");
        }

        if (string.IsNullOrWhiteSpace(config.CertificatePem))
        {
            bad.Add("certificate_pem");
        }

        if (string.IsNullOrWhiteSpace(config.PrivateKeyPem))
        {
            bad.Add("private_key_pem");
        }

        if (bad.Count > 0)
        {
            throw Fail.InvalidConfig(bad);
        }

        if (environment == "production" && mode == ProductionMode.Off)
        {
            // Traba de producción: ni se lee el certificado ni se pide un ticket al WSAA real.
            throw Fail.InvalidConfig(["environment"], ProductionDisabled);
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPem(config.CertificatePem, config.PrivateKeyPem);
        }
        catch (CryptographicException)
        {
            // Nunca se devuelve el detalle de la excepción: podría arrastrar parte de la clave.
            throw Fail.InvalidConfig(
                ["certificate_pem", "private_key_pem"],
                "No se pudo leer el certificado y la clave privada. Se esperan en PEM, la clave sin contraseña y del mismo par.");
        }

        // Los tickets guardados valen solo para este CUIT y este ambiente.
        var tickets = new Dictionary<string, Ticket>();
        if (call.Request.Instance.State is JsonObject state && state["tickets"] is JsonObject saved)
        {
            foreach (var (service, node) in saved)
            {
                try
                {
                    if (node?.Deserialize<Ticket>(ModuleJson.Options) is { Token.Length: > 0, Sign.Length: > 0 } ticket
                        && ticket.Cuit == cuit
                        && ticket.Environment == environment)
                    {
                        tickets[service] = ticket;
                    }
                }
                catch (JsonException)
                {
                }
            }
        }

        return new ArcaSession(cuit, environment, certificate, tickets, mode);
    }

    /// <summary>El instance_state a devolver: solo si hubo un login nuevo, para no pisar el del núcleo en vano.</summary>
    public JsonNode? StateIfChanged() =>
        Changed ? new JsonObject { ["tickets"] = JsonSerializer.SerializeToNode(Tickets, ModuleJson.Options) } : null;

    public void Dispose() => Certificate.Dispose();
}

/// <summary>Las operaciones del módulo. Cada una abre su sesión, llama a ARCA y arma la respuesta del protocolo.</summary>
internal sealed class ArcaOperations(HttpClient http, TimeProvider time, ILogger logger, ProductionMode production)
{
    /// <summary>Días hacia atrás que prueba exchange_rate cuando ARCA no tiene cotización para la fecha pedida.</summary>
    private const int MaxDaysBack = 10;

    /// <summary>Un ticket que vence en menos que esto se renueva antes de usarlo.</summary>
    private static readonly TimeSpan RenewMargin = TimeSpan.FromSeconds(60);

    private readonly SoapClient soap = new(http);

    /// <summary>
    /// Último ticket que se obtuvo por CUIT, ambiente y servicio. Un ticket pedido en una llamada que después falla
    /// viaja en el error como instance_state, pero si el núcleo no llega a guardarlo (una versión de estado pisada,
    /// una conexión cortada), WSAA no entrega otro mientras el anterior siga vigente. Esta memoria lo salva en esta
    /// réplica hasta que el núcleo lo guarde.
    /// </summary>
    private readonly ConcurrentDictionary<string, Ticket> recent = new();

    // ---- Operaciones ----

    /// <summary>
    /// FEDummy y un login de WSAA. Con fresh_login pide un ticket nuevo sin usar el guardado ni el que el módulo
    /// tiene en memoria: así renovar el certificado no se prueba con el ticket del viejo. authorize_enabled dice si
    /// esta instancia puede emitir, para que el panel muestre la traba del módulo sin adivinarla.
    /// </summary>
    public async Task<ModuleResponse> Test(ModuleCall call)
    {
        var fresh = Inputs.Read<TestInput>(call).FreshLogin == true;
        return await WithSession(call, async s =>
        {
            var ct = call.Aborted;
            var response = await Post(s, s.Endpoints.Wsfe, Wsfe.Ns.NamespaceName + "FEDummy", new XElement(Wsfe.Ns + "FEDummy"), mutating: false, ct).ConfigureAwait(false);
            var servers = Wsfe.ReadDummy(ResultOf(response, "FEDummy", mutating: false));
            var ticket = await GetTicket(s, Wsfe.Service, ct, fresh).ConfigureAwait(false);
            return ModuleResponse.Completed(
                new JsonObject
                {
                    ["environment"] = s.Environment,
                    ["cuit"] = s.Cuit,
                    ["servers"] = servers,
                    ["ticket_expires_at"] = ticket.ExpiresAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                    ["certificate_expires_on"] = Fmt.Iso(DateOnly.FromDateTime(s.Certificate.NotAfter.ToUniversalTime())),
                    ["authorize_enabled"] = s.AuthorizeEnabled,
                    ["production_mode"] = s.Mode.ToString().ToLowerInvariant(),
                },
                s.StateIfChanged());
        });
    }

    /// <summary>
    /// Las tablas de WSFEv1 más los puntos de venta y los tipos de tributo. Con export, también los puntos de venta de
    /// exportación por WSFEX, con su propio login: si la CUIT no está delegada para ese servicio, acá se nota.
    /// </summary>
    public async Task<ModuleResponse> Parameters(ModuleCall call)
    {
        var export = Inputs.Read<ParametersInput>(call).Export == true;
        return await WithSession(call, async s =>
        {
            var ct = call.Aborted;

            Task<XElement> Get(string operation) =>
                Invoke(s, Wsfe.Service, operation, mutating: false, ct, t => Wsfe.Request(operation, t, s.Cuit));

            // Sin puntos de venta ARCA responde 602 (sin datos): es una lista vacía, no un error.
            async Task<XElement?> GetOrEmpty(string operation)
            {
                try
                {
                    var result = await Get(operation).ConfigureAwait(false);
                    return Wsfe.Errors(result).Any(e => e.Code == 602) ? null : result;
                }
                catch (ModuleException ex) when (ex.Code == ErrorCodes.NotFound)
                {
                    return null;
                }
            }

            var result = new JsonObject
            {
                ["voucher_types"] = Wsfe.ReadTable(await Get("FEParamGetTiposCbte").ConfigureAwait(false), "CbteTipo", numericId: true),
                ["document_types"] = Wsfe.ReadTable(await Get("FEParamGetTiposDoc").ConfigureAwait(false), "DocTipo", numericId: true),
                ["vat_rates"] = Wsfe.ReadTable(await Get("FEParamGetTiposIva").ConfigureAwait(false), "IvaTipo", numericId: true),
                ["currencies"] = Wsfe.ReadTable(await Get("FEParamGetTiposMonedas").ConfigureAwait(false), "Moneda", numericId: false),
                ["receiver_vat_conditions"] = Wsfe.ReadVatConditions(await Get("FEParamGetCondicionIvaReceptor").ConfigureAwait(false)),
                ["tax_types"] = Wsfe.ReadTable(await Get("FEParamGetTiposTributos").ConfigureAwait(false), "TributoTipo", numericId: true),
                ["points_of_sale"] = Wsfe.ReadPointsOfSale(await GetOrEmpty("FEParamGetPtosVenta").ConfigureAwait(false)),
            };
            if (export)
            {
                XElement? points;
                try
                {
                    points = await Invoke(s, Wsfex.Service, "FEXGetPARAM_PtoVenta", mutating: false, ct, t => Wsfex.Request("FEXGetPARAM_PtoVenta", t, s.Cuit)).ConfigureAwait(false);
                }
                catch (ModuleException ex) when (ex.Code == ErrorCodes.NotFound)
                {
                    points = null;
                }

                result["export_points_of_sale"] = Wsfex.ReadPointsOfSale(points);
            }

            return ModuleResponse.Completed(result, s.StateIfChanged());
        });
    }

    /// <summary>
    /// Constancia de inscripción de un contribuyente (ws_sr_constancia_inscripcion, getPersona_v2): nombre, domicilio
    /// fiscal, impuestos y una condición frente al IVA sugerida. Ayuda a completar la ficha, nunca obliga. Las
    /// caracterizaciones no se devuelven.
    /// </summary>
    public async Task<ModuleResponse> Taxpayer(ModuleCall call)
    {
        var input = Inputs.Read<TaxpayerInput>(call);
        if (input.TaxId is not { Length: 11 } taxId || !taxId.All(char.IsAsciiDigit))
        {
            throw Fail.InvalidInput(["tax_id"]);
        }

        return await WithSession(call, async s =>
        {
            var ct = call.Aborted;
            s.Guard(Padron.Operation);
            var ticket = await GetTicket(s, Padron.Service, ct).ConfigureAwait(false);
            XElement response;
            try
            {
                response = await Post(s, s.Endpoints.Padron, string.Empty, Padron.BuildGetPersona(ticket, s.Cuit, taxId), mutating: false, ct).ConfigureAwait(false);
            }
            catch (ModuleException ex) when (ex.Code == ErrorCodes.UpstreamUnavailable && Padron.IsMissingPerson(ex.Message))
            {
                throw new ModuleException(ErrorCodes.NotFound, $"ARCA no tiene constancia de inscripción para la CUIT {taxId}.");
            }

            return ModuleResponse.Completed(Padron.ReadPersona(response, taxId), s.StateIfChanged());
        });
    }

    public async Task<ModuleResponse> LastNumber(ModuleCall call)
    {
        var key = Inputs.Read<DocumentRef>(call);
        RequireKey(key, withNumber: false);
        return await WithSession(call, async s =>
        {
            var ct = call.Aborted;
            long number;
            string? date;
            long? lastRequestId = null;
            if (Wsfex.IsExportType(key.TypeCode))
            {
                // WSFEX devuelve el número y la fecha juntos.
                var (n, d) = Wsfex.ReadLast(await Invoke(s, Wsfex.Service, "FEXGetLast_CMP", mutating: false, ct, t => Wsfex.BuildLast(t, s.Cuit, key)).ConfigureAwait(false));
                number = n;
                date = Fmt.Iso(d);

                // El ID de pedido no es de la serie sino del CUIT. Se pide después del número: si alguien emite entre las dos
                // consultas el ID sale más nuevo, nunca más viejo, y de ese lado el ID siguiente no choca con uno usado.
                lastRequestId = Wsfex.ReadLastId(await Invoke(s, Wsfex.Service, "FEXGetLast_ID", mutating: false, ct, t => Wsfex.BuildLastId(t, s.Cuit)).ConfigureAwait(false));
            }
            else
            {
                number = Wsfe.ReadLastNumber(await Invoke(
                    s,
                    Wsfe.Service,
                    "FECompUltimoAutorizado",
                    mutating: false,
                    ct,
                    t => Wsfe.Request("FECompUltimoAutorizado", t, s.Cuit, Wsfe.E("PtoVta", key.PointOfSale), Wsfe.E("CbteTipo", key.TypeCode))).ConfigureAwait(false));

                // FECompUltimoAutorizado no trae la fecha: se saca del comprobante con FECompConsultar.
                date = null;
                if (number > 0)
                {
                    var last = key with { Number = number };
                    try
                    {
                        var found = Wsfe.ReadFind(await Invoke(s, Wsfe.Service, "FECompConsultar", mutating: false, ct, t => FindRequest(t, s.Cuit, last)).ConfigureAwait(false), last);
                        date = found["document"]?["date"]?.GetValue<string>();
                    }
                    catch (ModuleException ex) when (ex.Code == ErrorCodes.NotFound)
                    {
                        // Un not_found de acá se leería como "la serie no existe". Es una inconsistencia pasajera de ARCA.
                        throw new ModuleException(ErrorCodes.UpstreamUnavailable, $"ARCA informó el comprobante {number} como el último autorizado pero no lo devuelve al consultarlo.", 30);
                    }
                }
            }

            var result = new JsonObject { ["number"] = number, ["date"] = date };
            if (lastRequestId is { } requestId)
            {
                result["last_request_id"] = requestId;
            }

            return ModuleResponse.Completed(result, s.StateIfChanged());
        });
    }

    public async Task<ModuleResponse> Find(ModuleCall call)
    {
        var key = Inputs.Read<DocumentRef>(call);
        RequireKey(key, withNumber: true);
        return await WithSession(call, async s =>
        {
            var ct = call.Aborted;
            var result = Wsfex.IsExportType(key.TypeCode)
                ? Wsfex.ReadFind(await Invoke(s, Wsfex.Service, "FEXGetCMP", mutating: false, ct, t => Wsfex.BuildFind(t, s.Cuit, key)).ConfigureAwait(false), key)
                : Wsfe.ReadFind(await Invoke(s, Wsfe.Service, "FECompConsultar", mutating: false, ct, t => FindRequest(t, s.Cuit, key)).ConfigureAwait(false), key);
            return ModuleResponse.Completed(result, s.StateIfChanged());
        });
    }

    public async Task<ModuleResponse> ExchangeRate(ModuleCall call)
    {
        var input = Inputs.Read<ExchangeRateInput>(call);
        if (input.Currency is not { Length: 3 } currency)
        {
            throw Fail.InvalidInput(["currency"]);
        }

        return await WithSession(call, async s =>
        {

            // Los tipos de exportación se cotizan en WSFEX, con la base aduanera. Sin tipo, o con otro, en WSFEv1.
            var export = input.TypeCode is { } type && Wsfex.IsExportType(type);

            async Task<JsonObject> Quote(DateOnly? day) => export
                ? Wsfex.ReadExchangeRate(await Invoke(s, Wsfex.Service, "FEXGetPARAM_Ctz", mutating: false, call.Aborted, t => Wsfex.BuildExchangeRate(t, s.Cuit, currency, day)).ConfigureAwait(false), currency)
                : Wsfe.ReadExchangeRate(await Invoke(s, Wsfe.Service, "FEParamGetCotizacion", mutating: false, call.Aborted, t => Wsfe.Request("FEParamGetCotizacion", t, s.Cuit, Wsfe.E("MonId", currency), day is null ? null : Wsfe.E("FchCotiz", Fmt.Ymd(day)))).ConfigureAwait(false), currency);

            // Sin fecha es una sola consulta: ARCA devuelve la última cotización.
            if (input.Date is not { } date)
            {
                return ModuleResponse.Completed(await Quote(null).ConfigureAwait(false), s.StateIfChanged());
            }

            // El núcleo emite todos los días y ARCA no cotiza fines de semana ni feriados: se prueba la fecha pedida y, mientras ARCA
            // diga que no hay cotización, el día anterior, hasta MaxDaysBack días atrás. Todos los intentos usan la misma sesión,
            // o sea el mismo ticket. La fecha que se devuelve es la que informa ARCA para la cotización que encontró, no la pedida.
            ModuleException? last = null;
            for (var back = 0; back <= MaxDaysBack && back <= date.DayNumber; back++)
            {
                try
                {
                    var quote = await Quote(DateOnly.FromDayNumber(date.DayNumber - back)).ConfigureAwait(false);
                    return ModuleResponse.Completed(quote, s.StateIfChanged());
                }
                catch (ModuleException ex) when (LacksQuote(ex))
                {
                    last = ex;
                }
            }

            // Con la respuesta del último intento, para saber si ARCA decía "sin datos" o rechazaba por otra cosa.
            throw new ModuleException(
                ErrorCodes.NotFound,
                $"ARCA no tiene cotización de {currency} para el {Fmt.Iso(date)} ni para los {MaxDaysBack} días anteriores. Último intento: {last?.Message}",
                details: last?.Details);
        });
    }

    /// <summary>
    /// Si la respuesta de ARCA quiere decir "no hay cotización para esa fecha", que es cuando se prueba el día anterior. ARCA no
    /// documenta qué devuelve en ese caso, así que valen el not_found (602 de WSFEv1, cotización vacía o en cero) y cualquier
    /// rechazo de negocio. Lo demás corta: la configuración, ARCA caído, los errores reintentables y un rechazo por la moneda,
    /// que con otro día sería el mismo.
    /// </summary>
    private static bool LacksQuote(ModuleException ex) => ex.Code switch
    {
        ErrorCodes.NotFound => true,
        ErrorCodes.Rejected => !IsCurrencyRejection(ex),
        _ => false,
    };

    /// <summary>12000 y 12001 de WSFEv1: la moneda no es una de las habilitadas o falta (manual 4.7, FEParamGetCotizacion).</summary>
    private static bool IsCurrencyRejection(ModuleException ex) =>
        ex.Details?["upstream"] is JsonArray upstream && upstream.Any(u => u?["code"]?.GetValue<int>() is 12000 or 12001);

    public async Task<ModuleResponse> Authorize(ModuleCall call)
    {
        var input = Inputs.Read<AuthorizeInput>(call);
        var doc = input.Document ?? throw Fail.InvalidInput(["document"]);
        var export = Wsfex.IsExportType(doc.TypeCode);
        var bad = export ? Wsfex.Validate(input) : Wsfe.Validate(doc);
        if (bad.Count > 0)
        {
            throw Fail.InvalidInput(bad);
        }

        return await WithSession(call, async s =>
        {

            // No se corta cuando el núcleo se desconecta: con el pedido ya enviado, abandonarlo dejaría el resultado
            // sin guardar. Si se termina, la SDK lo registra por la clave de idempotencia y el reintento lo recupera.
            var ct = CancellationToken.None;
            JsonObject result;
            if (export)
            {
                var response = await Invoke(s, Wsfex.Service, "FEXAuthorize", mutating: true, ct, t => Wsfex.BuildAuthorize(t, s.Cuit, input)).ConfigureAwait(false);
                result = Wsfex.ReadAuthorize(response, input);
            }
            else
            {
                var response = await Invoke(s, Wsfe.Service, "FECAESolicitar", mutating: true, ct, t => Wsfe.BuildAuthorize(t, s.Cuit, doc)).ConfigureAwait(false);
                result = Wsfe.ReadAuthorize(response, doc);
            }

            return ModuleResponse.Completed(result, s.StateIfChanged());
        });
    }

    // ---- Llamadas a ARCA ----

    /// <summary>
    /// Abre la sesión y corre la operación. Si falla después de un login nuevo, el ticket viaja en el error como
    /// instance_state: el núcleo lo guarda igual, y la llamada siguiente, en esta réplica o en otra, no vuelve a WSAA.
    /// </summary>
    private async Task<ModuleResponse> WithSession(ModuleCall call, Func<ArcaSession, Task<ModuleResponse>> work)
    {
        using var s = ArcaSession.Open(call, production);
        try
        {
            return await work(s).ConfigureAwait(false);
        }
        catch (ModuleException ex) when (s.Changed)
        {
            ex.InstanceState ??= s.StateIfChanged();
            throw;
        }
    }

    private static XElement FindRequest(Ticket ticket, string cuit, DocumentRef key) =>
        Wsfe.Request(
            "FECompConsultar",
            ticket,
            cuit,
            new XElement(Wsfe.Ns + "FeCompConsReq", Wsfe.E("CbteTipo", key.TypeCode), Wsfe.E("CbteNro", key.Number), Wsfe.E("PtoVta", key.PointOfSale)));

    private static void RequireKey(DocumentRef key, bool withNumber)
    {
        var bad = new List<string>();
        if (key.PointOfSale is < 1 or > 99998)
        {
            bad.Add("point_of_sale");
        }

        if (key.TypeCode <= 0)
        {
            bad.Add("type_code");
        }

        if (withNumber && key.Number < 1)
        {
            bad.Add("number");
        }

        if (bad.Count > 0)
        {
            throw Fail.InvalidInput(bad);
        }
    }

    /// <summary>Llama a una operación de WSFEv1 o de WSFEX con el ticket del servicio y devuelve su elemento Result.</summary>
    private async Task<XElement> Invoke(ArcaSession s, string service, string operation, bool mutating, CancellationToken ct, Func<Ticket, XElement> build)
    {
        // Antes del ticket: en modo read una emisión no llega ni al login de WSAA.
        s.Guard(operation);
        var ticket = await GetTicket(s, service, ct).ConfigureAwait(false);
        var export = service == Wsfex.Service;
        var ns = export ? Wsfex.Ns : Wsfe.Ns;
        var url = export ? s.Endpoints.Wsfex : s.Endpoints.Wsfe;
        var response = await Post(s, url, ns.NamespaceName + operation, build(ticket), mutating, ct).ConfigureAwait(false);
        return ResultOf(response, operation, mutating);
    }

    /// <summary>
    /// Manda el pedido. Si ARCA no responde, en una operación que emite el resultado es incierto (el comprobante pudo
    /// haberse autorizado) salvo que el corte sea antes de enviar. En las de consulta no hay riesgo en repetir. Es el
    /// único camino hacia ARCA fuera del login, por eso acá se aplica la traba de solo lectura.
    /// </summary>
    private async Task<XElement> Post(ArcaSession s, Uri url, string action, XElement body, bool mutating, CancellationToken ct)
    {
        s.Guard(body.Name.LocalName);
        try
        {
            return await soap.Call(url, action, body, ct).ConfigureAwait(false);
        }
        catch (SoapException ex) when (mutating && ex.Sent)
        {
            logger.LogWarning("Sin respuesta confiable de ARCA para {Action}: {Reason}", action, ex.Message);
            throw Fail.Unknown(
                $"ARCA no confirmó el pedido: {ex.Message} Puede haberse procesado, hay que consultar el comprobante antes de repetir.",
                "no_response");
        }
        catch (SoapException ex)
        {
            throw new ModuleException(ErrorCodes.UpstreamUnavailable, ex.Message, 30);
        }
    }

    private static XElement ResultOf(XElement response, string operation, bool mutating) =>
        response.Child(operation + "Result")
        ?? throw (mutating
            ? Fail.Unknown("ARCA devolvió una respuesta inesperada.", "unexpected_response")
            : new ModuleException(ErrorCodes.UpstreamUnavailable, $"ARCA devolvió una respuesta inesperada a {operation}.", 30));

    // ---- WSAA ----

    /// <summary>
    /// El ticket del servicio: el del estado de la instancia o el último que se obtuvo mientras sirva, y si no, uno
    /// nuevo de WSAA. WSAA no da otro ticket mientras el anterior siga vigente, por eso nunca se pide de más. Con
    /// <paramref name="freshLogin"/> se ignoran los que hay y se pide uno nuevo con el certificado de esta llamada.
    /// </summary>
    private async Task<Ticket> GetTicket(ArcaSession s, string service, CancellationToken ct, bool freshLogin = false)
    {
        s.Guard("loginCms");
        var now = time.GetUtcNow();
        var key = $"{s.Cuit}/{s.Environment}/{service}";
        s.Tickets.TryGetValue(service, out var saved);
        recent.TryGetValue(key, out var kept);
        var best = saved is not null && kept is not null ? (saved.ExpiresAt >= kept.ExpiresAt ? saved : kept) : saved ?? kept;
        if (!freshLogin && best is not null && best.ExpiresAt - now > RenewMargin)
        {
            return Use(best);
        }

        string cms;
        try
        {
            cms = Wsaa.Sign(Wsaa.BuildTra(service, now), s.Certificate);
        }
        catch (CryptographicException)
        {
            throw Fail.InvalidConfig(["certificate_pem", "private_key_pem"], "No se pudo firmar con el certificado y la clave privada.");
        }

        XElement response;
        try
        {
            response = await soap.Call(s.Endpoints.Wsaa, string.Empty, Wsaa.LoginRequest(cms), ct).ConfigureAwait(false);
        }
        catch (SoapException ex) when (!freshLogin && Wsaa.IsAlreadyAuthenticated(ex) && best is not null && best.ExpiresAt > now)
        {
            // Falta poco para que venza y WSAA todavía no deja pedir otro: se usa el que queda. Con fresh no vale,
            // porque el que queda puede ser del certificado anterior.
            return Use(best);
        }
        catch (SoapException ex)
        {
            logger.LogWarning("WSAA no entregó ticket para {Service} ({Environment}): {Reason}", service, s.Environment, ex.Message);
            throw Wsaa.MapFailure(ex, s.Environment, service);
        }

        var fresh = Wsaa.ParseTicket(response, s.Cuit, s.Environment, now);
        recent[key] = fresh;
        logger.LogInformation("Ticket nuevo de WSAA para {Service} ({Environment}), vence {Expires:O}", service, s.Environment, fresh.ExpiresAt);
        return Use(fresh);

        Ticket Use(Ticket ticket)
        {
            if (!s.Tickets.TryGetValue(service, out var current) || current != ticket)
            {
                s.Tickets[service] = ticket;
                s.Changed = true;
            }

            return ticket;
        }
    }
}
