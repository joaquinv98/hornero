# Base externa: servicio administrado o clúster del cliente

**Parte de:** [despliegue](README.md)

La aplicación corre igual que en la [HA en VMs](ha-vms.md) (paso 8, con `compose.yaml` en cada nodo), y la base
la pone otro: un servicio administrado (Amazon RDS, Google Cloud SQL, Azure Database for PostgreSQL), un
CloudNativePG en Kubernetes o un clúster propio del cliente. Hornero solo habla el protocolo de PostgreSQL.

## Contrato de la base

Lo que tiene que cumplir para que no se pierda ni se duplique un comprobante o un pago:

| Requisito | Por qué |
|---|---|
| PostgreSQL 17 o 18 | Las versiones soportadas |
| HA **sincrónica**: un commit confirmado ya está en otra zona o nodo | Con replicación asincrónica, una conmutación puede perder una emisión fiscal o un pago ya informado a la pasarela |
| Un endpoint de escritura que siempre apunta a la primaria, o la lista de nodos para la cadena de varios hosts | La aplicación nunca escribe en una réplica |
| TLS con un certificado que se pueda validar | La cadena usa `SSL Mode=VerifyFull` |
| La extensión `btree_gist` disponible | La exclusión de períodos facturados la usa |
| Un rol que pueda crear esquemas en la base, para `migrate` | Las migraciones crean los esquemas de cada módulo |
| Respaldos con restauración a un punto en el tiempo, y una prueba de restauración periódica | La guía de [backups.md](backups.md) vale igual, con las herramientas del proveedor |
| El importador, además, `CREATE DATABASE` | Cada corrida del importador va a una base nueva |

Lo que no está soportado: multiprimario, leer réplicas en los caminos de dinero, `synchronous_commit` en `local`
u `off`, y replicación lógica en los dos sentidos.

## Roles

[`deploy/sql/roles.sql`](../../deploy/sql/roles.sql) se corre con el usuario administrador del servicio, que no
es superusuario. Si el servicio no deja usar `ALTER DEFAULT PRIVILEGES FOR ROLE billing_owner` (en algunas
versiones el creador de un rol no es miembro de él), esas tres líneas se corren conectado como `billing_owner`,
sin el `FOR ROLE`.

## Cadena de conexión

Igual que en la HA en VMs, con el endpoint del proveedor y su CA:

```text
Host=billing.cluster-abc.us-east-1.rds.amazonaws.com;Database=billing_oss;Username=billing_app;Password=...;
SSL Mode=VerifyFull;Root Certificate=/run/billing/pg-ca.crt;Maximum Pool Size=20;Keepalive=10;Timeout=5
```

En una sola línea, en `secrets/db_app` y `secrets/db_dueno`. `secrets/tls/ca.crt` es la CA que publica el
proveedor. Con `preparar.sh`, pasar `DB_HOSTS` con el endpoint y reemplazar después `tls/ca.crt` por la del
proveedor.

## Notas por proveedor

| Proveedor | HA sincrónica | Endpoint | Ojo con |
|---|---|---|---|
| Amazon RDS for PostgreSQL | Multi-AZ (instancia), con réplica sincrónica en otra zona | El del clúster o la instancia, que sigue a la primaria | `btree_gist` se crea con el usuario administrador |
| Google Cloud SQL | Alta disponibilidad regional, con disco replicado sincrónicamente | La IP de la instancia | Conmutación de unos 60 segundos |
| Azure Database for PostgreSQL, servidor flexible | HA con redundancia de zona | El nombre del servidor | Agregar `BTREE_GIST` al parámetro `azure.extensions` |
| CloudNativePG | `synchronous` con `dataDurability: required` | El servicio `-rw` | Nunca `preferred`: con eso una conmutación puede perder datos |
| Patroni u otro clúster propio del cliente | El modo sincrónico estricto, como en [ha-vms.md](ha-vms.md) | Varios hosts o su endpoint | Lo opera el cliente, bajo su responsabilidad |

En los servicios administrados la réplica sincrónica no siempre aparece en `synchronous_standby_names`, porque es
por almacenamiento. Se confía en lo que declara el proveedor, y la HA tiene que estar activada y ser sincrónica.

## Prueba de humo

Antes de dar la instalación por buena:

1. `docker compose run --rm migrate`.
2. Emitir un comprobante contra la homologación de ARCA y cobrar con credenciales de prueba.
3. Forzar una conmutación desde la consola del proveedor con carga corriendo.
4. Verificar con las consultas de [monitoreo.md](monitoreo.md#después-de-una-promoción) que no quedó nada perdido
   ni duplicado.

## Residencia de los datos

La base tiene datos personales de los clientes del proveedor. Si el servicio guarda los datos fuera de Argentina,
la Ley 25.326 pide que el país de destino tenga protección adecuada o el consentimiento de los titulares. Es una
decisión de cada proveedor, antes de elegir región.
