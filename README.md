# Hornero

Free and open source billing and provisioning for hosting providers, built with .NET 10, PostgreSQL and
server-rendered Razor Pages with htmx.

The hornero (*Furnarius rufus*) is the Argentine bird that builds its own house. This project does the same for
hosting businesses: it gives them their own billing system instead of renting one.

> **Status: in development.** Hornero is not ready for production use by third parties yet. See
> [Project status](#project-status).

[Resumen en castellano](#resumen-en-castellano)

## Who it is for

Hosting companies, VPS and game server providers, managed service providers (MSPs) and resellers that need
customer billing, invoicing and automatic provisioning in one place. It is aimed especially at markets where a
commercial license priced in US dollars per active client, like WHMCS, is hard to justify, and at providers who
want to own and audit the system that handles their customers' money.

You can migrate from WHMCS, but Hornero does not depend on it: it is an independent product with its own data
model, its own API and its own modules.

## Features

**Billing core**
- Accounts with several users and roles, and a catalog with product groups, configurable options, add-ons,
  stock, free trials, bundles, coupons, multi-currency prices and plan changes with proration.
- Orders, services with their life cycle, renewals and dunning (grace, suspension, cancellation of unpaid
  invoices and termination), credit balance with top-ups from the portal, and refunds.
- Immutable invoices, credit and debit notes, manual invoices, quotes, billable items, late fees and configurable
  reminders. Taxes are computed per invoice and per rate, with dated tax rules, and exchange rates can be fetched
  from Banco Nación.
- Domains with registration, renewal, daily sync, DNS, email forwarding, ID protection and expiry notices.
- Usage-based billing with daily samples and overage lines on the invoice.
- Affiliate program with commissions, holds, reversals and payouts.

**Argentina first**
- Electronic invoicing with ARCA (WSAA, WSFEv1 and WSFEX), guided certificate setup, a read-only test before
  issuing and safety locks that keep production emission off until the operator turns it on.
- VAT conditions, perceptions, gross income regimes, Libro IVA Digital export and the consumer protection
  buttons (cancellation and withdrawal) required by Argentine law.

**Modules** (separate services that speak a signed HTTPS [module protocol](diseno/protocolo-modulos-v0.md))
- Provisioning: Proxmox VE, Pterodactyl, HestiaCP, cPanel and WHM, Plesk, DirectAdmin and Tactical RMM, plus a
  built-in manual module that turns operations into staff tasks.
- Payments: Mercado Pago, Stripe, PayPal and CryptoCloud, plus bank transfers reconciled by the payer's tax ID
  and automatic charges with saved payment methods or subscriptions.
- Domain registrars: Internet.bs, ResellerClub, OpenProvider and Namecheap.
- Fraud: MaxMind minFraud. Extensions: an example that allows one free trial per account.
- A C# SDK for new modules. A module can also be written in any language that speaks the protocol.

**Customers and staff**
- Customer portal and admin panel, in Spanish (es-AR) and English, with self sign-up, login with Google or any
  OpenID Connect provider, two-factor authentication, login history, blocklists and optional OIDC login for
  staff.
- Support tickets with inbound email, escalation rules and satisfaction surveys.
- Knowledge base, announcements with RSS, downloads and a network status page with incidents and maintenance
  windows.
- Email and PDF templates in Liquid, with invoices rendered to PDF by Gotenberg.
- Reports and accounting exports.

**Integrations**
- REST API (`/api/v1`) with scoped API keys, idempotency keys and Problem Details errors.
- Signed outgoing webhooks (Standard Webhooks) and extension modules that can validate orders.
- A WHMCS-compatible API subset at `/includes/api.php`, so existing integrations keep working after a
  migration.

**Migration and operations**
- Importer that reads a WHMCS database dump without running WHMCS, keeps the original IDs and invoice numbers,
  and reconciles balances. A "parallel month" mode replays daily billing and compares it with the old system.
- Docker Compose recipe for a single host and a high availability recipe on VMs (Patroni with synchronous
  replication, etcd, pgBackRest, HAProxy or keepalived).
- OpenTelemetry logs, metrics and traces, health endpoints, business metrics and monitoring templates.
- A per-node updater that applies verified releases, with rollback.

## Getting started

### With Docker

The single host recipe runs the core (web and worker), PostgreSQL 18 with TLS, the ARCA, Mercado Pago, Stripe,
PayPal and CryptoCloud modules and Gotenberg with Docker Compose.

```sh
docker build -f deploy/Dockerfile --target nucleo      -t billing-oss:0.1.0 .
docker build -f deploy/Dockerfile --target arca        -t billing-oss-arca:0.1.0 .
docker build -f deploy/Dockerfile --target mercadopago -t billing-oss-mercadopago:0.1.0 .
docker build -f deploy/Dockerfile --target stripe      -t billing-oss-stripe:0.1.0 .
docker build -f deploy/Dockerfile --target paypal      -t billing-oss-paypal:0.1.0 .
docker build -f deploy/Dockerfile --target cryptocloud -t billing-oss-cryptocloud:0.1.0 .

cd deploy/compose
cp .env.example .env          # set BILLING_VERSION and BILLING_PUBLIC_URL
sudo ./preparar.sh            # creates secrets/, keys and internal certificates
docker compose up -d postgres
docker compose run --rm migrate
docker compose up -d
curl -fsS http://127.0.0.1:8080/health/ready
```

Back up `secrets/` before anything else: the `llaves` file is required to read module credentials and the ARCA
certificate. The full guide, including the reverse proxy, modules and backups, is in
[docs/despliegue/receta-chica.md](docs/despliegue/receta-chica.md). High availability is described in
[docs/despliegue/ha-vms.md](docs/despliegue/ha-vms.md).

### With .NET

You need the .NET 10 SDK (the exact version is in `global.json`) and PostgreSQL 17 or later.

```sh
dotnet build BillingOss.slnx
dotnet test tests/BillingOss.Domain.Tests/BillingOss.Domain.Tests.csproj

export ConnectionStrings__Main="Host=localhost;Database=billing_oss;Username=billing;Password=..."
dotnet run --project src/BillingOss.Web -- migrate
dotnet run --project src/BillingOss.Web
```

`./build.ps1` builds the solution and runs every test. Integration tests use the PostgreSQL server in
`BILLINGOSS_TEST_PG` and are skipped without it. All settings are listed in
[docs/configuracion.md](docs/configuracion.md).

## Documentation

The documentation is written in Spanish for now.

- [docs/configuracion.md](docs/configuracion.md): configuration keys, API limits and staff operations.
- [diseno/](diseno/README.md): the core design, the module protocol and the architecture decisions.
- [docs/despliegue/](docs/despliegue/README.md): deployment, high availability, backups and updates.
- [docs/interfaz/](docs/interfaz/README.md): customer portal, admin panel and identity.
- [docs/facturacion/](docs/facturacion/README.md), [docs/cobros/](docs/cobros/README.md) and
  [docs/dominios/](docs/dominios/README.md): advanced billing, payments and domains.
- [docs/integraciones/](docs/integraciones/README.md): webhooks, extensions and the compatible API.
- [docs/fiscal/](docs/fiscal/README.md): ARCA setup and tax configuration.

## Project status

Hornero is under active development. Today:

- The core, the modules listed above, both APIs, the portal and the admin panel are built and covered by
  automated tests, but there is no tagged release yet and the database schema can still change.
- The design is a v0 draft. Some parts described in [diseno/](diseno/README.md) are not implemented yet.
- Fiscal emission is tested against the ARCA test environment (homologación). Production emission and outgoing
  email stay locked by default.
- Migration from WHMCS is being validated with real data in a parallel billing run before the first production
  cutover.
- The documentation is mostly in Spanish and the user interface is available in Spanish and English.

Expect breaking changes until version 1.0. Issues and feedback from providers who want to try it are very
welcome.

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) and follow the
[code of conduct](CODE_OF_CONDUCT.md).

## Sponsored by NeaTech

Hornero is sponsored by [NeaTech](https://neatech.ar), a hosting and IT services company from Argentina.

To run Hornero on a NeaTech VPS, use the coupon **HORNERO30**: 30% off the first 6 monthly invoices of a new
NeaTech VPS. It applies to the monthly billing cycle, new orders only and one use per customer, and it does not
expire. It includes help from NeaTech to install Hornero and with any problem along the way.

- [NeaTech VPS](https://neatech.ar/index.php?rp=/store/vps/argentina)
- [All NeaTech VPS plans](https://neatech.ar/index.php?rp=/store/vps)

## License

Hornero is free software, licensed under the GNU Affero General Public License, version 3 only
([AGPL-3.0-only](LICENSE)). If you run a modified version for others over a network, you must offer them its source
code.

The module SDK and protocol (`src/BillingOss.Modules.Protocol`, `src/BillingOss.Modules.Sdk` and
`src/BillingOss.Modules.Hosting`) and the bundled modules in `src/modules` are licensed under the
[Apache License 2.0](src/BillingOss.Modules.Sdk/LICENSE), so anyone can write a module under the license of their
choice. Each of those folders carries its own `LICENSE` file.

WHMCS is a trademark of its owner. Hornero is an independent project and is not affiliated with it.

---

## Resumen en castellano

Hornero es un sistema libre de facturación y aprovisionamiento para proveedores de hosting, VPS, game servers y
servicios gestionados, hecho con .NET 10, PostgreSQL y Razor Pages con htmx. Toma el nombre del hornero, el pájaro
argentino que construye su propia casa.

**Para quién.** Para empresas de hosting, MSP y revendedores que necesitan cobrar, facturar y aprovisionar en un
solo lugar, sobre todo en mercados donde una licencia comercial en dólares por cliente activo, como la de WHMCS,
es difícil de justificar. Se puede migrar desde WHMCS, pero Hornero no depende de él.

**Qué trae.** Cuentas, catálogo con opciones, complementos, cupones y cambios de plan, pedidos, servicios,
renovaciones y mora, facturas y notas inmutables, presupuestos, recargos, impuestos por factura y alícuota, factura
electrónica de ARCA, cobros con Mercado Pago, Stripe, PayPal, CryptoCloud y transferencias, dominios con Internet.bs,
ResellerClub, OpenProvider y Namecheap, módulos para Proxmox VE, Pterodactyl, HestiaCP, cPanel, Plesk, DirectAdmin
y Tactical RMM, portal del cliente con registro e ingreso con Google, panel del staff, soporte con correo entrante,
base de conocimiento, anuncios, descargas, estado de la red, afiliados, uso medido, API propia, webhooks firmados,
una API compatible con la de WHMCS, importador desde WHMCS y recetas de despliegue con Docker Compose y con alta
disponibilidad.

**Cómo levantarlo.** Con Docker, siguiendo la [receta chica](docs/despliegue/receta-chica.md). Con .NET,
compilando `BillingOss.slnx` con el SDK de .NET 10 y apuntando `ConnectionStrings__Main` a un PostgreSQL 17 o
posterior. La configuración completa está en [docs/configuracion.md](docs/configuracion.md).

**Estado.** Está en desarrollo y todavía no está listo para que otros lo usen en producción: no hay versión
publicada, el esquema de la base puede cambiar y la emisión fiscal de producción viene trabada por defecto.

**Cómo contribuir.** Leé [CONTRIBUTING.md](CONTRIBUTING.md). Los issues y los pedidos de cambio son bienvenidos.

**Patrocinio.** Hornero lo patrocina [NeaTech](https://neatech.ar). Con el cupón **HORNERO30** tenés un 30 % de
descuento en las primeras 6 facturas mensuales de un [VPS de NeaTech](https://neatech.ar/index.php?rp=/store/vps/argentina)
para correr Hornero, en pedidos nuevos con ciclo mensual y un uso por cliente, sin vencimiento. Incluye asistencia
de NeaTech para instalarlo y con los problemas. También podés ver [todos los VPS](https://neatech.ar/index.php?rp=/store/vps).

**Licencia.** [AGPL-3.0](LICENSE), solo la versión 3. El SDK y el protocolo de módulos y los módulos incluidos en
`src/modules` van con [Apache-2.0](src/BillingOss.Modules.Sdk/LICENSE), así cualquiera puede escribir un módulo con
la licencia que quiera.
