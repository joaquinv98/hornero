using System.Text.Json.Nodes;
using BillingOss.Modules.Hosting.Testing;
using BillingOss.Modules.Sdk;

namespace BillingOss.Modules.Hosting.Tests;

/// <summary>Una cuenta del panel falso.</summary>
internal sealed class FakeAccount
{
    public string Domain { get; set; } = "example.test";

    public string Plan { get; set; } = "basico";

    public bool Suspended { get; set; }

    public string? Marker { get; set; }

    public string Password { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string? Name { get; set; }
}

/// <summary>
/// Un panel en memoria detrás de <see cref="IHostingPanel"/>, para probar las operaciones comunes sin ningún protocolo de
/// panel. Respeta el plazo como el transporte de verdad: un cambio no sale si no queda tiempo. Cada test puede pisar la
/// respuesta de un cambio, cortar una lectura y adelantar el reloj en cada llamada.
/// </summary>
internal sealed class FakePanel
{
    public static readonly string[] AllPlans = ["basico", "pro", "Plan Empresa"];

    private readonly Dictionary<string, Func<Func<Ack>, Ack>> overrides = new(StringComparer.Ordinal);
    private readonly List<string> calls = [];

    public Dictionary<string, FakeAccount> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool KeepsMarker { get; init; }

    /// <summary>Si está, cada llamada adelanta este reloj: así los tests controlan el plazo de las operaciones.</summary>
    public StepClock? Clock { get; init; }

    public TimeSpan Step { get; set; }

    /// <summary>Cuántas lecturas de cuenta fallan como no disponibles antes de volver a andar. -1 es siempre.</summary>
    public int FailingReads { get; set; }

    /// <summary>La URL que devuelve el acceso directo, para probar el control del host.</summary>
    public Uri? SsoUrl { get; set; }

    public string? LastClientIp { get; private set; }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    /// <summary>Los cambios que llegaron al panel, con el usuario: "create:c...", "suspend:c...".</summary>
    public string[] Mutations => Calls.Where(c => !c.StartsWith("read:", StringComparison.Ordinal) && !c.StartsWith("owner:", StringComparison.Ordinal)
        && !c.StartsWith("plans", StringComparison.Ordinal) && !c.StartsWith("sso:", StringComparison.Ordinal) && !c.StartsWith("usage:", StringComparison.Ordinal)).ToArray();

    public static ModuleDefinition Module(FakePanel panel, TimeProvider? time = null) =>
        HostingModule.Create(HostingModule.Manifest("fake", "Panel falso", "0.1.0", new JsonObject { ["type"] = "object" }, "Plan del panel"), panel.Open, time);

    public FakePanel With(string username, string domain = "example.test", string plan = "basico", bool suspended = false, string? marker = null)
    {
        Accounts[username] = new FakeAccount { Domain = domain, Plan = plan, Suspended = suspended, Marker = marker };
        return this;
    }

    /// <summary>
    /// Pisa la respuesta de un cambio: create, suspend, unsuspend, delete, change_plan o change_password. La función recibe
    /// el cambio normal, por si lo quiere aplicar.
    /// </summary>
    public FakePanel On(string change, Func<Func<Ack>, Ack> respond)
    {
        overrides[change] = respond;
        return this;
    }

    /// <summary>El cambio salió y no hubo respuesta. Con apply, el panel llegó a aplicarlo.</summary>
    public FakePanel Lost(string change, bool apply) => On(change, run =>
    {
        if (apply)
        {
            run();
        }

        return Ack.NoReply;
    });

    public FakePanel Refuse(string change, string text) => On(change, _ => Ack.Refused(text));

    public IHostingPanel Open(ModuleCall call, Budget budget, IReadOnlyList<string?> hidden) => new Session(this, budget);

    private void Note(string call)
    {
        Clock?.Advance(Step);
        lock (calls)
        {
            calls.Add(call);
        }
    }

    private sealed class Session(FakePanel fake, Budget budget) : IHostingPanel
    {
        public string UsernamePrefix => "c";

        public Uri LoginUrl { get; } = new("https://panel.example.test:2083/");

        public bool KeepsMarker => fake.KeepsMarker;

        public Task<IReadOnlyList<string>> Plans()
        {
            fake.Note("plans");
            return Task.FromResult<IReadOnlyList<string>>(AllPlans);
        }

