namespace BillingOss.App;

/// <summary>Ajustes de la emisión fiscal (sección Fiscal de la configuración).</summary>
public sealed class FiscalOptions
{
    /// <summary>
    /// Habilita emitir contra el fisco real. Apagada por defecto: mientras lo esté, el núcleo no llama a ninguna
    /// operación que pueda autorizar un comprobante en una instancia fiscal de producción. La emisión queda
    /// pendiente con el motivo y su trabajo, muerto y visible. Se prende recién en el corte, con el OK de quien factura.
    /// </summary>
    public bool ProductionEnabled { get; set; }
}
