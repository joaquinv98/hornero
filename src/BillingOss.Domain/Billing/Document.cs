using System.Globalization;
using System.Text.Json;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Billing;

public enum DocumentKind
{
    Invoice,
    CreditNote,
    DebitNote,
}

public enum LineKind
{
    Renewal,
    Setup,
    OneTime,
    PlanChange,
    LateFee,
    Manual,

    /// <summary>Excedente de una métrica de uso medido, con su cargo (<see cref="Usage.UsageCharge"/>).</summary>
    Usage,

    /// <summary>Ítem facturable suelto (<see cref="BillableItem"/>), con su clave contra duplicados.</summary>
    Item,

    /// <summary>Solo en documentos importados, cuando las líneas del sistema anterior no suman el total.</summary>
    RoundingAdjustment,
}

/// <summary>
/// Documento comercial: factura, nota de crédito o nota de débito (sección 6 del modelo de dominio). Se arma como
/// borrador y se emite con un cierre explícito que lo congela. La base protege el congelamiento con un trigger.
/// </summary>
public sealed class Document
{
    private readonly List<DocumentLine> lines = [];

    private Document()
    {
    }

    public Document(DocumentKind kind, long accountId, Currency currency, PriceMode priceMode, DateOnly dueDate, DateTimeOffset now, long? relatedDocumentId = null, string? reason = null)
    {
        Kind = kind;
        AccountId = accountId;
        Currency = currency;
        PriceMode = priceMode;
        DueDate = dueDate;
        CreatedAt = now;
        RelatedDocumentId = relatedDocumentId;
        Reason = reason;
    }

    public long Id { get; private set; }

    public DocumentKind Kind { get; private set; }

    /// <summary>Número comercial, de la serie de su tipo. Se asigna al emitir.</summary>
    public long? Number { get; private set; }

    public long AccountId { get; private set; }

    public Currency Currency { get; private set; }

    public PriceMode PriceMode { get; private set; }

    public DateOnly? IssueDate { get; private set; }

    public DateOnly DueDate { get; private set; }

