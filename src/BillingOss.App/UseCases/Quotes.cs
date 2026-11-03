using System.Globalization;
using BillingOss.App.Notifications;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.UseCases;

/// <summary>Sin moneda, la de la cuenta. Sin vigencia, la de siempre (Billing:QuoteValidDays).</summary>
public sealed record QuoteInput(long AccountId, string Subject, IReadOnlyList<QuoteLine> Lines, DateOnly? ValidUntil = null, string? Currency = null, string? Notes = null);

public enum QuoteTarget
{
    Invoice,
    Order,
}

public sealed record ConvertedQuote(long QuoteId, QuoteStatus Status, long? DocumentId, long? OrderId);

/// <summary>
/// Presupuestos (sección 6.5 del modelo de dominio): el staff los arma y los manda, el cliente los acepta o rechaza
/// desde el portal mientras están vigentes, y aceptados se convierten una sola vez en una factura o en un pedido. Cada
/// paso toma el presupuesto bloqueado, así dos conversiones a la vez no arman dos facturas.
/// </summary>
public sealed class QuoteService(BillingDbContext db, BillingCore core, InvoiceService invoices, OrderService orders, OrderValidation validation, Notifier notifier, AuditLog audit, TimeProvider time)
{
    public async Task<long> Create(QuoteInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var quote = new Quote(input.AccountId, time.GetUtcNow(), Actor.Current.Id);
        await Fill(quote, input, ct).ConfigureAwait(false);
        db.Quotes.Add(quote);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("quote.create", Entity(quote.Id), new { account_id = input.AccountId, lines = input.Lines.Count }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return quote.Id;
    }

    /// <summary>
    /// Un borrador nuevo con lo mismo que otro presupuesto, en cualquier estado: asunto, moneda, renglones y notas, con
    /// la vigencia de siempre desde hoy y el total con la ficha de hoy.
    /// </summary>
    public async Task<long> Duplicate(long quoteId, CancellationToken ct = default)
    {
        var source = await db.Quotes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == quoteId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el presupuesto {quoteId}.");
        return await Create(new QuoteInput(source.AccountId, source.Subject, source.Lines, Currency: source.Currency.Code, Notes: source.Notes), ct).ConfigureAwait(false);
    }

    /// <summary>Cambia un presupuesto abierto. Si ya se mandó, el cliente ve la versión nueva.</summary>
    public async Task Update(long quoteId, QuoteInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var quote = await Lock(quoteId, ct).ConfigureAwait(false);
        if (quote.AccountId != input.AccountId)
        {
            throw new ValidationException("Un presupuesto no cambia de cuenta.");
        }

        EnsureOpen(quote);
        await Fill(quote, input, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("quote.update", Entity(quoteId), new { lines = input.Lines.Count }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Lo deja a la vista del cliente y le manda el aviso. Mandarlo de nuevo manda otro aviso.</summary>
    public async Task Send(long quoteId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var quote = await Lock(quoteId, ct).ConfigureAwait(false);
        EnsureOpen(quote);
        if (quote.IsExpired(core.Today()))
        {
            throw new ValidationException("El presupuesto está vencido: extendé la vigencia antes de mandarlo.");
        }

        quote.Send(time.GetUtcNow());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == quote.AccountId, ct).ConfigureAwait(false);
        var culture = TemplateRenderer.CultureFor(account.Language);
        var model = new Dictionary<string, object?>
        {
            ["account_name"] = account.Name,
            ["number"] = quote.Id.ToString(CultureInfo.InvariantCulture),
            ["subject"] = quote.Subject,
            ["valid_until"] = Notifier.Date(quote.ValidUntil, culture),
            ["total"] = Notifier.Money(quote.Total, quote.Currency, culture),
            ["notes"] = quote.Notes,
            ["lines"] = quote.Lines.Select(l => new Dictionary<string, object?>
            {
                ["description"] = l.Description,
                ["amount"] = Notifier.Money(Domain.Shared.Rounding.Commercial(l.Quantity * l.UnitPrice), quote.Currency, culture),
            }).ToList(),
        };
        await notifier.Queue(TemplateKeys.Quote, account.Email, account.Language, model, $"{TemplateKeys.Quote}:{quote.Id}:{quote.SentAt:yyyyMMddHHmmssfff}", account.Id, ct).ConfigureAwait(false);
        await audit.Record("quote.send", Entity(quoteId), ct: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lo convierte el staff, por ejemplo aceptado por teléfono: abierto, aunque esté vencido. A factura va con todos sus
    /// renglones. A pedido, los renglones con plan crean sus servicios con el precio pactado y el resto va en la misma
    /// factura.
    /// </summary>
    public async Task<ConvertedQuote> Convert(long quoteId, QuoteTarget target, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        var validated = target == QuoteTarget.Order ? await ValidateOrder(quoteId, null, ct).ConfigureAwait(false) : null;
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var quote = await Lock(quoteId, ct).ConfigureAwait(false);
        var result = await ConvertLocked(quote, target, validated, ct).ConfigureAwait(false);
        await audit.Record("quote.convert", Entity(quoteId), new { target = target.ToString(), document_id = result.DocumentId, order_id = result.OrderId }, ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Lo acepta el cliente desde el portal, mandado y vigente: pasa a pedido si cotiza planes en la moneda de la cuenta
    /// y si no a factura, como lo convertiría el staff. Uno ajeno o en borrador da lo mismo que uno que no existe.
    /// </summary>
    public async Task<ConvertedQuote> Accept(long quoteId, IReadOnlyCollection<long> accounts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        db.ChangeTracker.Clear();
        var validated = await ValidateOrder(quoteId, accounts, ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var quote = await LockVisible(quoteId, accounts, ct).ConfigureAwait(false);
        if (quote.Status != QuoteStatus.Sent || quote.IsExpired(core.Today()))
        {
            throw new ValidationException("El presupuesto ya no se puede aceptar.");
        }

        var target = quote.HasPlans && quote.Currency == await AccountCurrency(quote.AccountId, ct).ConfigureAwait(false) ? QuoteTarget.Order : QuoteTarget.Invoice;
        var result = await ConvertLocked(quote, target, validated, ct).ConfigureAwait(false);
        await audit.Record("quote.accept", Entity(quoteId), new { document_id = result.DocumentId, order_id = result.OrderId }, ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Lo rechaza el cliente (con <paramref name="accounts"/>, solo uno mandado) o el staff, abierto.</summary>
    public async Task Decline(long quoteId, IReadOnlyCollection<long>? accounts = null, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var quote = accounts is null ? await Lock(quoteId, ct).ConfigureAwait(false) : await LockVisible(quoteId, accounts, ct).ConfigureAwait(false);
        if (!quote.IsOpen || (accounts is not null && quote.Status != QuoteStatus.Sent))
        {
            throw new ValidationException("El presupuesto ya está cerrado.");
        }

        quote.Decline(time.GetUtcNow());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("quote.decline", Entity(quoteId), ct: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Solo un borrador se borra: uno que el cliente vio se rechaza.</summary>
    public async Task Delete(long quoteId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var quote = await Lock(quoteId, ct).ConfigureAwait(false);
        if (quote.Status != QuoteStatus.Draft)
        {
            throw new ValidationException("Solo se borra un borrador: uno que el cliente vio se marca rechazado.");
        }

        db.Quotes.Remove(quote);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("quote.delete", Entity(quoteId), ct: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static LineInput ToLine(QuoteLine l) => new(l.Description, l.Quantity, l.UnitPrice, l.Treatment);

    private static string Entity(long quoteId) => string.Create(CultureInfo.InvariantCulture, $"quote:{quoteId}");

    /// <summary>
    /// Los renglones como los factura el motor de impuestos: uno con plan lleva la categoría del producto, la misma con
    /// que el pedido lo factura (telefonía al 27 %, exento), y uno suelto la de su tratamiento. Así el total que el
    /// cliente acepta es el que se le factura, a factura o a pedido.
    /// </summary>
    private async Task<List<LineInput>> Resolve(IReadOnlyList<QuoteLine> lines, CancellationToken ct)
    {
        var planIds = lines.Where(l => l.PlanId is not null).Select(l => l.PlanId!.Value).Distinct().ToList();
        var categories = planIds.Count == 0 ? [] : await db.Plans.Where(p => planIds.Contains(p.Id))
            .Join(db.Products, p => p.ProductId, x => x.Id, (p, x) => new { p.Id, x.TaxCategory })
            .ToDictionaryAsync(x => x.Id, x => x.TaxCategory, ct).ConfigureAwait(false);
        return [.. lines.Select(l => ToLine(l) with { Category = l.PlanId is { } plan ? categories.GetValueOrDefault(plan) : null })];
    }

    private Task<Currency> AccountCurrency(long accountId, CancellationToken ct) =>
        db.Accounts.Where(a => a.Id == accountId).Select(a => a.Currency).SingleAsync(ct);

    /// <summary>
    /// Las líneas del pedido de un presupuesto, cada una con su precio pactado. Un producto que no se pide por cantidad va
    /// de a una unidad por línea, como en la tienda: su renglón por N pasa a N líneas de una, con el mismo precio.
    /// </summary>
    private async Task<List<(OrderItem Item, decimal UnitPrice)>> Plans(Quote quote, CancellationToken ct)
    {
        var planIds = quote.Lines.Where(l => l.PlanId is not null).Select(l => l.PlanId!.Value).Distinct().ToList();
        var byQuantity = await db.Plans.Where(p => planIds.Contains(p.Id))
            .Join(db.Products, p => p.ProductId, x => x.Id, (p, x) => new { p.Id, x.AllowQuantity })
            .ToDictionaryAsync(x => x.Id, x => x.AllowQuantity, ct).ConfigureAwait(false);
        return [.. quote.Lines.Where(l => l.PlanId is not null).SelectMany(l => byQuantity.GetValueOrDefault(l.PlanId!.Value)
            ? Enumerable.Repeat((new OrderItem(l.PlanId!.Value, (int)l.Quantity), l.UnitPrice), 1)
            : Enumerable.Repeat((new OrderItem(l.PlanId!.Value), l.UnitPrice), (int)l.Quantity))];
    }

    /// <summary>
    /// Las extensiones que validan pedidos ven los planes de un presupuesto que va a pasar a pedido antes de abrir la
    /// transacción, como cualquier pedido: nunca se llama a un módulo con una abierta. Con <paramref name="accounts"/> es
    /// el cliente, y uno que no ve no se valida. Devuelve lo validado, o null si no va a pedido.
    /// </summary>
    private async Task<List<OrderItem>?> ValidateOrder(long quoteId, IReadOnlyCollection<long>? accounts, CancellationToken ct)
    {
        var quote = await db.Quotes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == quoteId, ct).ConfigureAwait(false);
        if (quote is null || !quote.HasPlans || (accounts is not null && (!accounts.Contains(quote.AccountId) || quote.Status == QuoteStatus.Draft))
            || quote.Currency != await AccountCurrency(quote.AccountId, ct).ConfigureAwait(false))
        {
            return null;
        }

        var items = (await Plans(quote, ct).ConfigureAwait(false)).ConvertAll(p => p.Item);
        await validation.Check(quote.AccountId, items, ct).ConfigureAwait(false);
        return items;
    }

    private static void EnsureOpen(Quote quote)
    {
        if (!quote.IsOpen)
        {
            throw new ValidationException("El presupuesto ya está cerrado: armá otro.");
        }
    }

    private async Task<ConvertedQuote> ConvertLocked(Quote quote, QuoteTarget target, IReadOnlyList<OrderItem>? validated, CancellationToken ct)
    {
        EnsureOpen(quote);
        var now = time.GetUtcNow();
        if (target == QuoteTarget.Invoice)
        {
            // La clave de cada renglón impide que el mismo presupuesto entre en dos facturas.
            var mode = (await core.Settings(ct).ConfigureAwait(false)).DefaultPriceMode(quote.Currency);
            var draft = new Document(DocumentKind.Invoice, quote.AccountId, quote.Currency, mode, core.Today().AddDays(core.Options.InvoiceDueDays), now, reason: DocumentReasons.Manual);
            await invoices.AddLines(draft, await Resolve(quote.Lines, ct).ConfigureAwait(false), LineKind.Manual, ct, i => string.Create(CultureInfo.InvariantCulture, $"quote:{quote.Id}:{i}")).ConfigureAwait(false);
            db.Documents.Add(draft);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await invoices.IssueAndApplyCredit(draft, ct).ConfigureAwait(false);
            quote.Invoiced(draft.Id, now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new ConvertedQuote(quote.Id, quote.Status, draft.Id, null);
        }

        if (!quote.HasPlans)
        {
            throw new ValidationException("Un presupuesto sin planes se convierte en factura.");
        }

        if (await AccountCurrency(quote.AccountId, ct).ConfigureAwait(false) != quote.Currency)
        {
            throw new ValidationException("Un pedido va en la moneda de la cuenta: este presupuesto se convierte en factura.");
        }

        // Las extensiones validaron los planes antes de abrir la transacción: tienen que ser los que se piden.
        var plans = await Plans(quote, ct).ConfigureAwait(false);
        var items = plans.ConvertAll(p => p.Item);
        if (validated is null || !items.SequenceEqual(validated))
        {
            throw new ValidationException("El presupuesto cambió mientras se convertía: probá de nuevo.");
        }

        var terms = new QuotedTerms([.. plans.Select(p => p.UnitPrice)], [.. quote.Lines.Where(l => l.PlanId is null).Select(ToLine)]);
        var placed = await orders.PlaceOrder(quote.AccountId, items, ct, Entity(quote.Id), quoted: terms).ConfigureAwait(false);
        quote.Ordered(placed.OrderId, placed.InvoiceId, now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new ConvertedQuote(quote.Id, quote.Status, placed.InvoiceId, placed.OrderId);
    }

    /// <summary>Valida y completa el presupuesto, con el total que daría hoy la factura: el mismo cálculo de impuestos.</summary>
    private async Task Fill(Quote quote, QuoteInput input, CancellationToken ct)
    {
        var subject = input.Subject?.Trim() ?? string.Empty;
        if (subject.Length is 0 or > 200)
        {
            throw new ValidationException("El presupuesto necesita un asunto de hasta 200 caracteres.");
        }

        var notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        if (notes is { Length: > 4000 })
        {
            throw new ValidationException("Las notas van hasta 4000 caracteres.");
        }

        var lines = input.Lines ?? [];
        InvoiceService.Validate([.. lines.Select(ToLine)]);
        // Con dos decimales, desdoblar un renglón por unidades (Plans) no cambia el total.
        if (lines.Any(l => l.PlanId is not null && (l.Quantity != decimal.Truncate(l.Quantity) || l.UnitPrice < 0m || !Rounding.HasAtMostDecimals(l.UnitPrice))))
        {
            throw new ValidationException("Un renglón con plan va con cantidad entera y un precio que no sea negativo, con dos decimales como máximo.");
        }

        var planIds = lines.Where(l => l.PlanId is not null).Select(l => l.PlanId!.Value).Distinct().ToList();
        var products = planIds.Count == 0 ? [] : await db.Plans.Where(p => planIds.Contains(p.Id) && p.Active)
            .Join(db.Products, p => p.ProductId, x => x.Id, (p, x) => new { p.Id, x.Name, x.Addon, x.AllowQuantity })
            .ToDictionaryAsync(x => x.Id, ct).ConfigureAwait(false);
        if (products.Count != planIds.Count)
        {
            throw new ValidationException("Hay planes que no existen o no están activos.");
        }

        // Las reglas por línea del catálogo, que el pedido aplicaría recién al aceptarlo.
        if (products.Values.FirstOrDefault(p => p.Addon) is { } addon)
        {
            throw new ValidationException($"{addon.Name} es un complemento y va colgado de un servicio: su plan no se cotiza en un presupuesto. Cotizalo como renglón suelto.");
        }

        if (lines.Sum(l => l.PlanId is { } plan && !products[plan].AllowQuantity ? l.Quantity : 1m) > InvoiceService.MaxLines)
        {
            throw new ValidationException($"Cada unidad de un producto que no se pide por cantidad pasa a pedido como un servicio aparte: la factura del pedido tendría más de {InvoiceService.MaxLines} renglones.");
        }

        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == input.AccountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la cuenta {input.AccountId}.");
        var currency = input.Currency is null ? account.Currency : InvoiceService.ParseCurrency(input.Currency);
        var today = core.Today();
        var validUntil = input.ValidUntil ?? today.AddDays(core.Options.QuoteValidDays);
        if (validUntil < today)
        {
            throw new ValidationException("La vigencia no puede terminar antes de hoy.");
        }

        // El total sale del mismo motor que la factura: los precios en el modo de carga por defecto de la moneda, cada
        // renglón con su categoría y el receptor de hoy, y los tributos de comprobante que le tocarían al emitir.
        var receiver = await core.Receiver(account.Id, ct).ConfigureAwait(false);
        var taxes = await core.Taxes(ct).ConfigureAwait(false);
        var draft = new Document(DocumentKind.Invoice, account.Id, currency, taxes.Settings.DefaultPriceMode(currency), today, time.GetUtcNow());
        InvoiceService.AddTaxed(draft, await Resolve(lines, ct).ConfigureAwait(false), LineKind.Manual, taxes, receiver, today);
        var total = InvoiceService.Total(draft, taxes, receiver, today);
        if (total <= 0m)
        {
            throw new ValidationException("El total tiene que dar más que cero.");
        }

        quote.Set(subject, currency, validUntil, [.. lines.Select(l => l with { Description = l.Description.Trim() })], notes, total, time.GetUtcNow());
    }

    private async Task<Quote> Lock(long quoteId, CancellationToken ct)
    {
        await Sql.LockRows(db, "billing.quotes", [quoteId], ct).ConfigureAwait(false);
        return await db.Quotes.SingleOrDefaultAsync(q => q.Id == quoteId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el presupuesto {quoteId}.");
    }

    /// <summary>El presupuesto como lo ve el cliente: de una de sus cuentas y ya mandado.</summary>
    private async Task<Quote> LockVisible(long quoteId, IReadOnlyCollection<long> accounts, CancellationToken ct)
    {
        var quote = await Lock(quoteId, ct).ConfigureAwait(false);
        return accounts.Contains(quote.AccountId) && quote.Status != QuoteStatus.Draft
            ? quote
            : throw new KeyNotFoundException($"No existe el presupuesto {quoteId}.");
    }
}
