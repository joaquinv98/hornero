using System.Text;
using System.Text.Json;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Fiscal;
using Fluid;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Documents;

/// <summary>
/// Renderiza el HTML del comprobante con la plantilla propia y el motor tipo Liquid común (<see cref="Liquid"/>, D-13):
/// salida codificada, los mismos topes que los avisos y solo los tipos del modelo.
/// </summary>
public static class DocumentHtml
{
    private static readonly IFluidTemplate Template = Parse();

    private static readonly TemplateOptions Options =
        Liquid.Options(typeof(PrintModel), typeof(PrintParty), typeof(PrintLine), typeof(PrintTotal), typeof(PrintTransparency), typeof(PrintFiscal));

    public static Task<string> Render(PrintModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Liquid.Render(Template, new TemplateContext(model, Options), Liquid.Html);
    }

    private static IFluidTemplate Parse()
    {
        using var stream = typeof(DocumentHtml).Assembly.GetManifestResourceStream("BillingOss.App.Documents.Comprobante.liquid")
            ?? throw new InvalidOperationException("Falta la plantilla del comprobante.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Liquid.Parse(reader.ReadToEnd());
    }
}

/// <summary>
/// Cliente de Gotenberg (D-13): convierte el HTML a PDF con Chromium en un contenedor aparte, sin salida a la red. Falla si
/// el documento intenta cargar algo de afuera, así todo lo que muestra tiene que ir embebido.
/// </summary>
public sealed class GotenbergClient(HttpClient http)
{
    public async Task<byte[]> Convert(string html, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(html, Encoding.UTF8, "text/html"), "files", "index.html" },
            { new StringContent("true"), "preferCssPageSize" },
            { new StringContent("true"), "printBackground" },
            { new StringContent("true"), "failOnResourceLoadingFailed" },
            { new StringContent("true"), "failOnConsoleExceptions" },
        };
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(new Uri("forms/chromium/convert/html", UriKind.Relative), form, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new RetryLaterException($"El conversor de PDF no responde: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new RetryLaterException($"El conversor de PDF respondió {(int)response.StatusCode}: {detail[..Math.Min(detail.Length, 300)]}");
            }

            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
    }
}

/// <summary>PDF guardado de un documento con su comprobante fiscal: se genera una vez y no cambia más (D-05).</summary>
public sealed class DocumentPdf
{
    public long DocumentId { get; set; }

