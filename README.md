# UrbaLink

Aplicación vecinal para comunidades privadas. Base inicial en C# y .NET 10:
.NET MAUI para Android, API ASP.NET Core y PostgreSQL.

## Qué funciona en esta entrega

- Conexión de la app Android con la API y PostgreSQL.
- Registro con nombre, correo y contraseña; inicio y cierre de sesión.
- Sesiones de siete días, revocables al cerrar sesión, guardadas en SecureStorage
  del dispositivo y asociadas a la dirección exacta del servidor.
- Creación de comunidades: el creador queda como administrador.
- Lista de comunidades limitada al usuario autenticado.
- Invitaciones de un solo uso, válidas siete días y generadas solo por administradores.
- Entrada como vecino mediante invitación, incluso ante peticiones simultáneas.
- Contraseñas con PasswordHasher de ASP.NET Core; sesiones e invitaciones
  almacenadas solo como hashes. No se registran contraseñas ni tokens en logs.

El tablón permite publicar, filtrar por categoría y cargar mensajes anteriores.
Solo los administradores pueden publicar avisos oficiales. El autor y los
administradores pueden retirar publicaciones; se conserva el registro en la base.

Todavía no incluye fotos, chat privado, recuperación de contraseña, verificación
del correo, moderación, aprobación manual ni administración de miembros.
Esta versión permite a cualquier cuenta crear una comunidad; eso no verifica
que represente oficialmente a una urbanización.

## Probar registro y comunidades

1. Detén la API anterior y vuelve a ejecutarla para cargar los cambios.
2. Arranca PostgreSQL y la API con las instrucciones siguientes. Al iniciar con
   conexión configurada, se aplica la migración SQL inicial de forma transaccional
   e idempotente, con un bloqueo para evitar migraciones simultáneas.
3. Ejecuta UrbaLink.Mobile de nuevo desde Visual Studio.
4. Comprueba la conexión y pulsa **Entrar en UrbaLink**.
5. Pulsa **Quiero registrarme**. La contraseña debe tener 12–128 caracteres.
6. Crea una comunidad, por ejemplo El Carambolo. Tu cuenta será administradora.
7. Pulsa **Invitar**, copia el código y vuelve atrás.
8. Cierra sesión y crea otra cuenta (otro correo de pruebas).
9. Introduce el código para entrar como vecino. Esa cuenta no podrá invitar.

Puedes volver a iniciar sesión con la primera cuenta. Los datos sobreviven al
reinicio de la API y del contenedor. No uses datos reales en las pruebas HTTP.
Para comprobar aislamiento, crea otra comunidad con la segunda cuenta.

## Probar el tablón

1. Reinicia la API: se aplicará la migración 002 sin borrar cuentas ni comunidades.
2. Detén la depuración de Visual Studio y vuelve a ejecutar la app.
3. Inicia sesión y pulsa **Abrir tablón** en una de tus comunidades.
4. Usa **Nueva publicación** para elegir categoría, título y mensaje.
5. Entra con otro miembro y pulsa **Actualizar** para ver el mensaje.
6. Prueba el filtro por categorías y **Ver publicaciones anteriores**.
7. El autor o un administrador pueden **Retirar publicación**, previa confirmación.

El tablón carga 20 publicaciones por página, de más recientes a más antiguas.
Las categorías son General, Ayuda, Ofrezco, Material, Coche y Aviso oficial.
Material y Coche son categorías de publicaciones en esta versión; todavía no
incluyen reservas de objetos ni coincidencias automáticas de trayectos.
No hay actualización en tiempo real: usa Actualizar para ver nuevas publicaciones.

Las pruebas automatizadas se ejecutan con PowerShell 7:

    pwsh -File scripts/Test-Posts.ps1

Necesitan el contenedor PostgreSQL del proyecto funcionando. Crean una base
temporal, verifican permisos, validación, autoría, filtros, paginación y
persistencia tras reiniciar, y eliminan solo esa base temporal al terminar.
Compilan en .artifacts para evitar los archivos bloqueados por Visual Studio.

## Comentarios en publicaciones