        public Task<AccountState> Read(PanelAccount account)
        {
            fake.Note($"read:{account.Username}");
            if (fake.FailingReads != 0)
            {
                fake.FailingReads = Math.Max(-1, fake.FailingReads - 1);
                throw Fail.Unavailable("El panel falso no respondió a la lectura.", "no_response");
            }

            lock (fake.Accounts)
            {
                return Task.FromResult(fake.Accounts.TryGetValue(account.Username, out var a)
                    ? new AccountState
                    {
                        Exists = true,
                        OwnsDomain = string.Equals(a.Domain, account.Domain, StringComparison.OrdinalIgnoreCase),
                        Plan = a.Plan,
                        Suspended = a.Suspended,
                        Marker = fake.KeepsMarker ? a.Marker : null,
                    }
                    : AccountState.Absent);
            }
        }

        public Task<string?> DomainOwner(string domain)
        {
            fake.Note($"owner:{domain}");
            lock (fake.Accounts)
            {
                return Task.FromResult<string?>(fake.Accounts.FirstOrDefault(a => string.Equals(a.Value.Domain, domain, StringComparison.OrdinalIgnoreCase)).Key);
            }
        }

        public Task<Ack> Create(NewAccount account) => Change("create", account.Username, () =>
        {
            if (fake.Accounts.TryGetValue(account.Username, out var existing))
            {
                // Lo que le falta a una cuenta con la misma marca: el dominio, como el cliente de Plesk sin su suscripción.
                if (existing.Marker != account.Marker || existing.Domain.Length > 0)
                {
                    return Ack.Refused($"La cuenta {account.Username} ya existe");
                }

                existing.Domain = account.Domain;
                existing.Plan = account.Plan;
                return Ack.Done;
            }

            if (!AllPlans.Contains(account.Plan))
            {
                return Ack.Refused($"No existe el plan {account.Plan}");
            }

            fake.Accounts[account.Username] = new FakeAccount
            {
                Domain = account.Domain,
                Plan = account.Plan,
                Password = account.Password,
                Email = account.Email,
                Name = account.Name,
                Marker = account.Marker,
            };
            return Ack.Done;
        });

        public Task<Ack> Suspend(PanelAccount account) => Change("suspend", account.Username, () => Set(account, a => a.Suspended = true));

        public Task<Ack> Unsuspend(PanelAccount account) => Change("unsuspend", account.Username, () => Set(account, a => a.Suspended = false));

        public Task<Ack> Delete(PanelAccount account) => Change("delete", account.Username, () =>
            fake.Accounts.Remove(account.Username) ? Ack.Done : Ack.Refused("No existe la cuenta"));

        public Task<Ack> ChangePlan(PanelAccount account, string plan) => Change("change_plan", account.Username, () =>
            AllPlans.Contains(plan) ? Set(account, a => a.Plan = plan) : Ack.Refused($"No existe el plan {plan}"));

        public Task<Ack> ChangePassword(PanelAccount account, string password) => Change("change_password", account.Username, () =>
            Set(account, a => a.Password = password));

        public Task<SsoLink> Sso(PanelAccount account, string? clientIp)
        {
            fake.Note($"sso:{account.Username}");
            fake.LastClientIp = clientIp;
            return Task.FromResult(new SsoLink(
                fake.SsoUrl ?? new Uri($"https://panel.example.test:2083/sesion/{account.Username}"),
                new DateTimeOffset(2026, 10, 29, 15, 0, 0, TimeSpan.Zero)));
        }

        public Task<PanelUsage> Usage(PanelAccount account)
        {
            fake.Note($"usage:{account.Username}");
            return Task.FromResult(new PanelUsage(1536, 120.5m));
        }

        private Ack Set(PanelAccount account, Action<FakeAccount> change)
        {
            if (!fake.Accounts.TryGetValue(account.Username, out var a))
            {
                return Ack.Refused("No existe la cuenta");
            }

            change(a);
            return Ack.Done;
        }

        /// <summary>Un cambio: como el transporte de verdad, no sale si no queda tiempo para mandarlo.</summary>
        private Task<Ack> Change(string name, string username, Func<Ack> apply)
        {
            if (budget.Remaining < PanelTransport.MutatorRoom)
            {
                throw new OutOfTimeException();
            }

            fake.Note($"{name}:{username}");
            Ack Run()
            {
                lock (fake.Accounts)
                {
                    return apply();
                }
            }

            return Task.FromResult(fake.overrides.TryGetValue(name, out var respond) ? respond(Run) : Run());
        }
    }
}