    public long FiscalDocumentId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public byte[] Content { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class DocumentPdfConfiguration : IEntityTypeConfiguration<DocumentPdf>
{
    public void Configure(EntityTypeBuilder<DocumentPdf> b)
    {
        b.ToTable("document_pdfs", "billing");
        b.HasKey(x => x.DocumentId);
        b.Property(x => x.FileName).HasMaxLength(100);
        b.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId);
        b.HasOne<FiscalDocument>().WithMany().HasForeignKey(x => x.FiscalDocumentId);
    }
}

public sealed record RenderedPdf(string FileName, byte[] Content);

/// <summary>
/// El PDF de una factura o nota. Con comprobante fiscal se genera una vez y se guarda en la base, así todos los nodos
/// devuelven el mismo archivo aunque cambie la plantilla. Sin comprobante (todavía no autorizado, o sin fisco) se
/// genera en cada pedido y lleva la marca de no válido como factura cuando se espera uno.
/// </summary>
public sealed class DocumentPdfService(BillingDbContext db, TimeProvider time, GotenbergClient? converter = null)
{
    /// <summary>El PDF del documento, o null si no existe o es un borrador.</summary>
    public async Task<RenderedPdf?> Get(long documentId, CancellationToken ct)
    {
        var stored = await db.Set<DocumentPdf>().AsNoTracking().Where(p => p.DocumentId == documentId)
            .Select(p => new RenderedPdf(p.FileName, p.Content)).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (stored is not null)
        {
            return stored;
        }

        var source = await Load(documentId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        // El PDF con CAE se guarda para siempre: con los datos del emisor incompletos (por ejemplo, el emisor que deja
        // el importador) no se genera hasta que el staff los carga (docs/pdf).
        if (source.Fiscal is not null && PrintModels.MissingIssuerData(source.Issuer) is { Count: > 0 } missing)
        {
            throw new InvalidOperationException($"Faltan datos del emisor para el PDF: {string.Join(", ", missing)}.");
        }

        if (converter is null)
        {
            throw new RetryLaterException("No está configurado el conversor de PDF (Pdf:GotenbergUrl).");
        }

        var model = PrintModels.Build(source);
        var content = await converter.Convert(await DocumentHtml.Render(model).ConfigureAwait(false), ct).ConfigureAwait(false);
        var pdf = new RenderedPdf(FileName(source.Document, model), content);
        if (source.Fiscal is not { } fiscal)
        {
            return pdf;
        }

        // Dos nodos pueden generarlo a la vez: gana el primero y los dos devuelven el guardado.
        // ponytail: el PDF va en la base (unos 100 KB por comprobante) y viaja con la replicación. Pasa al
        // almacenamiento de adjuntos de D-14 cuando exista o si el volumen lo pide.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO billing.document_pdfs (document_id, fiscal_document_id, file_name, content, created_at) VALUES ({documentId}, {fiscal.Id}, {pdf.FileName}, {pdf.Content}, {time.GetUtcNow()}) ON CONFLICT (document_id) DO NOTHING",
            ct).ConfigureAwait(false);
        return await db.Set<DocumentPdf>().AsNoTracking().Where(p => p.DocumentId == documentId)
            .Select(p => new RenderedPdf(p.FileName, p.Content)).SingleAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Lee de la base todo lo que el PDF muestra. Null si el documento no existe o no está emitido.</summary>
    public async Task<PrintSource?> Load(long documentId, CancellationToken ct)
    {
        var doc = await db.Documents.AsNoTracking().Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == documentId, ct).ConfigureAwait(false);
        if (doc is not { IsIssued: true })
        {
            return null;
        }

        // Un importado puede tener más de un comprobante colgado (D-17): va el que corresponde a su tipo.
        var fiscals = await db.FiscalDocuments.AsNoTracking().Where(f => f.DocumentId == documentId).OrderBy(f => f.Id).ToListAsync(ct).ConfigureAwait(false);
        var fiscal = fiscals.FirstOrDefault(f => KindOf(f.TypeCode) == doc.Kind) ?? fiscals.FirstOrDefault();
        var active = await db.IssuerProfiles.AsNoTracking().Where(i => i.Active).OrderBy(i => i.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var issuer = fiscal is null ? active : await db.IssuerProfiles.AsNoTracking().SingleAsync(i => i.Id == fiscal.IssuerId, ct).ConfigureAwait(false);
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == doc.AccountId, ct).ConfigureAwait(false);

        var receiver = string.IsNullOrEmpty(doc.Receiver) ? null : JsonSerializer.Deserialize<ReceiverSnapshot>(doc.Receiver);
        if (string.IsNullOrEmpty(receiver?.LegalName))
        {
            // Los importados no traen la copia del receptor: se imprime la identidad fiscal vigente.
            var identity = await db.TaxIdentities.AsNoTracking().SingleOrDefaultAsync(t => t.AccountId == doc.AccountId && t.ValidTo == null, ct).ConfigureAwait(false);
            receiver = identity is null
                ? new ReceiverSnapshot(Domain.Accounts.TaxDocumentType.None, string.Empty, account.CompanyName ?? account.Name, receiver?.VatCondition is > 0 and var v ? v : 5, "AR", null)
                : new ReceiverSnapshot(identity.DocumentType, identity.DocumentNumber, identity.LegalName, identity.VatCondition, identity.Country, identity.Province);
        }

        var lineIds = doc.Lines.Select(l => l.Id).ToList();
        var periods = await db.BilledPeriods.AsNoTracking().Where(p => lineIds.Contains(p.DocumentLineId)).ToListAsync(ct).ConfigureAwait(false);
        var related = doc.RelatedDocumentId is { } relatedId
            ? await db.Documents.AsNoTracking().Where(d => d.Id == relatedId).Select(d => d.Number).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        return new PrintSource(
            doc,
            receiver,
            issuer,
            fiscal,
            FiscalExpected: !doc.Imported && !account.NoFiscalDocument && active?.ModuleInstanceId is not null,
            periods.Count > 0 ? periods.Min(p => p.Start) : null,
            periods.Count > 0 ? periods.Max(p => p.End).AddDays(-1) : null,
            related);
    }

    private static DocumentKind KindOf(int typeCode) => typeCode switch
    {
        2 or 7 or 12 or 20 or 52 => DocumentKind.DebitNote,
        3 or 8 or 13 or 21 or 53 => DocumentKind.CreditNote,
        _ => DocumentKind.Invoice,
    };

    private static string FileName(Document doc, PrintModel model)
    {
        var kind = doc.Kind switch { DocumentKind.CreditNote => "nota-de-credito", DocumentKind.DebitNote => "nota-de-debito", _ => "factura" };
        return model.Letter is null ? $"{kind}-{model.Number}.pdf" : $"{kind}-{model.Letter}-{model.Number}.pdf";
    }
}

public static class DocumentPdfServices
{
    /// <summary>
    /// Registra el PDF de los comprobantes. Sin <paramref name="gotenbergUrl"/> los pedidos de PDF responden que falta el
    /// conversor, y el resto del sistema anda igual.
    /// </summary>
    public static IServiceCollection AddDocumentPdf(this IServiceCollection services, string? gotenbergUrl)
    {
        if (!string.IsNullOrWhiteSpace(gotenbergUrl))
        {
            var baseUrl = new Uri(gotenbergUrl.EndsWith('/') ? gotenbergUrl : gotenbergUrl + "/");
            services.AddSingleton(new GotenbergClient(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
            {
                BaseAddress = baseUrl,
                Timeout = TimeSpan.FromSeconds(30),
            }));
        }

        services.AddScoped<DocumentPdfService>();
        return services;
    }
}