- En el tablón, pulsa **Ver comentarios / Responder**.
- La pantalla muestra la publicación y las respuestas más recientes primero.
- Escribe hasta 2000 caracteres y pulsa **Enviar comentario**.
- **Ver comentarios anteriores** carga otras 20 respuestas.
- El autor del comentario o un administrador pueden retirarlo con confirmación.
  Ser autor de la publicación no permite retirar respuestas de otras personas.
- Si la red falla, el texto se conserva. Reenviar el mismo texto en esa pantalla
  reutiliza un identificador de envío para evitar duplicados. Cambiar el texto,
  salir de la pantalla o reiniciar la app inicia un envío distinto.
- Al retirarse la publicación, sus comentarios dejan de estar accesibles.
- Usa **Actualizar conversación** para ver nuevas respuestas; no hay avisos
  ni actualización automática en esta iteración.

La migración 003 crea la tabla de comentarios al reiniciar la API y conserva
las cuentas, comunidades y publicaciones existentes.

Pruebas automatizadas, incluyendo reintentos concurrentes:

    pwsh -File scripts/Test-Posts.ps1 -Suite comments

Usan una base temporal y verifican acceso, permisos, autoría, límites,
paginación, retirada, ausencia de duplicados y persistencia tras reiniciar.

## Requisitos

- Visual Studio compatible con .NET 10 y la carga de trabajo .NET MAUI,
  incluyendo Android SDK, JDK y emulador; también ASP.NET y desarrollo web.
- SDK .NET 10. El archivo global.json admite sus feature bands estables.
- PostgreSQL local, o Docker con Compose si prefieres contenedores.

El proyecto móvil apunta únicamente a Android para no requerir las cargas de
iOS, Mac o Windows. Las carpetas adicionales generadas por la plantilla se
conservan para una futura ampliación. El identificador com.urbalink.mobile es
provisional y debe revisarse antes de publicar.

## 1. Arrancar la API sin base de datos

Desde la raíz del repositorio:

    dotnet run --project src/UrbaLink.Api --launch-profile local

Abrir http://localhost:5080/health/live: debe devolver estado alive.
GET /health/ready devuelve 503 y database_not_configured hasta configurar PostgreSQL.
La API puede compilarse por separado mientras se instala MAUI:

    dotnet build src/UrbaLink.Api

## 2. Preparar PostgreSQL

Opción A: instala PostgreSQL y crea una base urbalink con un usuario propio.
La instalación de PostgreSQL es independiente de la carga de MAUI.

Opción B: con Docker instalado, copia .env.example a .env.local, sustituye la contraseña
de ejemplo por una contraseña local y ejecuta:

    powershell -File scripts/Database.ps1 Start
    powershell -File scripts/Database.ps1 Status

Para detener PostgreSQL conservando los datos:

    powershell -File scripts/Database.ps1 Stop

El archivo .env.local contiene la contraseña y está excluido de Git. No es una
carpeta. El script encuentra Docker Desktop aunque la terminal no tenga su PATH.

El puerto 5432 solo se publica en localhost. Los móviles acceden a la API,
nunca directamente a PostgreSQL. El volumen conserva los datos entre reinicios.
Cambiar .env.local no cambia la contraseña de una base que ya fue inicializada.

Configura en una terminal local el secreto de desarrollo, sustituyendo
TU_PASSWORD por la contraseña elegida (no lo añadas a appsettings.json):

    dotnet user-secrets set "ConnectionStrings:UrbaLink" "Host=localhost;Port=5432;Database=urbalink;Username=urbalink;Password=TU_PASSWORD" --project src/UrbaLink.Api

También se admite la variable de entorno ConnectionStrings__UrbaLink.
Reinicia la API después de configurar la conexión. /health/ready debe devolver
200 y ready. Si se pierde la conexión después del arranque, devuelve 503 y database_unavailable,
sin exponer credenciales al cliente. Si PostgreSQL no está disponible al arrancar,
la migración impide iniciar la API: arranca primero la base de datos.

## 3. Probar desde tu teléfono por Wi-Fi

1. Arranca la API con el perfil de red:

       dotnet run --project src/UrbaLink.Api --launch-profile lan

   También puedes ejecutar scripts/Start-LocalApi.ps1 -Lan.
