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

## Panel web de administración de la plataforma

El panel se sirve con la API, en `/admin/` (por ejemplo,
`https://urbalink-production.up.railway.app/admin/`). No necesita otro servicio
ni otro despliegue. El código HTML de acceso es público; todos los datos y acciones
están protegidos en el servidor con sesiones administrativas independientes.

### Activar la primera cuenta

1. Usa una cuenta ya registrada de UrbaLink. En PostgreSQL de Railway, consulta
   su identificador, sustituyendo el correo por el tuyo:

       SELECT id, name, email FROM users WHERE email = 'tu-correo@example.com';

2. En las variables del servicio **API**, añade:

   | Variable | Valor |
   | --- | --- |
   | `PlatformAdmin__UserIds` | UUID de tu usuario (varios operadores, separados por comas) |
   | `PlatformAdmin__TotpSecrets__UUID` | Clave TOTP individual de ese operador |
   | `Communities__RequireApproval` | `true` (también es el comportamiento predeterminado) |

   En el nombre de la segunda variable, reemplaza `UUID` por el mismo identificador
   en minúsculas, **sin guiones** (32 caracteres). En `PlatformAdmin__UserIds` sí se usa el UUID habitual con guiones. No hay una cuenta ni una contraseña predeterminada.
   Crear una comunidad no concede permisos de plataforma.

3. Genera una clave nueva en tu propio equipo con PowerShell 7:

       pwsh -File scripts/New-PlatformTotp.ps1 -UserId UUID

   El script muestra la clave **solo en tu terminal**; no la escribe en archivos.
   Añádela manualmente a tu autenticador (TOTP, 6 dígitos, SHA-1, 30 segundos)
   y a la variable correspondiente de Railway. No uses la clave de las pruebas,
   no pegues la clave en chats ni la subas al repositorio.

4. Conserva el pre-deploy habitual:

       dotnet UrbaLink.Api.dll --Database:MigrateOnly=true

   Despliega el commit con la migración **013** y verifica `/health/ready`.
   Abre `/admin/` e introduce tu correo, contraseña de UrbaLink y código del
   autenticador. La sesión dura 30 minutos; se guarda solo en memoria de la
   pestaña. Recargar la página requiere entrar de nuevo. Un código ya utilizado
   no se puede reutilizar; espera al siguiente. El reloj del móvil debe estar sincronizado.

### Qué permite el panel

- Buscar urbanizaciones, revisar sus miembros y aprobar, suspender o reactivar comunidades.
- Consultar nombres, correos, roles y estado de sus miembros.
- Bloquear un miembro solo en una comunidad o suspender su cuenta en toda la plataforma.
  La suspensión global revoca sus sesiones; al reactivarla tendrá que iniciar sesión.
- Revisar publicaciones, comentarios, objetos y avisos de animales, y retirarlos con motivo.
- Atender denuncias con copia del contenido señalado, resolverlas o descartarlas con motivo.
- Consultar el historial de accesos a información sensible y de medidas administrativas.

Las comunidades existentes quedan **activas** al aplicar la migración; las nuevas
quedan **pendientes** y no pueden invitar vecinos ni publicar hasta su aprobación.
Archivar/restaurar una comunidad desde Android no revierte una suspensión de plataforma.
Los bloqueos conservan la membresía e impiden volver a entrar con una invitación.
Los cambios conservan los datos y quedan auditados. No se puede bloquear al último
administrador activo de una comunidad; se puede suspender la comunidad cuando proceda.
La suspensión global de una cuenta puede afectar al único administrador de sus comunidades.
Las cuentas globales autorizadas se protegen de suspensión desde el propio panel;
para revocar a un operador, elimina su UUID de `PlatformAdmin__UserIds` y despliega
la nueva configuración. Para recuperar el segundo factor, genera y configura una clave nueva.

### Privacidad y denuncias

No existe una ruta de administración para leer chats completos. En la app actualizada,
**Denunciar mensaje** envía solo el mensaje concreto que el participante señala, no
el resto de la conversación. La API comprueba que ese usuario participe en el chat.
El resto del contenido tiene **Denunciar publicación/comentario/objeto/aviso/comunidad**.
Las denuncias se envían al equipo de plataforma, sin entregarlas a los administradores
locales. El panel debe consultarse: esta versión no envía correo ni push al operador.
Los textos se renderizan como texto y no como HTML; el panel utiliza CSP y no almacena
credenciales ni tokens en localStorage. Las copias de denuncias y el registro administrativo
se guardan para revisar las decisiones; no hay borrado permanente desde el panel.

Necesitas un APK actualizado para mostrar los botones de denuncia y los estados de
aprobación en Android. El servidor aplica las restricciones también a clientes anteriores.
Los clientes anteriores pueden mostrar un error genérico al abrir una comunidad pendiente.

### Pruebas

    pwsh -File scripts/Test-Posts.ps1 -Suite platform

La suite utiliza una base temporal y configuración ficticia exclusiva de pruebas.
Comprueba acceso, doble factor y repetición de códigos, aprobación, moderación,
privacidad, bloqueos, auditoría y persistencia tras reiniciar. Las suites anteriores
usan `Communities__RequireApproval=false` solo dentro de sus procesos de prueba.
