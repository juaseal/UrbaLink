# Piloto en Railway

La API y PostgreSQL son dos servicios del mismo proyecto. La API tiene un dominio
HTTPS de Railway; PostgreSQL se conecta por la red privada. Empezar con una base
vacía no modifica ni exporta las cuentas y mensajes locales.

## Código

Subir los cambios revisados al repositorio `juaseal/UrbaLink` antes del despliegue.
Los archivos locales sin commit no están disponibles para el despliegue desde GitHub.
El contexto de Docker admite solo el código de API/Contracts y `global.json`;
no incluye `.env.local`, copias de seguridad, pruebas ni el proyecto Android.

## Configuración de los servicios

1. Crear o reutilizar el proyecto UrbaLink, en una región europea.
2. Añadir PostgreSQL con volumen persistente y configurar sus copias de seguridad.
   No habilitar acceso público/TCP a la base para la aplicación.
3. Añadir un servicio desde el repositorio de GitHub. La raíz de compilación es
   la raíz del repositorio que contiene `Dockerfile`, no `src/UrbaLink.Api`.
4. Railway detecta el `Dockerfile`. Configurar:

| Campo | Valor |
| --- | --- |
| Builder | Dockerfile |
| Puerto de destino del dominio | `8080` |
| Pre-deploy command | `dotnet UrbaLink.Api.dll --Database:MigrateOnly=true` |
| Start command | Usar el CMD del Dockerfile |
| Healthcheck path | `/health/ready` |
| Healthcheck timeout | `120` segundos |
| Restart policy | On Failure |
| Réplicas iniciales | 1 |

5. Configurar las variables de la API usando referencias al servicio PostgreSQL.
   Si se llama `Postgres`, las referencias son:

| Variable | Valor |
| --- | --- |
| `PGHOST` | `${{Postgres.PGHOST}}` |
| `PGPORT` | `${{Postgres.PGPORT}}` |
| `PGDATABASE` | `${{Postgres.PGDATABASE}}` |
| `PGUSER` | `${{Postgres.PGUSER}}` |
| `PGPASSWORD` | `${{Postgres.PGPASSWORD}}` |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `Hosting__RailwayEdge` | `true` |
| `Database__MigrateOnStartup` | `false` |

La contraseña se referencia en Railway; no se copia en Git ni en la app.
`ConnectionStrings__UrbaLink`, si existe, prevalece sobre las variables `PG*`;
eliminar cualquier conexión de desarrollo heredada en el servicio remoto.

`Hosting__RailwayEdge` acepta `X-Real-IP` y `X-Forwarded-Proto` del proxy de Railway
para distinguir las IP de los usuarios y evitar bucles de redirección HTTPS.
Solo debe activarse con el contenedor aislado detrás del ingreso HTTP de Railway,
sin TCP proxy público hacia la API y sin servicios no confiables en la red privada.
No debe activarse para un proceso publicado directamente a Internet.

6. Desplegar. Las migraciones se ejecutan antes de arrancar la API; si fallan,
   el despliegue debe detenerse. Generar el dominio público de la API al estar lista.
7. Comprobar por HTTPS `/health/live` (`alive`) y `/health/ready` (`ready`).
8. Introducir la URL HTTPS en Android y probar con datos móviles: registro,
   acceso, invitación y conversación privada entre dos cuentas de pruebas.

El cambio de dirección requiere iniciar sesión: el almacenamiento seguro del
cliente asocia cada sesión a su servidor. Sin una migración de datos, las cuentas
locales no existen en la nueva base.

## Operación

Configurar alertas de gasto, copias periódicas y una prueba de restauración antes
de incorporar datos del piloto. Un volumen por sí solo no es una copia.
Este despliegue de una API y una base no ofrece conmutación automática ante fallos.
No se garantiza ausencia de interrupción al aplicar futuras migraciones:
el chequeo de readiness exige que el esquema coincida con la versión de la API.

La configuración se hace en el servicio: no se añade `railway.json`, cuyo formato
Config as Code está marcado como obsoleto en la documentación actual. Se puede
automatizar la configuración con las herramientas de Railway una vez conectado.

## Verificación local del despliegue

Ejecutar `docker build -t urbalink-api:pilot .` y después, con PostgreSQL local
arrancado, `pwsh -File scripts/Test-Deployment.ps1`. La prueba crea y elimina una
base temporal y un contenedor propio. Comprueba migraciones independientes,
ejecución sin root, conexión mediante variables `PG*`, readiness, redirección a
HTTPS, cabeceras del proxy, límites de autenticación por IP y conservación de la
sesión tras reiniciar. La construcción y esta prueba han pasado localmente;
todavía se necesita validar el dominio y la configuración reales de Railway.

Fuentes: [Dockerfiles](https://docs.railway.com/builds/dockerfiles),
[Pre-deploy](https://docs.railway.com/deployments/pre-deploy-command),
[Red pública y cabeceras](https://docs.railway.com/networking/public-networking/specs-and-limits),
[Configuración del despliegue](https://docs.railway.com/config-as-code/reference).
