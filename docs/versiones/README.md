# Versiones, vigilancia y publicación

**Decisión:** [D-20](../../diseno/decisiones.md#d-20-actualizaciones-y-seguridad)

Guía para los mantenedores: cómo se vigila lo que usamos, cómo sale una versión y cómo llega al canal que leen los
actualizadores. La parte del proveedor está en [el actualizador](../despliegue/actualizador.md).

| Pieza | Dónde | Qué hace |
|---|---|---|
| Auditoría de paquetes | `Directory.Build.props` | NuGetAudit con las transitivas. NU1900 a NU1905 son advertencia, y error solo con `-p:AuditPipeline=true` |
| CI | `.forgejo/workflows/ci.yml` | Cada PR y cada empuje a `main` y `release/*`: restore bloqueado, build, modelo sin cambios pendientes, pruebas, Trivy y la versión anterior sobre el esquema nuevo |
| Renovate | `renovate.json5`, corrido desde un repo aparte con su propio token | Los PR de versión, por digest y por SHA, con el servicing de .NET en uno por rama |
| Vigía | `.forgejo/workflows/vigia.yml` y `billing-oss-versiones vigia` | Cada día, NuGet, runtime de .NET y Trivy por línea, con issues en Forgejo |
| Publicar | `.forgejo/workflows/publicar.yml` | La etiqueta `vX.Y.Z` construye una vez con BuildKit sin raíz, publica por digest, deja SBOM, manifiesto y actualizador. Sin llave y sin el host |
| Canal | Un repo aparte para el canal | El JSON firmado que leen los actualizadores. Cambia solo por PR aprobado por otro mantenedor |

## CI

`main` y `release/*` se protegen en Forgejo con `ci.yml` obligatoria y revisión, también para los administradores.
La CI no tiene ningún secreto: corre el código del PR.

- `dotnet restore --locked-mode`: un `packages.lock.json` desactualizado hace fallar el PR.
- `dotnet ef migrations has-pending-model-changes`: un cambio de modelo sin su migración también.
- Las pruebas corren proyecto por proyecto contra un PostgreSQL 18 efímero (`BILLINGOSS_TEST_PG`).
- Trivy sobre los archivos de bloqueo frena con una CVE crítica que ya tiene arreglo.
- **Versión anterior:** el trabajo `version-anterior` migra una base plantilla con el commit nuevo y corre ahí las
  pruebas de la última etiqueta, con `BILLINGOSS_TEST_TEMPLATE`. Una migración que borra o endurece algo que la
  versión anterior usa hace fallar la CI.

## Migraciones

La regla de D-20: **la versión anterior funciona con el esquema nuevo, con los datos que escribe la
nueva y con los trabajos que encola.** Además de lo que dice [actualizaciones.md](../despliegue/actualizaciones.md):

- Un parche solo agrega (columnas que aceptan nulos, también con su clave foránea, tablas, índices). Lo que quita o
  endurece va en una minor, y esa migración declara desde qué versión del núcleo vale con
  `migrationBuilder.RequireCoreVersion("X.Y.0")`. Un nodo con un binario anterior no arranca. La prueba
  `MigrationRulesTests` frena una migración que borra, renombra o pasa a `NOT NULL` sin declararlo.
- Un índice que no bloquea va con `migrationBuilder.CreateIndexConcurrently(nombre, "esquema.tabla", "(columnas)")`,
  solo en su migración: borra primero el que haya dejado un intento fallido, así el reintento lo rehace entero.
- Una migración que va a un parche se escribe en la rama de versión y se lleva a `main` con el mismo ID.

## Renovate

El cron y el token viven en un repo aparte, nunca acá: en este repo quien empuja una rama puede correr cualquier
workflow. En `main` fusiona solo parches y digests con la CI en verde. En `release/*` solo propone parches y digests, y
fusiona una persona. Las majors de .NET y de PostgreSQL están apagadas, cosign nunca se fusiona solo y lo que no es
de seguridad espera 3 días. El primer PR fija por SHA las acciones y por digest las imágenes de los workflows, del
`Dockerfile` y de Compose: mientras tanto van por etiqueta, y `publicar.yml` se niega a armar un manifiesto con
Gotenberg o PostgreSQL sin digest.

## Vigía

`vigia.yml` corre a las 06:00 de Argentina y a mano. Las líneas soportadas van en `LINEAS` (antes de la 1.0, solo
`main`). Por cada una:

1. `dotnet restore -p:AuditPipeline=true` y `dotnet list package --vulnerable --include-transitive --format json`.
2. El runtime de la etiqueta exacta de `RUNTIME_IMAGE` en `deploy/Dockerfile` contra `releases-index.json` y el
   `releases.json` del canal de .NET: si hay versiones más nuevas con `security`, junta sus CVE. NuGetAudit no ve el
   runtime, porque el SDK poda los paquetes del framework.
3. Trivy sobre los archivos de bloqueo de todas las líneas.

`billing-oss-versiones vigia` deduplica por aviso y por línea, actualiza el issue fijado **Vigía: estado de las líneas
soportadas** con la fecha de la corrida, abre un issue por hallazgo nuevo con etiquetas `vigia`, `severidad/...` y
`linea/...` y el PR de Renovate si lo hay, y cierra los que dejaron de aparecer. Si un hallazgo pasa su plazo
objetivo sin versión, le pone `vencido` y menciona a los de la variable `MANTENEDORES`, y Forgejo les manda el
correo. El trabajo falla mientras haya hallazgos. Conviene que un monitoreo externo mire la fecha del issue fijado: si
tiene más de un día, el vigía no corre.

| Qué | Plazo objetivo |
|---|---|
| Seguridad de .NET | 2 días hábiles |
| Alta o crítica de un paquete o de la imagen | 7 días |
| Media o baja | La próxima versión |

Para probarlo a mano sobre una carpeta: `dotnet run --project src/BillingOss.Releases -- vigia --linea main=.` (con
`vigia-nuget.json` generado antes), sin `--forgejo` solo lista.

## Publicar un parche

1. Fusionar el PR en la rama de la línea con la CI en verde.
2. Crear la etiqueta `vX.Y.Z` desde la web de Forgejo (nueva versión). Solo los mantenedores pueden crear `v*`.
3. `publicar.yml`, en el mismo runner de contenedores que la CI, construye las seis imágenes de la receta (el núcleo y
   los módulos de ARCA, Mercado Pago, Stripe, PayPal y CryptoCloud) una vez con BuildKit sin
   raíz (`moby/buildkit:*-rootless` y `buildctl-daemonless.sh`, adentro del paso, sin daemon ni socket del host) con
   `attest:provenance=mode=max`, las publica por digest, frena con una CVE crítica con arreglo (Trivy lee del
   registro), genera un SBOM CycloneDX por imagen, publica el actualizador autocontenido y adjunta a la versión
   `manifiesto.json`, los SBOM, el actualizador y `sha256sums.txt`. No tiene llave de firma: lo publicado no le llega a
   nadie hasta que entra al canal.

Necesita en Forgejo la variable `REGISTRO_USUARIO` y el secreto `REGISTRO_TOKEN` (del vault). **Ningún runner de
este repo corre trabajos en el host:** un runner de Forgejo toma cualquier trabajo del repo cuya `runs-on` coincida con
su etiqueta, sin mirar el evento ni quién lo empujó, así que uno con el socket de Docker sería root en la máquina que
construye las versiones para quien pueda empujar una rama (un mantenedor, el usuario de Renovate o quien le robe el
token), y una caché de BuildKit o un `docker` envenenados ahí irían a parar a la próxima versión legítima. Con
BuildKit sin raíz adentro del contenedor del trabajo nada queda en la máquina entre corridas. La exposición que
queda es esta: quien empuja una rama puede correr `publicar.yml` con el token del
registro y publicar imágenes, pero el canal nombra solo las que aprobó otro mantenedor comparando los digests. El
runner `docker` tiene que dejar que el paso de BuildKit cree espacios de nombres de usuario (`container.options`
con `--security-opt seccomp=unconfined --security-opt apparmor=unconfined`, como pide la guía de BuildKit sin raíz),
no montar el socket (`container.docker_host: "-"`) y no dar contenedores privilegiados ni montajes del host
(`container.privileged: false` y `container.valid_volumes` vacío), así un workflow de una rama tampoco los consigue
pidiéndolos en su `container.options`.

## Canal

Vive en un repo aparte, con reglas propias: un mantenedor suma la
versión con `billing-oss-versiones entrada` y abre el PR con su cuenta, otro lo aprueba comparando los digests con
la corrida de `publicar.yml`, y al fusionar `canal.yml` renueva (serial nuevo, emitido ahora y vencimiento a 7 días),
firma y publica. La firma se configura después: hasta entonces `canal.yml` frena antes de publicar y el canal se
carga a mano en el actualizador.

## Pendiente de verificar

Además de lo que falta de D-20:

- Que la instancia de Forgejo resuelva `actions/checkout`, `actions/setup-dotnet` y `actions/forgejo-release`, y que
  el runner corra pasos `docker://` con `entrypoint` y `env` propios.
- Que BuildKit sin raíz construya y publique en ese runner: el paso usa `buildctl-daemonless.sh` con
  `--oci-worker-no-process-sandbox` y corre como el usuario 1000 sobre `dist`, que el paso anterior deja escribible.
  Si el runner no le deja crear espacios de nombres de usuario, la alternativa es un trabajo aparte con la imagen
  rootless como contenedor del trabajo. También que `trivy image --image-src remote` lea del registro de Forgejo con
  `TRIVY_USERNAME` y `TRIVY_PASSWORD`.
- Las versiones de las imágenes de Trivy, BuildKit y Renovate que traen los workflows. Renovate las mueve y fija sus digests,
  pero hay que confirmar que la primera sea una legítima: las 0.69.4 a 0.69.6 de Trivy fueron las comprometidas.
- Que la etiqueta `aspnet:10.0.14-noble-chiseled-extra` exista en MCR.
