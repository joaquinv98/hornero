using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Fiscal;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.App.UseCases;

/// <summary>Datos del emisor del paso 2 del asistente: lo que decide la clase de comprobante y lo que pide el impreso (RG 1415, anexo II).</summary>
public sealed record IssuerInput(
    string TaxId,
    string LegalName,
    int VatCondition,
    bool ClassAWithLegend = false,
    string? Address = null,
    string? GrossIncomeNumber = null,
    DateOnly? ActivityStart = null,
    bool Exports = false,
    bool UsesRegistry = false);

/// <summary>Lo que deja el paso 7: las series creadas, los avisos y el estado de las dos trabas de producción, que el asistente no prende.</summary>
public sealed record SetupSummary(string Environment, IReadOnlyList<string> SeriesCreated, IReadOnlyList<string> Warnings, bool CoreProductionEnabled, bool? ModuleAuthorizeEnabled);

/// <summary>Lo que el padrón informa de un contribuyente (constancia de inscripción), sin caracterizaciones.</summary>
public sealed record TaxpayerInfo(
    string TaxId,
    string Name,
    string? PersonType,
    string? Street,
    string? City,
    string? PostalCode,
    string? Province,
    IReadOnlyList<string> Taxes,
    string? MonotaxCategory,
    int? SuggestedVatCondition,
    bool Blocked,
    IReadOnlyList<string> Notes)
{
    public static TaxpayerInfo From(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var address = node["address"];
        return new TaxpayerInfo(
            node["tax_id"]?.GetValue<string>() ?? string.Empty,
            node["name"]?.GetValue<string>() ?? string.Empty,
            node["person_type"]?.GetValue<string>(),
            address?["street"]?.GetValue<string>(),
            address?["city"]?.GetValue<string>(),
            address?["postal_code"]?.GetValue<string>(),
            address?["province"]?.GetValue<string>(),
            [.. (node["taxes"] as JsonArray ?? []).Select(t => t?["description"]?.GetValue<string>()).OfType<string>()],
            node["monotax_category"]?.GetValue<string>(),
            node["suggested_vat_condition"]?.GetValue<int>(),
            node["blocked"]?.GetValue<bool>() == true,
            [.. (node["notes"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>()]);
    }
}

public enum CheckLevel
{
    Ok,
    Warning,
    Error,
}

/// <summary>Un control del certificado cargado (sección 2.5 del diseño fiscal), con su mensaje en castellano.</summary>
public sealed record CertificateCheck(string Code, CheckLevel Level, string Message);

/// <summary>Un punto de la prueba sin emitir, con su remedio cuando falla.</summary>
public sealed record TestItem(string Step, bool Ok, string Message, string? Remedy = null);

/// <summary>Resultado de la prueba sin emitir (sección 2.6 del diseño fiscal). Se guarda en el emisor y se repite desde su ficha.</summary>
public sealed record FiscalTestReport(
    DateTimeOffset At,
    bool Ok,
    IReadOnlyList<TestItem> Items,
    JsonArray? VatRates = null,
    JsonArray? PointsOfSale = null,
    JsonArray? TaxTypes = null,
    JsonArray? ReceiverConditions = null,
    bool? AuthorizeEnabled = null)
{
    public string ToJson() => JsonSerializer.Serialize(this, ModuleJson.Options);

    public static FiscalTestReport? Parse(string? json) => json is null ? null : JsonSerializer.Deserialize<FiscalTestReport>(json, ModuleJson.Options);
}

/// <summary>Clave RSA y pedido de certificado con el sujeto que pide ARCA (sección 2.4 del diseño fiscal), con la biblioteca estándar.</summary>
public static partial class CsrBuilder
{
    public const string DefaultAlias = "billingoss";

    /// <summary>Solo letras minúsculas y números, como el ejemplo del manual de WSAA.</summary>
    public static bool IsValidAlias(string alias) => alias is { Length: >= 1 and <= 50 } && AliasPattern().IsMatch(alias);

    /// <summary>
    /// Clave de 2048 bits y pedido con C=AR, O=razón social, CN=alias y serialNumber=CUIT con la palabra, un espacio y
    /// los 11 dígitos, como PrintableString. Devuelve la clave en PKCS#8 PEM, sin contraseña, y el pedido en PEM.
    /// </summary>
    public static (string PrivateKeyPem, string CsrPem) Create(string alias, string taxId, string organization)
    {
        if (!IsValidAlias(alias))
        {
            throw new ValidationException("El alias lleva solo letras minúsculas y números, sin acentos ni espacios.");
        }

        if (!TaxIdentity.IsValidCuit(taxId))
        {
            throw new ValidationException($"CUIT inválida: {taxId}.");
        }

        if (string.IsNullOrWhiteSpace(organization) || organization.Any(c => c > 127))
        {
            throw new ValidationException("La razón social va sin acentos ni eñes: los ejemplos de ARCA son ASCII.");
        }

        using var rsa = RSA.Create(2048);
        var name = new X500DistinguishedNameBuilder();
        name.AddCountryOrRegion("AR");
        name.AddOrganizationName(organization.Trim());
        name.AddCommonName(alias);
        name.Add("2.5.4.5", $"CUIT {taxId}", UniversalTagNumber.PrintableString);
        var request = new CertificateRequest(name.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return (rsa.ExportPkcs8PrivateKeyPem(), request.CreateSigningRequestPem());
    }

    [GeneratedRegex("^[a-z0-9]+$")]
    private static partial Regex AliasPattern();
}

/// <summary>Los controles del certificado que da el fisco, cada uno con su mensaje (sección 2.5 del diseño fiscal).</summary>
public static partial class CertificateChecks
{
    /// <summary>Lee un certificado en PEM, DER o PKCS#12. Lo que no se lee como X.509 sale como validación.</summary>
    public static X509Certificate2 Load(byte[] data, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var text = Encoding.UTF8.GetString(data);
        try
        {
            if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            {
                return X509Certificate2.CreateFromPem(text);
            }

            return password is null ? X509CertificateLoader.LoadCertificate(data) : X509CertificateLoader.LoadPkcs12(data, password);
        }
        catch (CryptographicException)
        {
            // DER que no es un certificado suelto: puede ser un PKCS#12 sin contraseña.
            try
            {
                return X509CertificateLoader.LoadPkcs12(data, password);
            }
            catch (CryptographicException)
            {
                throw new ValidationException("Esto no es un certificado. Bajalo de nuevo desde ARCA con el botón Ver del alias.");
            }
        }
    }

    /// <summary>CUIT y alias del sujeto: serialNumber (2.5.4.5) con la forma "CUIT 11 dígitos" y el nombre común.</summary>
    public static (string? TaxId, string? Alias) Subject(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        string? taxId = null;
        string? alias = null;
        foreach (var rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            var value = rdn.GetSingleElementValue();
            switch (rdn.GetSingleElementType().Value)
            {
                case "2.5.4.5":
                    taxId = value is not null && SerialPattern().Match(value) is { Success: true } m ? m.Groups[1].Value : value;
                    break;
                case "2.5.4.3":
                    alias = value;
                    break;
                default:
                    break;
            }
        }

        return (taxId, alias);
    }

    public static IReadOnlyList<CertificateCheck> Run(FiscalCredential credential, X509Certificate2 certificate, RSA? storedKey, string issuerTaxId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(certificate);
        var checks = new List<CertificateCheck>();
        if (storedKey is not null && !certificate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(storedKey.ExportSubjectPublicKeyInfo()))
        {
            checks.Add(new("key_mismatch", CheckLevel.Error, $"Este certificado es de otro pedido. Usá el que te dio ARCA para el alias {credential.Alias} con el pedido de este asistente."));
        }

        var (taxId, alias) = Subject(certificate);
        if (taxId is null || !TaxIdentity.IsValidCuit(taxId))
        {
            checks.Add(new("cuit_invalid", CheckLevel.Error, "El certificado no trae una CUIT válida."));
        }
        else if (taxId != issuerTaxId)
        {
            checks.Add(new("cuit_other", CheckLevel.Warning, $"El certificado es de la CUIT {taxId} y vas a facturar con la {issuerTaxId}. Funciona solo si {issuerTaxId} autorizó a {taxId} en ARCA. En homologación es lo normal."));
        }

        if (alias != credential.Alias)
        {
            checks.Add(new("alias_other", CheckLevel.Warning, $"El alias del certificado es {alias ?? "(vacío)"} y el del pedido es {credential.Alias}."));
        }

        var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
        if (notBefore > now)
        {
            checks.Add(new("not_yet_valid", CheckLevel.Error, $"El certificado recién vale desde el {notBefore:dd/MM/yyyy}."));
        }
        else if (notAfter < now)
        {
            checks.Add(new("expired", CheckLevel.Error, $"El certificado venció el {notAfter:dd/MM/yyyy}. Generá un pedido nuevo con Renovar."));
        }
        else if ((notAfter - now).TotalDays < 30)
        {
            checks.Add(new("expires_soon", CheckLevel.Warning, $"Vence en {(int)(notAfter - now).TotalDays} días."));
        }

        checks.Add(new("issuer", CheckLevel.Ok, $"Emitido por {certificate.Issuer}. El ambiente no se deduce del certificado: si es del otro, la prueba sin emitir lo detecta."));
        return checks;
    }

    [GeneratedRegex(@"^CUIT\s*(\d{11})$")]
    private static partial Regex SerialPattern();
}

/// <summary>
/// Alta guiada de ARCA (sección 2 del diseño fiscal): credenciales con clave y pedido generados en el servidor,
/// carga y validación del certificado, activación con reemplazo de los secretos de la instancia, prueba sin emitir,
/// padrón, renovación probada y avisos de vencimiento. Nunca llama a una operación que autorice un comprobante.
/// </summary>
public sealed class FiscalSetupService(BillingDbContext db, ModuleRegistry registry, ModuleClient client, SecretBox box, AuditLog audit, TimeProvider time, BillingCore core, IOptions<FiscalOptions> fiscal)
{
    private const string CredentialsTable = "fiscal.credentials";

    /// <summary>Días antes del vencimiento en que se avisa al staff.</summary>
    public static IReadOnlyList<int> NoticeDays { get; } = [30, 15, 7, 1];

    /// <summary>
    /// Tipos de comprobante por clase (tabla de FEParamGetTiposCbte): A 1, 2 y 3 (o 51, 52 y 53 con leyenda), B 6, 7 y 8,
    /// C 11, 12 y 13 y E 19, 20 y 21. Un responsable inscripto emite A y B, un monotributista o exento emite C, y E se
    /// suma si exporta.
    /// </summary>
    public static string DefaultTypeMap(int vatCondition, bool classAWithLegend, bool exports)
    {
        var map = new JsonObject();
        if (VatConditions.IssuesClassC(vatCondition))
        {
            map["C"] = Types(11, 12, 13);
        }
        else
        {
            map["A"] = classAWithLegend ? Types(51, 52, 53) : Types(1, 2, 3);
            map["B"] = Types(6, 7, 8);
        }

        if (exports)
        {
            map["E"] = Types(19, 20, 21);
        }

        return map.ToJsonString();

        static JsonObject Types(int invoice, int debit, int credit) => new() { ["Invoice"] = invoice, ["DebitNote"] = debit, ["CreditNote"] = credit };
    }

    /// <summary>
    /// El emisor que trabaja el asistente: el que está en preparación (inactivo y sin terminar, el del otro ambiente),
    /// si no el activo. Sin ninguno activo, que es el corte de una migración, el que dejó el importador: trae la
    /// numeración, el momento de emisión y los ajustes del sistema anterior, y la prueba compara sus series con las de
    /// ARCA (D-17). Si no hay ninguno, uno nuevo y vacío.
    /// </summary>
    public async Task<IssuerProfile> EnsureIssuer(CancellationToken ct = default)
    {
        var issuer = await InSetup(ct).ConfigureAwait(false)
            ?? await db.IssuerProfiles.SingleOrDefaultAsync(i => i.Active, ct).ConfigureAwait(false)
            ?? await db.IssuerProfiles.Where(i => !i.Active && i.SetupStep < 7).OrderByDescending(i => i.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (issuer is null)
        {
            issuer = new IssuerProfile { TypeMap = DefaultTypeMap(VatConditions.ResponsableInscripto, false, false) };
            db.IssuerProfiles.Add(issuer);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return issuer;
    }

    /// <summary>
    /// El emisor en preparación para el otro ambiente, si lo hay: inactivo, con el asistente sin terminar y todavía sin
    /// series ni comprobantes. El que deja el importador queda afuera: guarda la numeración y los comprobantes del
    /// sistema anterior, no frena el pase a producción y, mientras haya un emisor activo, el asistente no lo toma.
    /// </summary>
    public Task<IssuerProfile?> InSetup(CancellationToken ct = default) =>
        db.IssuerProfiles.Where(i => !i.Active && i.SetupStep < 7 && !db.FiscalSeries.Any(s => s.IssuerId == i.Id) && !db.FiscalDocuments.Any(f => f.IssuerId == i.Id))
            .OrderByDescending(i => i.Id).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Otro emisor para pasar al otro ambiente (sección 2.10 del diseño fiscal): copia los datos del impreso y la
    /// configuración del actual, sin instancia, series ni comprobantes, y queda inactivo hasta que el asistente
    /// termine. Al terminar reemplaza al activo: homologación y producción nunca comparten emisor. Sale solo del activo
    /// de homologación, como ofrece la ficha: el nuevo no toma lo emitido antes, y en producción eso es real.
    /// </summary>
    public async Task<IssuerProfile> StartNewIssuer(long fromIssuerId, CancellationToken ct = default)
    {
        if (await InSetup(ct).ConfigureAwait(false) is not null)
        {
            throw new ValidationException("Ya hay un emisor en preparación: seguí ese asistente hasta activarlo.");
        }

        var from = await db.IssuerProfiles.AsNoTracking().SingleAsync(i => i.Id == fromIssuerId, ct).ConfigureAwait(false);
        var instance = from.ModuleInstanceId is { } instanceId ? await db.ModuleInstances.AsNoTracking().SingleAsync(i => i.Id == instanceId, ct).ConfigureAwait(false) : null;
        if (!from.Active || instance is null || Environment(instance) != FiscalEnvironments.Homologation)
        {
            throw new ValidationException("Solo el emisor activo de homologación pasa a producción: lo que emite uno de producción es real y sigue saliendo con él.");
        }

        return await CopyOf(from, ct).ConfigureAwait(false);
    }

    /// <summary>Otro emisor con los datos del impreso y la configuración de este, sin instancia, series ni comprobantes, inactivo.</summary>
    private async Task<IssuerProfile> CopyOf(IssuerProfile from, CancellationToken ct)
    {
        var issuer = new IssuerProfile
        {
            TaxId = from.TaxId,
            LegalName = from.LegalName,
            VatCondition = from.VatCondition,
            ClassAWithLegend = from.ClassAWithLegend,
            Address = from.Address,
            GrossIncomeNumber = from.GrossIncomeNumber,
            ActivityStart = from.ActivityStart,
            Exports = from.Exports,
            UsesRegistry = from.UsesRegistry,
            PointOfSale = from.PointOfSale,
            ExportPointOfSale = from.ExportPointOfSale,
            TypeMap = from.TypeMap,
            ClassAConditions = from.ClassAConditions,
            Moment = from.Moment,
            Settings = from.Settings,
            Active = false,
        };
        db.IssuerProfiles.Add(issuer);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("fiscal.issuer.new", $"issuer:{issuer.Id}", new { from = from.Id }, ct).ConfigureAwait(false);
        return issuer;
    }

    /// <summary>Si el emisor ya tiene series o comprobantes, propios o del sistema anterior.</summary>
    private async Task<bool> Used(long issuerId, CancellationToken ct) =>
        await db.FiscalSeries.AnyAsync(s => s.IssuerId == issuerId, ct).ConfigureAwait(false)
        || await db.FiscalDocuments.AnyAsync(f => f.IssuerId == issuerId, ct).ConfigureAwait(false);

    /// <summary>
    /// Paso 2: valida la CUIT con su dígito verificador, acepta solo las condiciones que un emisor puede tener y arma el
    /// mapa de tipos de comprobante según la condición, salvo que el emisor ya tenga uno y la condición no cambie.
    /// </summary>
    public async Task SaveIssuer(long issuerId, IssuerInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!TaxIdentity.IsValidCuit(input.TaxId))
        {
            throw new ValidationException($"CUIT inválida: {input.TaxId}.");
        }

        if (string.IsNullOrWhiteSpace(input.LegalName))
        {
            throw new ValidationException("Falta la razón social.");
        }

        if (!VatConditions.IssuerConditions.Contains(input.VatCondition))
        {
            throw new ValidationException("La condición del emisor es responsable inscripto, monotributista o exento.");
        }

        var issuer = await db.IssuerProfiles.SingleAsync(i => i.Id == issuerId, ct).ConfigureAwait(false);
        var remap = issuer.TypeMap is "{}" || issuer.VatCondition != input.VatCondition || issuer.ClassAWithLegend != input.ClassAWithLegend || issuer.Exports != input.Exports;
        issuer.TaxId = input.TaxId;
        issuer.LegalName = input.LegalName.Trim();
        issuer.VatCondition = input.VatCondition;
        issuer.ClassAWithLegend = input.ClassAWithLegend;
        issuer.Address = input.Address;
        issuer.GrossIncomeNumber = input.GrossIncomeNumber;
        issuer.ActivityStart = input.ActivityStart;
        issuer.Exports = input.Exports;
        issuer.UsesRegistry = input.UsesRegistry;
        if (remap)
        {
            issuer.TypeMap = DefaultTypeMap(input.VatCondition, input.ClassAWithLegend, input.Exports);
        }

        issuer.SetupStep = Math.Max(issuer.SetupStep, 2);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // La instancia del módulo lleva la CUIT del emisor en su configuración visible: se mantiene a la par.
        if (issuer.ModuleInstanceId is { } instanceId)
        {
            var instance = await db.ModuleInstances.AsNoTracking().SingleAsync(i => i.Id == instanceId, ct).ConfigureAwait(false);
            var config = JsonNode.Parse(instance.Config)?.AsObject() ?? [];
            if (config["cuit"]?.ToString() != input.TaxId)
            {
                config["cuit"] = input.TaxId;
                await registry.ChangeConfig(instanceId, config.ToJsonString(), ct).ConfigureAwait(false);
            }
        }

        await audit.Record("fiscal.issuer.save", $"issuer:{issuerId}", new { input.TaxId, input.VatCondition, input.Exports, input.UsesRegistry }, ct).ConfigureAwait(false);
    }

    /// <summary>Hasta qué paso llegó el asistente, que se puede cortar y retomar.</summary>
    public async Task SetStep(long issuerId, int step, CancellationToken ct = default)
    {
        var issuer = await db.IssuerProfiles.SingleAsync(i => i.Id == issuerId, ct).ConfigureAwait(false);
        issuer.SetupStep = Math.Clamp(step, 0, 7);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Instalaciones de módulos fiscales (las que declaran el tipo fiscal en su manifiesto), para elegir dónde crear la instancia.</summary>
    public async Task<IReadOnlyList<ModuleInstallation>> FiscalInstallations(CancellationToken ct = default)
    {
        var installations = await db.ModuleInstallations.AsNoTracking().OrderBy(i => i.Id).ToListAsync(ct).ConfigureAwait(false);
        return [.. installations.Where(i => (JsonNode.Parse(i.Manifest)?["kinds"] as JsonArray)?.Any(k => k?.ToString() == "fiscal") == true)];
    }

    /// <summary>Instancias fiscales existentes, con su ambiente, para asignar una al emisor.</summary>
    public async Task<IReadOnlyList<(ModuleInstance Instance, string Environment)>> FiscalInstances(CancellationToken ct = default)
    {
        var installations = (await FiscalInstallations(ct).ConfigureAwait(false)).Select(i => i.Id).ToHashSet();
        var instances = await db.ModuleInstances.AsNoTracking().OrderBy(i => i.Id).ToListAsync(ct).ConfigureAwait(false);
        return [.. instances.Where(i => installations.Contains(i.InstallationId)).Select(i => (i, Environment(i)))];
    }

    /// <summary>
    /// Paso 1: ambiente, exportación y padrón, y la instancia fiscal del emisor: una existente del mismo ambiente, o
    /// una nueva en una instalación fiscal con la CUIT del emisor y el ambiente, que no cambia más (sección 2.10).
    /// El emisor del importador trae la numeración real del sistema anterior: en homologación (el staging, por
    /// ejemplo) el asistente sigue con otro emisor con sus datos y sin series, y aquel queda inactivo para el corte.
    /// </summary>
    public async Task Start(long issuerId, string environment, bool exports, bool usesRegistry, long? instanceId, long? installationId, CancellationToken ct = default)
    {
        if (!FiscalEnvironments.IsValid(environment))
        {
            throw new ValidationException("El ambiente es homologation o production.");
        }

        var issuer = await db.IssuerProfiles.SingleAsync(i => i.Id == issuerId, ct).ConfigureAwait(false);
        if (environment == FiscalEnvironments.Homologation && issuer is { Active: false, ModuleInstanceId: null } && await Used(issuer.Id, ct).ConfigureAwait(false))
        {
            issuer = await InSetup(ct).ConfigureAwait(false) ?? await CopyOf(issuer, ct).ConfigureAwait(false);
        }

        issuer.Exports = exports;
        issuer.UsesRegistry = usesRegistry;
        if (issuer.TypeMap is not "{}")
        {
            issuer.TypeMap = DefaultTypeMap(issuer.VatCondition, issuer.ClassAWithLegend, exports);
        }

        if (instanceId is null && installationId is { } install)
        {
            if (!(await FiscalInstallations(ct).ConfigureAwait(false)).Any(i => i.Id == install))
            {
                throw new ValidationException("Esa instalación no es de un módulo fiscal.");
            }

            var config = new JsonObject { ["cuit"] = issuer.TaxId, ["environment"] = environment };
            instanceId = (await registry.AddInstance(install, $"arca-{environment}", config.ToJsonString(), null, ct).ConfigureAwait(false)).Id;
        }

        if (instanceId is { } chosen)
        {
            var instance = await db.ModuleInstances.AsNoTracking().SingleAsync(i => i.Id == chosen, ct).ConfigureAwait(false);
            if (Environment(instance) != environment)
            {
                throw new ValidationException($"La instancia {instance.Name} es de {Environment(instance)} y el asistente está en {environment}.");
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AssignInstance(issuer.Id, chosen, ct).ConfigureAwait(false);
        }

        issuer.SetupStep = Math.Max(issuer.SetupStep, 1);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Los puntos de venta del emisor, elegidos de la lista que informó ARCA en la prueba o escritos a mano.</summary>
    public async Task SetPointsOfSale(long issuerId, int pointOfSale, int? exportPointOfSale, CancellationToken ct = default)
    {
        if (pointOfSale <= 0 || exportPointOfSale is <= 0)
        {
            throw new ValidationException("El punto de venta es un número mayor que cero.");
        }

        var issuer = await db.IssuerProfiles.SingleAsync(i => i.Id == issuerId, ct).ConfigureAwait(false);
        if (await db.FiscalSeries.AnyAsync(s => s.IssuerId == issuerId && s.PointOfSale == issuer.PointOfSale, ct).ConfigureAwait(false) && issuer.PointOfSale != pointOfSale)
        {
            throw new ValidationException($"El punto de venta {issuer.PointOfSale} ya tiene series: no se cambia desde el asistente.");
        }

        issuer.PointOfSale = pointOfSale;
        issuer.ExportPointOfSale = exportPointOfSale;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("fiscal.issuer.points_of_sale", $"issuer:{issuerId}", new { pointOfSale, exportPointOfSale }, ct).ConfigureAwait(false);
    }

    /// <summary>La credencial activa del emisor en el ambiente de su instancia, si la hay.</summary>
    public Task<FiscalCredential?> ActiveCredential(long issuerId, string environment, CancellationToken ct = default) =>
        db.FiscalCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.IssuerId == issuerId && c.Environment == environment && c.Status == CredentialStatus.Active, ct);

    public static string EnvironmentOf(ModuleInstance instance) => Environment(instance);

    /// <summary>
    /// Paso 7: con la prueba sin emitir pasada, crea las series que falten con el último número que informa ARCA. Si un
    /// punto de venta nuevo ya tiene comprobantes, otro sistema lo usa y tiene que dejar de hacerlo, porque cada punto
    /// es exclusivo. Las trabas de producción no se tocan: se devuelve su estado para mostrarlo.
    /// </summary>
    public async Task<SetupSummary> Finish(long issuerId, CancellationToken ct = default)
    {
        var issuer = await db.IssuerProfiles.SingleAsync(i => i.Id == issuerId, ct).ConfigureAwait(false);
        var report = FiscalTestReport.Parse(issuer.LastTest);
        if (report?.Ok != true)
        {
            throw new ValidationException("Antes de activar hay que pasar la prueba sin emitir.");
        }

        var module = await registry.Resolve(issuer.ModuleInstanceId ?? throw new ValidationException("El emisor no tiene instancia fiscal."), ct).ConfigureAwait(false);
        var created = new List<string>();
        var warnings = new List<string>();
        var map = JsonNode.Parse(issuer.TypeMap)?.AsObject() ?? [];
        foreach (var (letter, types) in map)
        {
            var pointOfSale = letter == "E" ? issuer.ExportPointOfSale ?? issuer.PointOfSale : issuer.PointOfSale;
            foreach (var (_, code) in types?.AsObject() ?? [])
            {
                var typeCode = code!.GetValue<int>();
                if (await db.FiscalSeries.AnyAsync(s => s.IssuerId == issuer.Id && s.PointOfSale == pointOfSale && s.TypeCode == typeCode, ct).ConfigureAwait(false))
                {
                    continue;
                }

                var result = await client.Call(module.Endpoint, "/v0/fiscal/last_number", new ModuleRequest(module.Data, [], new JsonObject { ["point_of_sale"] = pointOfSale, ["type_code"] = typeCode }), null, module.Timeout("last_number"), ct).ConfigureAwait(false);
                if (result.InstanceState is { } state)
                {
                    module = await registry.SaveState(module, state, ct).ConfigureAwait(false);
                }

                if (result.Error is { } error)
                {
                    throw new ValidationException($"ARCA no informó el último número del punto {pointOfSale} tipo {typeCode}: {error.Message}");
                }

                var number = result.Response!.Result!["number"]!.GetValue<long>();
                var series = new FiscalSeries(issuer.Id, pointOfSale, typeCode) { LastNumber = number };
                if (result.Response.Result["date"]?.GetValue<string>() is { } date)
                {
                    series.LastDate = DateOnly.Parse(date, CultureInfo.InvariantCulture);
                }

                db.FiscalSeries.Add(series);
                created.Add(string.Create(CultureInfo.InvariantCulture, $"{pointOfSale}-{typeCode} desde {number}"));
                if (number > 0)
                {
                    warnings.Add(string.Create(CultureInfo.InvariantCulture, $"El punto de venta {pointOfSale} ya tiene {number} comprobantes del tipo {typeCode} en ARCA: otro sistema lo usa y tiene que dejar de hacerlo, porque cada punto es exclusivo."));
                }
            }
        }

        // Un solo emisor activo: el que termina reemplaza al anterior, que conserva sus series y comprobantes. Lo emitido
        // mientras regía el anterior no sale con este (sección 2.10 del diseño fiscal).
        var previous = await db.IssuerProfiles.Where(i => i.Active && i.Id != issuer.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var other in previous)
        {
            other.Active = false;
        }

        if (previous.Count > 0)
        {
            issuer.ActiveSince = time.GetUtcNow();
        }

        issuer.SetupStep = 7;
        issuer.Active = true;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("fiscal.issuer.finish", $"issuer:{issuerId}", new { created, warnings = warnings.Count }, ct).ConfigureAwait(false);
        return new SetupSummary(Environment(module.Instance), created, warnings, fiscal.Value.ProductionEnabled, report.AuthorizeEnabled);
    }

    public async Task<FiscalCredential> CreateCredential(long issuerId, string environment, string alias, string subjectTaxId, string organization, CancellationToken ct = default)
    {
        if (!FiscalEnvironments.IsValid(environment))
        {
            throw new ValidationException("El ambiente es homologation o production.");
        }

        var (key, csr) = CsrBuilder.Create(alias, subjectTaxId, organization);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var credential = new FiscalCredential(issuerId, environment, alias, subjectTaxId, organization, csr, time.GetUtcNow());
        db.FiscalCredentials.Add(credential);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Cifrada en el mismo pedido que la genera, atada a su fila. No vuelve al navegador ni pasa por los logs.
        credential.PrivateKey = box.Encrypt(key, CredentialsTable, "private_key", credential.Id);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("fiscal.credential.create", $"credential:{credential.Id}", new { issuerId, environment, alias }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return credential;
    }

    /// <summary>
    /// Quien migra con un certificado propio lo sube con su clave: PEM con o sin contraseña, DER o PKCS#12. La clave
    /// se pasa a PKCS#8 sin contraseña antes de cifrarla, y la contraseña no se guarda ni se registra.
    /// </summary>
    public async Task<FiscalCredential> ImportCredential(long issuerId, string environment, byte[] certificate, byte[]? privateKey, string? password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!FiscalEnvironments.IsValid(environment))
        {
            throw new ValidationException("El ambiente es homologation o production.");
        }

        using var cert = CertificateChecks.Load(certificate, privateKey is null ? password : null);
        using var rsa = RSA.Create();
        try
        {
            if (privateKey is null)
            {
                using var fromPfx = cert.GetRSAPrivateKey() ?? throw new ValidationException("El archivo no trae la clave privada.");
                rsa.ImportPkcs8PrivateKey(fromPfx.ExportPkcs8PrivateKey(), out _);
            }
            else
            {
                var pem = Encoding.UTF8.GetString(privateKey);
                if (pem.Contains("ENCRYPTED", StringComparison.Ordinal))
                {
                    rsa.ImportFromEncryptedPem(pem, password ?? string.Empty);
                }
                else
                {
                    rsa.ImportFromPem(pem);
                }
            }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            // ImportFromPem lanza ArgumentException cuando el texto no tiene ningún PEM. Nunca se devuelve el detalle: podría arrastrar parte de la clave.
            throw new ValidationException("No se pudo leer la clave privada. Se espera en PEM (PKCS#1 o PKCS#8), con su contraseña si la tiene, o dentro del PKCS#12.");
        }

        var (taxId, alias) = CertificateChecks.Subject(cert);
        var issuer = await db.IssuerProfiles.SingleAsync(i => i.Id == issuerId, ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var credential = new FiscalCredential(issuerId, environment, alias ?? "importado", taxId ?? issuer.TaxId, issuer.LegalName, string.Empty, time.GetUtcNow());
        db.FiscalCredentials.Add(credential);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        credential.PrivateKey = box.Encrypt(rsa.ExportPkcs8PrivateKeyPem(), CredentialsTable, "private_key", credential.Id);
        var checks = CertificateChecks.Run(credential, cert, rsa, issuer.TaxId, time.GetUtcNow());
        if (checks.FirstOrDefault(c => c.Level == CheckLevel.Error) is { } error)
        {
            throw new ValidationException(error.Message);
        }

        Store(credential, cert);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("fiscal.credential.import", $"credential:{credential.Id}", new { issuerId, environment, alias }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return credential;
    }

    /// <summary>
    /// Valida el certificado localmente, sin llamar a ARCA, y explica cada caso. Si no hay errores queda guardado con
    /// sus metadatos y la credencial lista para activar. Los avisos no frenan.
    /// </summary>
    public async Task<IReadOnlyList<CertificateCheck>> LoadCertificate(long credentialId, byte[] data, string? password = null, CancellationToken ct = default)
    {
        var credential = await db.FiscalCredentials.SingleAsync(c => c.Id == credentialId, ct).ConfigureAwait(false);
        var issuer = await db.IssuerProfiles.SingleAsync(i => i.Id == credential.IssuerId, ct).ConfigureAwait(false);
        using var cert = CertificateChecks.Load(data, password);
        using var key = credential.PrivateKey is null ? null : RSA.Create();
        key?.ImportFromPem(box.Decrypt(credential.PrivateKey!, CredentialsTable, "private_key", credential.Id));
        var checks = CertificateChecks.Run(credential, cert, key, issuer.TaxId, time.GetUtcNow());
        if (checks.All(c => c.Level != CheckLevel.Error))
        {
            Store(credential, cert);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.Record("fiscal.credential.certificate", $"credential:{credential.Id}", new { credential.Thumbprint, credential.NotAfter }, ct).ConfigureAwait(false);
        }

        return checks;
    }

    /// <summary>
    /// Activa la credencial: copia certificado y clave a los secretos de la instancia del emisor (ReplaceSecrets),
    /// retira la activa anterior y borra la clave de la fila. Queda en la auditoría. La instancia tiene que ser del
    /// mismo ambiente que la credencial. Si la instancia ya tiene secretos (un certificado cargado a mano o una
    /// credencial anterior), la candidata se prueba primero con un login nuevo, como una renovación: unos secretos
    /// que funcionan no se pisan con un certificado que no pasa. Devuelve esa prueba, o null si no hacía falta.
    /// </summary>
    public async Task<FiscalTestReport?> Activate(long credentialId, CancellationToken ct = default)
    {
        var credential = await db.FiscalCredentials.AsNoTracking().SingleAsync(c => c.Id == credentialId, ct).ConfigureAwait(false);
        var issuer = await db.IssuerProfiles.AsNoTracking().SingleAsync(i => i.Id == credential.IssuerId, ct).ConfigureAwait(false);
        var instance = issuer.ModuleInstanceId is { } instanceId ? await db.ModuleInstances.AsNoTracking().SingleAsync(i => i.Id == instanceId, ct).ConfigureAwait(false) : null;
        if (instance?.Secrets is null || credential.Status != CredentialStatus.Ready)
        {
            await ActivateWith(credentialId, null, ct).ConfigureAwait(false);
            return null;
        }

        if (Environment(instance) != credential.Environment)
        {
            throw new ValidationException($"La credencial es de {credential.Environment} y la instancia del emisor es de {Environment(instance)}.");
        }

        return await ActivateRenewal(credentialId, ct).ConfigureAwait(false);
    }

    /// <summary>Una credencial nueva con el mismo sujeto y otra clave, para renovar sin cortar la emisión (sección 2.8).</summary>
    public async Task<FiscalCredential> Renew(long credentialId, CancellationToken ct = default)
    {
        var current = await db.FiscalCredentials.AsNoTracking().SingleAsync(c => c.Id == credentialId, ct).ConfigureAwait(false);
        return await CreateCredential(current.IssuerId, current.Environment, current.Alias, current.SubjectTaxId, current.Organization, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Prueba la credencial candidata con un login nuevo, armando el sobre con la configuración actual y el certificado
    /// y la clave nuevos sin guardarlos. Si pasa, reemplaza los secretos de la instancia y guarda el estado que devolvió
    /// la prueba, todo junto. Hasta ese momento la vieja sigue emitiendo.
    /// </summary>
    public async Task<FiscalTestReport> ActivateRenewal(long credentialId, CancellationToken ct = default)
    {
        var credential = await db.FiscalCredentials.AsNoTracking().SingleAsync(c => c.Id == credentialId, ct).ConfigureAwait(false);
        var (report, state) = await RunTest(credential.IssuerId, credential, ct).ConfigureAwait(false);
        if (!report.Ok)
        {
            throw new ValidationException($"La credencial nueva no pasó la prueba: {report.Items.First(i => !i.Ok).Message}");
        }

        await ActivateWith(credentialId, state, ct).ConfigureAwait(false);
        return report;
    }

    /// <summary>La prueba sin emitir del emisor con la credencial que tiene la instancia. Queda guardada en el emisor.</summary>
    public async Task<FiscalTestReport> Test(long issuerId, CancellationToken ct = default)
    {
        var (report, _) = await RunTest(issuerId, null, ct).ConfigureAwait(false);
        return report;
    }

    /// <summary>Consulta el padrón (constancia de inscripción) por la instancia del emisor. Ayuda, nunca obliga.</summary>
    public async Task<TaxpayerInfo> LookupTaxpayer(long issuerId, string taxId, CancellationToken ct = default)
    {
        if (!TaxIdentity.IsValidCuit(taxId))
        {
            throw new ValidationException($"CUIT inválida: {taxId}.");
        }

        var issuer = await db.IssuerProfiles.SingleAsync(i => i.Id == issuerId, ct).ConfigureAwait(false);
        var module = await registry.Resolve(issuer.ModuleInstanceId ?? throw new ValidationException("El emisor no tiene instancia fiscal."), ct).ConfigureAwait(false);
        var result = await client.Call(module.Endpoint, "/v0/fiscal/taxpayer", new ModuleRequest(module.Data, [], new JsonObject { ["tax_id"] = taxId }), null, module.Timeout("taxpayer"), ct).ConfigureAwait(false);
        if (result.InstanceState is { } state)
        {
            await registry.SaveState(module, state, ct).ConfigureAwait(false);
        }

        if (result.Error is { } error)
        {
            throw new ValidationException($"El padrón no respondió: {error.Message}");
        }

        return TaxpayerInfo.From(result.Response!.Result!);
    }

    /// <summary>Precarga razón social y domicilio del emisor desde el padrón. Ingresos brutos e inicio de actividades no vienen.</summary>
    public async Task<TaxpayerInfo> FillIssuerFromRegistry(long issuerId, CancellationToken ct = default)
    {
        var issuer = await db.IssuerProfiles.SingleAsync(i => i.Id == issuerId, ct).ConfigureAwait(false);
        var info = await LookupTaxpayer(issuerId, issuer.TaxId, ct).ConfigureAwait(false);
        if (info.Name.Length > 0)
        {
            issuer.LegalName = info.Name;
        }

        var address = string.Join(", ", new[] { info.Street, info.City, info.Province, info.PostalCode }.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (address.Length > 0)
        {
            issuer.Address = address;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return info;
    }

    /// <summary>
    /// Asigna la instancia fiscal al emisor. Un emisor con series o comprobantes no pasa a una instancia de otro
    /// ambiente: homologación y producción no comparten emisor (sección 2.10 del diseño fiscal). Sin instancia, lo que
    /// ya tiene es la numeración real del sistema anterior (el emisor del importador) y cuenta como producción.
    /// </summary>
    public async Task AssignInstance(long issuerId, long instanceId, CancellationToken ct = default)
    {
        var issuer = await db.IssuerProfiles.SingleAsync(i => i.Id == issuerId, ct).ConfigureAwait(false);
        var next = await db.ModuleInstances.SingleAsync(i => i.Id == instanceId, ct).ConfigureAwait(false);
        var environment = Environment(next);
        var previous = issuer.ModuleInstanceId is { } current
            ? Environment(await db.ModuleInstances.SingleAsync(i => i.Id == current, ct).ConfigureAwait(false))
            : FiscalEnvironments.Production;
        if (previous != environment && await Used(issuerId, ct).ConfigureAwait(false))
        {
            throw new ValidationException($"El emisor ya tiene series o comprobantes de {previous}: no pasa a una instancia de {environment}. Para el otro ambiente se usa otro emisor u otra instalación.");
        }

        issuer.ModuleInstanceId = instanceId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("fiscal.issuer.instance", $"issuer:{issuerId}", new { instanceId, environment }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Avisos de vencimiento de los certificados (sección 2.8 del diseño fiscal): a los 30, 15, 7 y 1 día, cada uno una
    /// sola vez, como evento de auditoría visible en el panel. Corre una vez por día. Devuelve cuántos avisos nuevos hubo.
    /// </summary>
    public async Task<int> CheckCertificates(CancellationToken ct = default)
    {
        var today = core.Today();
        var notices = 0;
        foreach (var credential in await db.FiscalCredentials.Where(c => c.Status == CredentialStatus.Active || c.Status == CredentialStatus.Ready).ToListAsync(ct).ConfigureAwait(false))
        {
            if (credential.DaysLeft(today) is not { } days)
            {
                continue;
            }

            var sent = JsonSerializer.Deserialize<List<int>>(credential.NoticesSent) ?? [];
            foreach (var threshold in NoticeDays.Where(t => days <= t && !sent.Contains(t)))
            {
                sent.Add(threshold);
                notices++;
                await audit.Record(days < 0 ? "fiscal.certificate.expired" : "fiscal.certificate.expiring", $"credential:{credential.Id}", new { credential.Alias, credential.Environment, days, threshold }, ct).ConfigureAwait(false);
            }

            credential.NoticesSent = JsonSerializer.Serialize(sent);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return notices;
    }

    private async Task ActivateWith(long credentialId, JsonNode? state, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var credential = await db.FiscalCredentials.SingleAsync(c => c.Id == credentialId, ct).ConfigureAwait(false);
        var issuer = await db.IssuerProfiles.SingleAsync(i => i.Id == credential.IssuerId, ct).ConfigureAwait(false);
        var instanceId = issuer.ModuleInstanceId ?? throw new ValidationException("El emisor no tiene instancia fiscal: cargala antes de activar la credencial.");
        var instance = await db.ModuleInstances.SingleAsync(i => i.Id == instanceId, ct).ConfigureAwait(false);
        if (Environment(instance) != credential.Environment)
        {
            throw new ValidationException($"La credencial es de {credential.Environment} y la instancia del emisor es de {Environment(instance)}.");
        }

        if (credential.Status != CredentialStatus.Ready || credential.PrivateKey is null)
        {
            throw new ValidationException("La credencial no tiene certificado validado.");
        }

        var secrets = new JsonObject
        {
            ["certificate_pem"] = credential.Certificate,
            ["private_key_pem"] = box.Decrypt(credential.PrivateKey, CredentialsTable, "private_key", credential.Id),
        };
        await registry.ReplaceSecrets(instanceId, secrets.ToJsonString(), state, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        foreach (var previous in await db.FiscalCredentials.Where(c => c.IssuerId == issuer.Id && c.Environment == credential.Environment && c.Status == CredentialStatus.Active).ToListAsync(ct).ConfigureAwait(false))
        {
            previous.Retire(now);
        }

        credential.Activate(now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("fiscal.credential.activate", $"credential:{credential.Id}", new { instanceId, credential.Thumbprint, credential.NotAfter }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Compone la prueba con consultas que no autorizan nada: instance/test con login nuevo, parameters (puntos de venta,
    /// tributos, alícuotas y condiciones), last_number de cada serie y, si el emisor usa el padrón, su propia constancia.
    /// Con una credencial candidata, el sobre lleva su certificado y su clave sin guardarlos, y el estado que devuelve la
    /// prueba se entrega para activarla.
    /// </summary>
    private async Task<(FiscalTestReport Report, JsonNode? State)> RunTest(long issuerId, FiscalCredential? candidate, CancellationToken ct)
    {
        var issuer = await db.IssuerProfiles.SingleAsync(i => i.Id == issuerId, ct).ConfigureAwait(false);
        var module = await registry.Resolve(issuer.ModuleInstanceId ?? throw new ValidationException("El emisor no tiene instancia fiscal."), ct).ConfigureAwait(false);
        var data = module.Data;
        if (candidate is not null)
        {
            var config = data.Config.DeepClone().AsObject();
            config["certificate_pem"] = candidate.Certificate ?? throw new ValidationException("La credencial no tiene certificado.");
            config["private_key_pem"] = box.Decrypt(candidate.PrivateKey ?? throw new ValidationException("La credencial no tiene clave."), CredentialsTable, "private_key", candidate.Id);
            data = data with { Config = config, State = null };
        }

        JsonNode? state = null;
        var items = new List<TestItem>();
        JsonArray? vatRates = null;
        JsonArray? points = null;
        JsonArray? taxTypes = null;
        JsonArray? conditions = null;
        bool? authorizeEnabled = null;

        async Task<JsonObject?> Call(string path, JsonObject input, string step, string remedy)
        {
            var result = await client.Call(module.Endpoint, path, new ModuleRequest(data, [], input), null, module.Timeout(path.Split('/')[^1]), ct).ConfigureAwait(false);
            if (result.InstanceState is { } fresh)
            {
                state = fresh;
                if (candidate is null)
                {
                    module = await registry.SaveState(module, fresh, ct).ConfigureAwait(false);
                    data = module.Data;
                }
                else
                {
                    data = data with { State = fresh };
                }
            }

            if (result.Error is { } error)
            {
                var wait = error.RetryAfter is { } s ? $" Esperá {s} segundos y probá de nuevo." : string.Empty;
                items.Add(new TestItem(step, false, $"{error.Message}{wait}", remedy));
                return null;
            }

            return result.Response?.Result as JsonObject;
        }

        // 1. Conexión, certificado y ambiente, con un ticket nuevo: así renovar el certificado no se prueba con el ticket del viejo.
        var test = await Call("/v0/instance/test", new JsonObject { ["fresh_login"] = true }, "connection", "Revisá que el certificado sea del ambiente elegido, que esté vigente y que la hora del servidor esté en NTP.").ConfigureAwait(false);
        if (test is not null)
        {
            authorizeEnabled = test["authorize_enabled"]?.GetValue<bool>();
            items.Add(new TestItem("connection", true, $"Conectado a {test["environment"]}. Certificado vence el {test["certificate_expires_on"]}."));
        }

        // 2 y 3. Servicios, delegación de la CUIT, puntos de venta y tablas: las llamadas autenticadas fallan si la CUIT no está delegada.
        var parameters = test is null ? null : await Call("/v0/fiscal/parameters", new JsonObject { ["export"] = issuer.Exports }, "services", "Asociá el servicio al certificado en el Administrador de Relaciones (producción) o en WSASS (homologación), y delegá la CUIT que factura.").ConfigureAwait(false);
        if (parameters is not null)
        {
            vatRates = parameters["vat_rates"]?.DeepClone() as JsonArray;
            taxTypes = parameters["tax_types"]?.DeepClone() as JsonArray;
            conditions = parameters["receiver_vat_conditions"]?.DeepClone() as JsonArray;
            points = parameters["points_of_sale"]?.DeepClone() as JsonArray;
            items.Add(new TestItem("services", true, "Los servicios responden con la CUIT del emisor."));
            CheckPoint(items, points, issuer.PointOfSale, "points_of_sale", "Dalo de alta en ARCA como punto de venta de web services (RECE para aplicativo y web services) con 3 días hábiles de anticipación, o elegí uno de la lista.");
            if (issuer.Exports)
            {
                CheckPoint(items, parameters["export_points_of_sale"] as JsonArray, issuer.ExportPointOfSale ?? 0, "export_point_of_sale", "La factura E necesita su propio punto de venta de Comprobantes de Exportación - Web Services.");
            }

            issuer.KnownPointsOfSale = (points ?? []).ToJsonString();
        }

        // 4. Series al día: el último número de ARCA contra el último registrado (o importado).
        if (parameters is not null)
        {
            var drift = new List<string>();
            foreach (var series in await db.FiscalSeries.Where(s => s.IssuerId == issuer.Id).OrderBy(s => s.Id).ToListAsync(ct).ConfigureAwait(false))
            {
                var last = await Call("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = series.PointOfSale, ["type_code"] = series.TypeCode }, "series", "Conciliá la serie antes de emitir.").ConfigureAwait(false);
                if (last?["number"]?.GetValue<long>() is { } number && number != series.LastNumber)
                {
                    drift.Add(string.Create(CultureInfo.InvariantCulture, $"{series.PointOfSale}-{series.TypeCode}: ARCA tiene {number} y el núcleo {series.LastNumber}"));
                }
            }

            if (drift.Count > 0)
            {
                items.Add(new TestItem("series", false, string.Join("; ", drift), "Conciliá las series antes de emitir: el fisco y el núcleo tienen que coincidir en el último número."));
            }
            else if (items.All(i => i.Step != "series"))
            {
                items.Add(new TestItem("series", true, "Las series coinciden con ARCA."));
            }
        }

        // 5. Condiciones del receptor: la tabla que usa la validación de la ficha.
        if (conditions is { Count: > 0 })
        {
            var unknown = conditions.Select(c => c?["id"]?.GetValue<int>()).OfType<int>().Where(c => !VatConditions.IsKnown(c)).ToList();
            items.Add(new TestItem("receiver_conditions", unknown.Count == 0, unknown.Count == 0 ? "La tabla de condiciones coincide con la del sistema." : $"ARCA tiene condiciones que el sistema no conoce: {string.Join(", ", unknown)}.", unknown.Count == 0 ? null : "Actualizá el sistema."));
        }

        // 6. Datos del emisor según ARCA, si autorizó el padrón.
        if (test is not null && issuer.UsesRegistry)
        {
            var taxpayer = await Call("/v0/fiscal/taxpayer", new JsonObject { ["tax_id"] = issuer.TaxId }, "taxpayer", "Autorizá ws_sr_constancia_inscripcion para el certificado, o apagá el padrón en el emisor.").ConfigureAwait(false);
            if (taxpayer is not null)
            {
                var info = TaxpayerInfo.From(taxpayer);

                // Se compara por clase de comprobante: el padrón sugiere 1, 6 o 4, y un monotributista social (13) o
                // promovido (16) emite la misma clase C que el 6. Solo frena lo que cambiaría la clase.
                var sameClass = info.SuggestedVatCondition is not { } suggested || VatConditions.IssuesClassC(suggested) == VatConditions.IssuesClassC(issuer.VatCondition);
                var message = info.Blocked ? $"La constancia está bloqueada: {string.Join(" ", info.Notes)}"
                    : info.SuggestedVatCondition is null || info.SuggestedVatCondition == issuer.VatCondition ? $"ARCA informa {info.Name} con la condición cargada."
                    : sameClass ? $"ARCA informa {info.Name} con la condición {info.SuggestedVatCondition}, de la misma clase de comprobante que la cargada ({issuer.VatCondition})."
                    : $"ARCA informa la condición {info.SuggestedVatCondition} y el emisor tiene {issuer.VatCondition}: cambia la clase de comprobante.";
                items.Add(new TestItem("taxpayer", sameClass && !info.Blocked, message, sameClass && !info.Blocked ? null : "Revisá la condición del emisor y la constancia en ARCA."));
            }
        }

        var report = new FiscalTestReport(time.GetUtcNow(), items.Count > 0 && items.All(i => i.Ok), items, vatRates, points, taxTypes, conditions, authorizeEnabled);
        if (candidate is null)
        {
            issuer.LastTest = report.ToJson();
            issuer.LastTestAt = report.At;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.Record("fiscal.test", $"issuer:{issuer.Id}", new { report.Ok, failed = items.Where(i => !i.Ok).Select(i => i.Step) }, ct).ConfigureAwait(false);
        }

        return (report, state);
    }

    private static void CheckPoint(List<TestItem> items, JsonArray? points, int wanted, string step, string remedy)
    {
        var found = points?.FirstOrDefault(p => p?["id"]?.GetValue<int>() == wanted);
        if (found is null)
        {
            items.Add(new TestItem(step, false, points is { Count: > 0 } ? $"El punto de venta {wanted} no está entre los que informa ARCA ({string.Join(", ", points.Select(p => p?["id"]?.ToString()))})." : $"ARCA no informa puntos de venta y el emisor usa el {wanted}.", remedy));
        }
        else if (found["blocked"]?.GetValue<bool>() == true || found["retired_on"] is not null)
        {
            items.Add(new TestItem(step, false, $"El punto de venta {wanted} está bloqueado o dado de baja en ARCA.", remedy));
        }
        else
        {
            items.Add(new TestItem(step, true, $"El punto de venta {wanted} está habilitado ({found["emission_type"]})."));
        }
    }

    private static string Environment(ModuleInstance instance) => JsonNode.Parse(instance.Config)?["environment"]?.ToString() ?? FiscalEnvironments.Homologation;

    private static void Store(FiscalCredential credential, X509Certificate2 cert)
    {
        var (taxId, alias) = CertificateChecks.Subject(cert);
        credential.LoadCertificate(
            cert.ExportCertificatePem(),
            taxId,
            alias,
            cert.Issuer,
            cert.SerialNumber,
            Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)),
            new DateTimeOffset(cert.NotBefore.ToUniversalTime()),
            new DateTimeOffset(cert.NotAfter.ToUniversalTime()));
    }
}

/// <summary>Trabajo diario de los avisos de vencimiento de certificados.</summary>
public sealed class FiscalCertificateHandler(FiscalSetupService setup) : IJobHandler
{
    public string Kind => JobKinds.FiscalCertificates;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        await setup.CheckCertificates(ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}
