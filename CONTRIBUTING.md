# Contributing to Hornero

Thanks for your interest in Hornero. Bug reports, fixes, new modules, translations and documentation are all
welcome. This guide explains how to work on the code and what a change needs before it can be merged.

[Versión en castellano más abajo](#contribuir-a-hornero).

## Before you start

- Search the existing issues first. For a bug, include the version or commit, what you did, what you expected
  and what happened, without real customer data, passwords or keys.
- For a new feature, a new module or a large change, open an issue to discuss it before writing the code. The
  design documents in [diseno/](diseno/README.md) explain why things are the way they are.
- Security problems must not be reported in public issues. Use the private vulnerability reporting of the
  repository (Security tab) instead.

## Development setup

- .NET 10 SDK, the version pinned in `global.json`.
- PostgreSQL 17 or later for the integration tests. Point `BILLINGOSS_TEST_PG` to a server where the test user
  can create and drop databases, for example
  `Host=localhost;Username=postgres;Password=...;Database=postgres`. Each test creates its own database and
  drops it at the end. Without the variable, integration tests are skipped.
- Docker, only if you work on the deployment recipes.

```sh
dotnet restore BillingOss.slnx --locked-mode
dotnet build BillingOss.slnx -c Release
dotnet test tests/BillingOss.Domain.Tests/BillingOss.Domain.Tests.csproj -c Release
```

`./build.ps1` builds everything and runs all the tests.

## What a change needs

- **It builds without warnings.** Warnings are errors in this repository.
- **It has tests.** Every use case has at least one test. Money, taxes, fiscal emission and payments need table
  tests for the edge cases, and concurrency tests where two requests can meet.
- **Database changes come with a migration**, and `dotnet ef migrations has-pending-model-changes` must pass.
  Migrations are written in two steps (add first, remove in a later version) so that a cluster can be updated
  node by node. See [docs/despliegue/actualizaciones.md](docs/despliegue/actualizaciones.md).
- **Dependencies are pinned.** Packages are managed centrally in `Directory.Packages.props` with lock files.
  A new dependency must be justified in the pull request, together with its license. Prefer what .NET already
  provides.
- **User-facing texts go to the resource files**, in Spanish (es-AR) and English.
- **No secrets and no real data.** Keys, passwords, certificates, `.env` files, database dumps and customer
  data never go into the repository, not even in tests. Use clearly fake values and the documentation address
  ranges (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24) and domains (`example.com`, `.example`, `.test`).
- **No WHMCS material.** Do not copy code, documentation, templates, texts, screenshots or database schema from
  WHMCS or from its packages. The compatible API and the importer are written from real integrations, data
  copies and permissively licensed clients, never from WHMCS itself.

Code identifiers are in English. Comments and design documents are in Spanish, and contributions in either
language are fine.

## Pull requests

- Keep each pull request focused on one change and explain why it is needed.
- Make sure the CI checks pass.
- A maintainer reviews every pull request. Changes to money, fiscal or security paths may need a second review.

## License

Hornero is licensed under the [GNU Affero General Public License v3.0 only](LICENSE). By sending a contribution you
agree that it is released under the license of the code it changes: Apache-2.0 in the folders that carry their own
LICENSE file, and AGPL-3.0-only everywhere else.

## Code of conduct

Everyone taking part in the project must follow the [code of conduct](CODE_OF_CONDUCT.md).

---

## Contribuir a Hornero

Gracias por querer sumarte. Son bienvenidos los reportes de errores, las correcciones, los módulos nuevos, las
traducciones y la documentación.

- **Antes de empezar.** Buscá si ya hay un issue. Para un cambio grande o un módulo nuevo, abrí un issue antes de
  escribir el código. Los problemas de seguridad no van en issues públicos: usá el reporte privado de
  vulnerabilidades del repositorio (pestaña Security).
- **Entorno.** SDK de .NET 10 (versión en `global.json`) y PostgreSQL 17 o posterior para las pruebas de
  integración, con la cadena en `BILLINGOSS_TEST_PG`. Sin esa variable, esas pruebas se saltean.
- **Qué necesita un cambio.** Que compile sin advertencias, que tenga pruebas, una migración si toca la base
  (en dos pasos), dependencias justificadas y fijadas, los textos en los archivos de recursos en castellano e
  inglés, ningún secreto ni dato real, y nada copiado de WHMCS: ni código, ni documentación, ni plantillas, ni
  textos, ni capturas.
- **Licencia.** Al contribuir aceptás que tu aporte se publique bajo la licencia del código que toca: Apache-2.0 en las carpetas que traen su propio LICENSE y [AGPL-3.0](LICENSE), solo la versión 3, en todo lo demás.
- **Convivencia.** Seguimos el [código de conducta](CODE_OF_CONDUCT.md).