    /// <summary>Factura original de una nota.</summary>
    public long? RelatedDocumentId { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>Copia de la identidad fiscal del receptor al emitir. JSON.</summary>
    public string? Receiver { get; private set; }

    /// <summary>Totales por alícuota al emitir. JSON.</summary>
    public string? Aliquots { get; private set; }

    public decimal NetTaxed { get; private set; }

    public decimal Exempt { get; private set; }

    public decimal NotTaxed { get; private set; }

    public decimal Tax { get; private set; }

    /// <summary>Tributos de comprobante que suman al total (percepciones). Cero si no hay.</summary>
    public decimal OtherTaxes { get; private set; }

    /// <summary>Los tributos de comprobante, calculados y congelados al emitir. JSON con la forma de DocumentTaxAmount.</summary>
    public string? Taxes { get; private set; }

    /// <summary>Leyendas que lleva el comprobante, congeladas al emitir. JSON con una lista de textos.</summary>
    public string? Legends { get; private set; }

    /// <summary>Neto gravado más IVA, exento, no gravado y tributos.</summary>
    public decimal Total { get; private set; }

    /// <summary>Suma de imputaciones vigentes. Se cambia bajo lock, junto con la imputación.</summary>
    public decimal Allocated { get; private set; }

    /// <summary>Suma de notas de crédito contra esta factura. Se cambia bajo lock, junto con la nota.</summary>
    public decimal Credited { get; private set; }

    /// <summary>Lo pagado que las notas de crédito devolvieron, como reembolso o como saldo a favor.</summary>
    public decimal Refunded { get; private set; }

    public bool Imported { get; private set; }

    /// <summary>Estado en el sistema anterior, tal cual vino. Solo en documentos importados (D-17).</summary>
    public string? SourceStatus { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? IssuedAt { get; private set; }

    public IReadOnlyList<DocumentLine> Lines => lines;

    public bool IsIssued => IssueDate is not null;

    /// <summary>
    /// Derivado de los importes. Un importado de total cero no tiene importes que lo digan (el núcleo nunca emite
    /// uno así): vale el estado del origen, pagado salvo que viniera anulado.
    /// </summary>
    public InvoiceStatus Status => Imported && IsIssued && Total == 0m
        ? SourceStatus is "Cancelled" or "Refunded" ? InvoiceStatus.Cancelled : InvoiceStatus.Paid
        : InvoiceRules.Status(IsIssued, Total, Credited, Allocated);

    public decimal Pending => InvoiceRules.Pending(Total, Credited, Allocated);

    public DocumentLine AddLine(LineKind kind, string description, decimal quantity, decimal unitPrice, TaxTreatment treatment, decimal rate, long? serviceId = null, string? dedupKey = null, string? category = null, string? reason = null, string? legend = null, long? domainId = null)
    {
        if (IsIssued)
        {
            throw new InvalidOperationException($"El documento {Id} ya está emitido.");
        }

        var line = new DocumentLine(kind, description, quantity, unitPrice, treatment, rate, serviceId, dedupKey) { Category = category, Reason = reason, Legend = legend, DomainId = domainId };
        lines.Add(line);
        return line;
    }

    /// <summary>Los renglones repartidos por el cálculo de sus líneas, con su categoría y su tratamiento, para los tributos de comprobante.</summary>
    public IReadOnlyList<TaxedLine> TaxedLines()
    {
        var computed = Compute();
        return [.. lines.Select((l, i) => new TaxedLine(l.Category ?? Catalog.TaxCategories.Digital, computed.Lines.Single(s => s.Key == Key(i)), l.Treatment))];
    }

    /// <summary>Lo que el cálculo de impuestos daría con las líneas actuales.</summary>
    public TaxBreakdown Compute() =>
        TaxCalculator.Compute(PriceMode, [.. lines.Select((l, i) => new TaxableLine(Key(i), l.Amount, l.Treatment, l.Rate))]);

    /// <summary>
    /// Primer paso del cierre: calcula los importes finales y la parte de cada línea, todavía en borrador. Las
    /// notas de crédito pasan los importes ya topeados contra el original (<see cref="CreditNoteRules"/>). Va en un
    /// paso aparte porque la base no deja tocar las líneas de un documento ya emitido.
    /// </summary>
    public TaxBreakdown PrepareIssue(TaxBreakdown? capped = null)
    {
        if (IsIssued)
        {
            throw new InvalidOperationException($"El documento {Id} ya está emitido.");
        }

        if (lines.Count == 0)
        {
            throw new InvalidOperationException("Un documento sin líneas no se emite.");
        }

        var computed = Compute();
        var breakdown = capped ?? computed;
        if (breakdown.Total <= 0m)
        {
            throw new InvalidOperationException("Un documento de total cero o negativo no se emite.");
        }

        var splits = SplitsMatching(computed, breakdown);
        for (var i = 0; i < lines.Count; i++)
        {
            lines[i].SetSplit(splits[Key(i)].Net, splits[Key(i)].Tax);
        }

        return breakdown;
    }

    /// <summary>
    /// Segundo paso del cierre: congela receptor, importes, tributos y leyendas. El número comercial no va acá: lo
    /// asigna <see cref="AssignNumber"/> justo antes de confirmar la transacción, así el contador queda bloqueado lo
    /// menos posible.
    /// </summary>
    public void Issue(DateOnly issueDate, string receiver, DateTimeOffset now, TaxBreakdown breakdown, IReadOnlyList<DocumentTaxAmount>? taxes = null, IReadOnlyList<string>? legends = null)
    {
        ArgumentNullException.ThrowIfNull(breakdown);
        if (IsIssued)
        {
            throw new InvalidOperationException($"El documento {Id} ya está emitido.");
        }

        taxes ??= [];
        IssueDate = issueDate;
        IssuedAt = now;
        Receiver = receiver;
        Aliquots = JsonSerializer.Serialize(breakdown.Aliquots);
        NetTaxed = breakdown.NetTaxed;
        Exempt = breakdown.Exempt;
        NotTaxed = breakdown.NotTaxed;
        Tax = breakdown.Tax;
        OtherTaxes = taxes.Where(t => t.AddsToTotal).Sum(t => t.Amount);
        Taxes = JsonSerializer.Serialize(taxes);
        Legends = JsonSerializer.Serialize(Collect(legends, taxes, lines));
        Total = breakdown.Total + OtherTaxes;
    }

    /// <summary>Número comercial de un documento emitido, una sola vez.</summary>
    public void AssignNumber(long number)
    {
        if (!IsIssued || Number is not null)
        {
            throw new InvalidOperationException($"El documento {Id} no está emitido o ya tiene número.");
        }

        Number = number;
    }

    /// <summary>Importes emitidos, para topear las notas de crédito.</summary>
    public TaxBreakdown IssuedBreakdown() =>
        IsIssued
            ? new TaxBreakdown(JsonSerializer.Deserialize<List<AliquotTotal>>(Aliquots!)!, [], Exempt, NotTaxed)
            : throw new InvalidOperationException($"El documento {Id} no está emitido.");

    /// <summary>Tributos de comprobante emitidos. Vacío en los documentos sin tributos y en los importados.</summary>
    public IReadOnlyList<DocumentTaxAmount> IssuedTaxes() =>
        Taxes is null ? [] : JsonSerializer.Deserialize<List<DocumentTaxAmount>>(Taxes) ?? [];

    public IReadOnlyList<string> IssuedLegends() =>
        Legends is null ? [] : JsonSerializer.Deserialize<List<string>>(Legends) ?? [];

    private static List<string> Collect(IReadOnlyList<string>? legends, IReadOnlyList<DocumentTaxAmount> taxes, IEnumerable<DocumentLine> lines) =>
        [.. (legends ?? []).Concat(taxes.Select(t => t.Legend).OfType<string>()).Concat(lines.Select(l => l.Legend).OfType<string>()).Distinct(StringComparer.Ordinal)];

    public void Allocate(decimal amount)
    {
        if (amount <= 0m || amount > Pending)
        {
            throw new InvalidOperationException($"Imputación de {amount} inválida: el documento {Id} debe {Pending}.");
        }

        Allocated += amount;
    }

    public void ReverseAllocation(decimal amount)
    {
        if (amount <= 0m || amount > Allocated)
        {
            throw new InvalidOperationException($"Reversa de {amount} inválida en el documento {Id}.");
        }

        Allocated -= amount;
    }

    /// <summary>Lo pagado que devuelve una nota de crédito: sale de lo imputado y queda como devuelto.</summary>
    public void ReturnPaid(decimal amount)
    {
        ReverseAllocation(amount);
        Refunded += amount;
    }

    public void AddCredit(decimal amount)
    {
        if (amount <= 0m || Credited + amount > Total)
        {
            throw new InvalidOperationException($"Crédito de {amount} inválido en el documento {Id}.");
        }

        Credited += amount;
    }

    public void SetImportedId(long id) => Id = id;

    /// <summary>
    /// Documento traído del sistema anterior: emitido, con sus importes de cabecera como verdad (D-17). Las líneas se
    /// agregan con <see cref="ImportLine"/>, y si no suman el total va una línea de ajuste de redondeo.
    /// </summary>
    public static Document FromImport(
        long id,
        DocumentKind kind,
        long accountId,
        Currency currency,
        long number,
        DateOnly issueDate,
        DateOnly dueDate,
        TaxBreakdown amounts,
        decimal allocated,
        decimal credited,
        string receiver,
        DateTimeOffset now,
        decimal refunded = 0m,
        string? sourceStatus = null,
        long? relatedDocumentId = null)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        return new Document(kind, accountId, currency, PriceMode.TaxIncluded, dueDate, now, relatedDocumentId)
        {
            Id = id,
            Number = number,
            IssueDate = issueDate,
            IssuedAt = now,
            Receiver = receiver,
            Aliquots = JsonSerializer.Serialize(amounts.Aliquots),
            NetTaxed = amounts.NetTaxed,
            Tax = amounts.Tax,
            Exempt = amounts.Exempt,
            NotTaxed = amounts.NotTaxed,
            Total = amounts.Total,
            Allocated = allocated,
            Credited = credited,
            Refunded = refunded,
            Imported = true,
            SourceStatus = sourceStatus,
        };
    }

    /// <summary>Línea de un documento importado. Solo vale en documentos importados.</summary>
    public void ImportLine(LineKind kind, string description, decimal amount, TaxTreatment treatment, decimal rate, long? serviceId, long? domainId = null)
    {
        if (!Imported)
        {
            throw new InvalidOperationException("Solo los documentos importados aceptan líneas emitidas.");
        }

        var line = new DocumentLine(kind, description, 1m, amount, treatment, rate, serviceId, null) { DomainId = domainId };
        line.SetSplit(amount, 0m);
        lines.Add(line);
    }

    private static string Key(int index) => index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Las partes de cada línea, ajustadas para que sumen los importes finales. Si una nota topeada difiere del
    /// cálculo de sus líneas en un centavo de neto o de IVA, la diferencia va a la línea mayor de esa alícuota.
    /// </summary>
    private Dictionary<string, LineSplit> SplitsMatching(TaxBreakdown computed, TaxBreakdown target)
    {
        var splits = computed.Lines.ToDictionary(l => l.Key);
        foreach (var aliquot in target.Aliquots)
        {
            var keys = lines.Select((l, i) => (Line: l, Key: Key(i)))
                .Where(x => x.Line.Treatment == TaxTreatment.Taxed && x.Line.Rate == aliquot.Rate)
                .ToList();
            var netDiff = aliquot.Net - keys.Sum(x => splits[x.Key].Net);
            var taxDiff = aliquot.Tax - keys.Sum(x => splits[x.Key].Tax);
            if ((netDiff != 0m || taxDiff != 0m) && keys.Count > 0)
            {
                var largest = keys.OrderByDescending(x => Math.Abs(x.Line.Amount)).First().Key;
                splits[largest] = splits[largest] with { Net = splits[largest].Net + netDiff, Tax = splits[largest].Tax + taxDiff };
            }
        }

        return splits;
    }
}

public sealed class DocumentLine
{
    private DocumentLine()
    {
    }

