namespace BillingOss.Modules.Hosting;

/// <summary>La cuenta de hosting de un servicio en el panel: el usuario y el dominio que prueba que es suya.</summary>
public sealed record PanelAccount(string Username, string Domain);

/// <summary>
/// Lo que se leyó del panel sobre la cuenta. <see cref="Exists"/> en false es una ausencia probada: lo que no se pudo
/// leer sale como excepción <c>upstream_unavailable</c>, nunca como ausencia.
/// </summary>
public sealed record AccountState
{
    public static readonly AccountState Absent = new();

    public bool Exists { get; init; }

    /// <summary>La cuenta tiene el dominio del servicio. Sin él puede ser de otro cliente: no se toca.</summary>
    public bool OwnsDomain { get; init; }

    public string? Plan { get; init; }

    public bool Suspended { get; init; }

    /// <summary>
    /// La marca que guarda el panel (el uuid del servicio), en los paneles que tienen dónde guardarla, tal como está. Un
    /// valor que no es un uuid lo dejó otro sistema y no cuenta como marca.
    /// </summary>
    public string? Marker { get; init; }
}

/// <summary>Una cuenta nueva. La marca es el uuid del servicio.</summary>
public sealed record NewAccount(string Username, string Domain, string Plan, string Password, string Email, string? Name, string Marker);

/// <summary>
/// Lo que contestó el panel a un cambio. Nunca alcanza como prueba: el resultado lo decide la lectura posterior.
/// </summary>
public sealed record Ack(bool Ok, bool Lost, string? Refusal)
{
    public static readonly Ack Done = new(true, false, null);

    /// <summary>Se mandó y no hubo respuesta: pudo haberse aplicado.</summary>
    public static readonly Ack NoReply = new(false, true, null);

    /// <summary>El panel rechazó el cambio. El texto ya viene sin secretos.</summary>
    public static Ack Refused(string text) => new(false, false, text);
}

/// <summary>
/// El uso de la cuenta en MiB, como lo cuenta el panel: el disco ocupado y lo transferido en el mes calendario, que el
/// panel vuelve a cero cada mes. Null es una medida que el panel contestó sin un valor que se pueda leer.
/// </summary>
public sealed record PanelUsage(decimal? DiskMib, decimal? TransferMib);

/// <summary>Acceso directo al panel para el navegador, de un solo uso o de vida corta.</summary>
public sealed record SsoLink(Uri Url, DateTimeOffset? ExpiresAt);

/// <summary>
/// Un panel de hosting visto por el módulo. Cada instancia vale para una sola llamada del núcleo, con su plazo: las
/// lecturas que fallan tiran <c>upstream_unavailable</c>, los rechazos de autenticación <c>invalid_config</c>, y los
/// cambios devuelven un <see cref="Ack"/> que la lectura posterior confirma o no.
/// </summary>
public interface IHostingPanel
{
    /// <summary>Prefijo de los usuarios nuevos, ya validado.</summary>
    string UsernamePrefix { get; }

    /// <summary>El login del panel para el navegador, sin credenciales. Su host es el único que acepta el acceso directo.</summary>
    Uri LoginUrl { get; }

    /// <summary>Si el panel guarda la marca con el uuid del servicio. Sin eso, la marca es el usuario, que sale del uuid.</summary>
    bool KeepsMarker { get; }

    /// <summary>Los planes o paquetes del panel. Prueba también las credenciales.</summary>
    Task<IReadOnlyList<string>> Plans();

    Task<AccountState> Read(PanelAccount account);

    /// <summary>El usuario dueño del dominio en el panel, o null si nadie lo tiene.</summary>
    Task<string?> DomainOwner(string domain);

    /// <summary>Crea la cuenta, o lo que le falta a una que dejó un alta anterior con la misma marca.</summary>
    Task<Ack> Create(NewAccount account);

    Task<Ack> Suspend(PanelAccount account);

    Task<Ack> Unsuspend(PanelAccount account);

    Task<Ack> Delete(PanelAccount account);

    Task<Ack> ChangePlan(PanelAccount account, string plan);

    Task<Ack> ChangePassword(PanelAccount account, string password);

    /// <param name="clientIp">La IP del navegador si el núcleo la manda. Algunos paneles atan la sesión a ella.</param>
    Task<SsoLink> Sso(PanelAccount account, string? clientIp);

    Task<PanelUsage> Usage(PanelAccount account);
}