2. Consulta con ipconfig la IPv4 del adaptador de tu red doméstica.
3. Ordenador y móvil deben estar en la misma red, sin aislamiento de invitados.
4. Si Windows lo solicita, permite la API en redes privadas. Si hace falta una
   regla manual, limita TCP 5080 al perfil privado y a la subred local. No abras
   puertos en el router ni desactives el firewall.
5. Abre UrbaLink.slnx en Visual Studio. Configura UrbaLink.Mobile como proyecto
   de inicio, conecta el móvil con depuración USB y ejecuta en Debug.
6. Introduce http://IP_DE_TU_PC:5080 y pulsa Comprobar conexión.

En el emulador Android, usa http://10.0.2.2:5080.
localhost en el teléfono se refiere al propio teléfono.
Una vez instalada la app, puedes desconectar el USB y seguir probando por Wi-Fi.
El ordenador debe seguir encendido con la API en marcha.

En Visual Studio puedes configurar API y Mobile como proyectos de inicio
múltiples, eligiendo el perfil lan para la API.

## Seguridad y publicación

El tráfico HTTP está permitido únicamente en la compilación Android Debug para
las pruebas domésticas. Release exige HTTPS y no permite direcciones HTTP en
la pantalla. Las pruebas iniciales deben usar datos ficticios.
No hay excepciones de validación de certificados.

Los endpoints de estado son públicos. Los endpoints de comunidades exigen sesión y comprueban los permisos en el servidor.
El alojamiento definitivo necesitará HTTPS, secretos externos, copias de respaldo
y monitorización. El perfil lan es solo para desarrollo.

## Comprobaciones

Pruebas automáticas de API sin PostgreSQL (PowerShell 7):

    pwsh -File scripts/Test-Api.ps1

El script arranca instancias temporales en localhost, comprueba estados de error
y las detiene al finalizar. No altera la configuración local de PostgreSQL.

Sin necesidad de la carga MAUI:

    dotnet run --project tests/UrbaLink.Checks

Con la API ejecutándose sin conexión configurada:

    dotnet run --project tests/UrbaLink.Checks -- http://localhost:5080 database_not_configured

Con PostgreSQL configurado y operativo:

    dotnet run --project tests/UrbaLink.Checks -- http://localhost:5080 ready

Compilación Android, una vez finalizada la instalación:

    dotnet build src/UrbaLink.Mobile -f net10.0-android

## Pruebas del flujo de cuentas

Con una API apuntando a una base PostgreSQL exclusiva para pruebas:

    dotnet run --project tests/UrbaLink.Checks -- http://localhost:15083 accounts

Crea cuentas ficticias y comprueba validación, login, aislamiento, permisos,
consumo concurrente de invitaciones y revocación. No lo ejecutes contra una base
con datos reales. El límite de autenticación es de 20 solicitudes por minuto
por IP; espera un minuto entre ejecuciones completas.

Los errores de autenticación responden 401, los permisos insuficientes 403,
los conflictos 409 y el límite de solicitudes 429. El cierre de sesión revoca
la sesión actual; las demás sesiones permanecen vigentes hasta siete días.
La app exige conexión para confirmar el cierre de sesión en el servidor.

## Próximos pasos

1. Fotografías en publicaciones.
2. Gestión de miembros y moderación.
3. Verificación de correo y recuperación de contraseña antes del uso público.



## Inicio y gestión de comunidades

La app recuerda el servidor y abre la sesión guardada al iniciar. La navegación principal
se divide en Inicio, Explorar y Perfil. Inicio muestra la comunidad seleccionada,
recuentos reales, accesos a ayuda, material y coche, avisos y actividad reciente.
Los accesos siguen utilizando publicaciones; aún no hay inventario ni reservas.

En Perfil, los administradores pueden gestionar cada comunidad y enviarla a la
papelera escribiendo su nombre exacto. La comunidad deja de estar disponible para
sus miembros y se invalidan sus invitaciones. Perfil → Papelera permite restaurarla
con sus miembros y contenido; será necesario generar nuevas invitaciones.
No se realiza un borrado permanente de los datos.

Pruebas de este ciclo con una base temporal aislada:

    ./scripts/Test-Posts.ps1 -Suite lifecycle

Después de actualizar, reinicia la API y vuelve a ejecutar la app desde Visual Studio
para cargar las nuevas pantallas y las reglas de acceso.