    internal DocumentLine(LineKind kind, string description, decimal quantity, decimal unitPrice, TaxTreatment treatment, decimal rate, long? serviceId, string? dedupKey)
    {
        Kind = kind;
        Description = description;
        Quantity = quantity;
        UnitPrice = unitPrice;
        Amount = Rounding.Commercial(quantity * unitPrice);
        Treatment = treatment;
        Rate = treatment == TaxTreatment.Taxed ? rate : 0m;
        ServiceId = serviceId;
        DedupKey = dedupKey;
    }

    public long Id { get; private set; }

    public long DocumentId { get; private set; }

    public LineKind Kind { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public decimal Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    /// <summary>Bruto o neto según el modo de precios del documento, con dos decimales.</summary>
    public decimal Amount { get; private set; }

    public TaxTreatment Treatment { get; private set; }

    public decimal Rate { get; private set; }

    public decimal Net { get; private set; }

    public decimal LineTax { get; private set; }

    public long? ServiceId { get; private set; }

    /// <summary>Dominio que factura la línea, en las renovaciones y registros de dominios.</summary>
    public long? DomainId { get; internal init; }

    /// <summary>Clave contra duplicados según el origen: cargo de alta, cambio de plan, recargo o período de un dominio.</summary>
    public string? DedupKey { get; private set; }

    /// <summary>Categoría del renglón, congelada como la alícuota (<see cref="Catalog.TaxCategory.Code"/>).</summary>
    public string? Category { get; init; }

    /// <summary>Por qué lleva esa alícuota (<see cref="TaxReasons"/>), congelado.</summary>
    public string? Reason { get; init; }

    /// <summary>Leyenda que la regla del renglón pide en el comprobante.</summary>
    public string? Legend { get; init; }

    internal void SetSplit(decimal net, decimal tax)
    {
        Net = net;
        LineTax = tax;
    }
}

/// <summary>Período reservado por una línea de renovación. Dos vigentes del mismo servicio no se superponen.</summary>
public sealed class BilledPeriod
{
    private BilledPeriod()
    {
    }

