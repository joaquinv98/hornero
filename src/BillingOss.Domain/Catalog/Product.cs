using BillingOss.Domain.Billing;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Catalog;

public enum ProductKind
{
    /// <summary>Lo aprovisiona un módulo.</summary>
    Provisioned,

    /// <summary>Lo hace el staff a mano, por ejemplo bare metal.</summary>
    Manual,

    Domain,

    OneOff,
}

/// <summary>Cuándo se crea el servicio de un pedido en su módulo, o en el núcleo si no tiene.</summary>
public enum ProductActivation
{
    /// <summary>Cuando se paga la primera factura, o enseguida si no hay nada que pagar.</summary>
    OnPayment,

    /// <summary>Apenas se confirma el pedido, sin esperar el pago.</summary>
    OnOrder,

    /// <summary>Cuando el staff acepta el pedido. El pago no lo activa.</summary>
    Manual,
}

/// <summary>Grupo de productos: ordena el catálogo en la tienda y en el admin.</summary>
public sealed class ProductGroup
{
    public long Id { get; private set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Bajada que acompaña al nombre en la tienda.</summary>
    public string? Headline { get; set; }

    public bool Visible { get; set; } = true;

    public int SortOrder { get; set; }

    public void SetImportedId(long id) => Id = id;
}

/// <summary>Lo que se vende (sección 3 del modelo de dominio y diseno/catalogo.md).</summary>
public sealed class Product
{
    public long Id { get; private set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Texto para la tienda y la API, sin HTML.</summary>
    public string? Description { get; set; }

    public long? GroupId { get; set; }

    /// <summary>Orden dentro del grupo.</summary>
    public int SortOrder { get; set; }

    public ProductKind Kind { get; set; }

    public bool Visible { get; set; } = true;

    /// <summary>Se puede pedir más de una unidad en la misma línea.</summary>
    public bool AllowQuantity { get; set; }

    /// <summary>Días de prueba gratis del primer período. Una sola por cuenta y producto.</summary>
    public int? TrialDays { get; set; }

    /// <summary>Complemento: se contrata colgado de un servicio vivo, como servicio hijo (sección 5 del modelo).</summary>
    public bool Addon { get; set; }

    /// <summary>De qué productos se puede colgar el complemento. Vacío es de cualquiera.</summary>
    public long[] ParentProductIds { get; set; } = [];

    /// <summary>A qué otros productos puede pasar el cliente un servicio de este. El staff puede pasarlo a cualquiera.</summary>
    public long[] UpgradeProductIds { get; set; } = [];

    /// <summary>El cliente puede cambiar las opciones de su servicio. El staff puede siempre.</summary>
    public bool OptionChanges { get; set; }

    /// <summary>Categoría a efectos del impuesto (<see cref="TaxCategory.Code"/>). Si no sabés, servicios digitales y hosting.</summary>
    public string TaxCategory { get; set; } = TaxCategories.Digital;

    public long? ModuleInstanceId { get; set; }

    /// <summary>Parámetros para el módulo, validados contra su esquema. JSON.</summary>
    public string ModuleParams { get; set; } = "{}";

    /// <summary>Cuándo se aprovisiona el servicio de un pedido: al pagar (lo de siempre), al pedir o cuando lo acepta el staff.</summary>
    public ProductActivation Activation { get; set; }

    /// <summary>Unidades disponibles. Nulo es sin control de stock. Cada pedido las descuenta.</summary>
    public int? Stock { get; set; }

    /// <summary>Días de mora hasta la baja, si el producto no sigue el plazo general.</summary>
    public int? TerminationDays { get; set; }

    public void SetImportedId(long id) => Id = id;
}

public sealed class Plan
{
    private Plan()
    {
    }

    public Plan(long productId, BillingCycle cycle)
    {
        ProductId = productId;
        CycleUnit = cycle.Unit;
        CycleCount = cycle.Count;
    }

    public long Id { get; private set; }

    public long ProductId { get; private set; }

    public CycleUnit CycleUnit { get; private set; }

    public int CycleCount { get; private set; }

    public bool Active { get; set; } = true;

    public BillingCycle Cycle => new(CycleUnit, CycleCount);
}

/// <summary>
/// Precio de un plan en una moneda, con vigencia y con su modo de carga: dice si el importe lleva el impuesto
/// incluido o no. Cambiarlo no toca los servicios existentes, que congelan precio y modo al contratar.
/// </summary>
public sealed class PlanPrice
{
    private PlanPrice()
    {
    }

    public PlanPrice(long planId, Currency currency, decimal amount, decimal setupFee, DateOnly validFrom, PriceMode priceMode = PriceMode.TaxIncluded)
    {
        PlanId = planId;
        Currency = currency;
        ValidFrom = validFrom;
        PriceMode = priceMode;
        Change(amount, setupFee);
    }

    public long Id { get; private set; }

    public long PlanId { get; private set; }

    public Currency Currency { get; private set; }

    public decimal Amount { get; private set; }

    public decimal SetupFee { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    /// <summary>Desde este día el plan no se vende en la moneda, porque la tabla de precios la sacó. Nulo sigue vigente.</summary>
    public DateOnly? ValidTo { get; private set; }

    /// <summary>Con impuesto incluido o sin impuesto. Las diferencias de las opciones configurables siguen este modo.</summary>
    public PriceMode PriceMode { get; private set; }

    /// <summary>No está cerrada el día <paramref name="on"/>. Se le pregunta a la última vigencia que empezó ese día o antes.</summary>
    public bool OpenOn(DateOnly on) => ValidTo is null || on < ValidTo;

    /// <summary>
    /// Otro importe para la misma vigencia: lo usa una corrección del mismo día, que no abre una vigencia nueva. Si la
    /// moneda se había sacado ese día, vuelve a venderse.
    /// </summary>
    public void Change(decimal amount, decimal setupFee)
    {
        if (amount < 0m || setupFee < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Un precio no puede ser negativo.");
        }

        Amount = amount;
        SetupFee = setupFee;
        ValidTo = null;
    }

    /// <summary>La tabla de precios sacó la moneda: el plan deja de venderse en ella desde <paramref name="on"/>.</summary>
    public void Close(DateOnly on) => ValidTo = on;
}