    public BilledPeriod(long serviceId, DateOnly start, DateOnly end, DocumentLine line)
    {
        ServiceId = serviceId;
        Start = start;
        End = end;
        Line = line;
    }

    public long Id { get; private set; }

    public long ServiceId { get; private set; }

    public DateOnly Start { get; private set; }

    public DateOnly End { get; private set; }

    public long DocumentLineId { get; private set; }

    public DocumentLine? Line { get; private set; }

    /// <summary>Liberado al anular la factura por completo: el período se puede volver a facturar.</summary>
    public bool Released { get; private set; }

    public void Release() => Released = true;
}

/// <summary>Movimiento del libro de saldo a favor. Solo altas: se compensa con otro, nunca se edita.</summary>
public sealed class CreditEntry
{
    private CreditEntry()
    {
    }

    public CreditEntry(long accountId, Currency currency, decimal amount, string reason, DateTimeOffset now, long? paymentId = null, long? documentId = null, string? actor = null)
    {
        Rounding.EnsureCents(amount, nameof(amount));
        AccountId = accountId;
        Currency = currency;
        Amount = amount;
        Reason = reason;
        CreatedAt = now;
        PaymentId = paymentId;
        DocumentId = documentId;
        Actor = actor;
    }

    public long Id { get; private set; }

    public long AccountId { get; private set; }

    public Currency Currency { get; private set; }

    /// <summary>Positivo suma saldo, negativo lo consume.</summary>
    public decimal Amount { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public long? PaymentId { get; private set; }

    public long? DocumentId { get; private set; }

    /// <summary>Quién hizo un ajuste a mano, como en la auditoría. Nulo en los movimientos que arma el sistema.</summary>
    public string? Actor { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
