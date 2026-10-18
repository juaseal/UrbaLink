# UrbaLink

Aplicación vecinal para comunidades privadas. Base inicial en C# y .NET 10:
.NET MAUI para Android, API ASP.NET Core y PostgreSQL.

La distribución de módulos, los invariantes de concurrencia, la configuración
de disponibilidad y los límites pendientes se describen en [ARCHITECTURE.md](ARCHITECTURE.md).
El despliegue del piloto con API y PostgreSQL en Railway se describe en [DEPLOYMENT.md](DEPLOYMENT.md).


## Web de vecinos

La versión web se sirve en la raíz de la API: abre el dominio de Railway desde
el navegador, sin APK ni dirección de servidor dentro de la aplicación. El panel
administrativo continúa en `/admin/`. Ambas interfaces usan las mismas cuentas,
comunidades, publicaciones y conversaciones que Android.

Esta primera versión web incluye acceso y registro, sesión persistente, invitaciones,
solicitud de comunidades, inicio, tablón con filtros, publicación, comentarios,
Servicios con contacto privado y bandeja de conversaciones leídas y pendientes.
Los chats existentes de Objetos y Animales también se pueden responder desde la
bandeja web. Incluye denuncias de contenido y mensajes, retirada de publicaciones
y comentarios por sus autores o administradores, e invitaciones para administradores
locales. Las nuevas comunidades requieren la aprobación de plataforma habitual.

Los catálogos y formularios completos de Objetos, Trayectos y Animales siguen en
Android y quedan para la siguiente fase web. La campana de esta versión avisa de
mensajes privados; no hay push con el navegador cerrado ni avisos de comentarios.

La sesión del navegador utiliza una cookie HttpOnly, SameSite=Strict y Secure en
producción. Las solicitudes se limitan al mismo origen y requieren una cabecera
propia para evitar peticiones desde otras webs. No se guardan contraseñas ni tokens
en localStorage; solo se recuerda la comunidad seleccionada. Cerrar sesión revoca
esa sesión y conserva las sesiones de otros dispositivos.

Se trabaja en la misma rama `main` y no hace falta otro servicio Railway, variables
nuevas ni migraciones adicionales. El Dockerfile existente publica también `wwwroot`.
Consulta [DEPLOYMENT.md](DEPLOYMENT.md#web-de-vecinos-en-el-mismo-servicio) para actualizar.

    pwsh -File scripts/Test-Posts.ps1 -Suite web

Para revisar el navegador con datos ficticios y limpieza de la base al terminar:

    pwsh -File scripts/Test-Posts.ps1 -Suite web -Preview

## Qué funciona en esta entrega

- Conexión automática de la app Android con el servidor de UrbaLink.
- Registro con nombre, correo y contraseña; inicio y cierre de sesión.
- Sesiones persistentes de 180 días, renovadas con el uso como máximo una vez al
  día en condiciones normales. Se guarda solo el token en SecureStorage, nunca
  la contraseña. Cerrar sesión revoca el acceso de ese dispositivo.
- Al abrir la app se recupera el acceso guardado para la misma dirección del
  servidor. Un fallo de red conserva la sesión y permite reintentar la conexión.
- Creación de comunidades: el creador queda como administrador.
- Lista de comunidades limitada al usuario autenticado.
- Invitaciones de un solo uso, válidas siete días y generadas solo por administradores.
- Entrada como vecino mediante invitación, incluso ante peticiones simultáneas.
- Contraseñas con PasswordHasher de ASP.NET Core; sesiones e invitaciones
  almacenadas solo como hashes. No se registran contraseñas ni tokens en logs.

El tablón permite publicar, filtrar por categoría y cargar mensajes anteriores.
Solo los administradores pueden publicar avisos oficiales. El autor y los
administradores pueden retirar publicaciones; se conserva el registro en la base.

Objetos incluye foto y conversaciones privadas entre propietario e interesado.
Quedan pendientes recuperación de contraseña, verificación del correo,
moderación avanzada y administración de miembros.
Esta versión permite a cualquier cuenta crear una comunidad; eso no verifica
que represente oficialmente a una urbanización.

## Probar registro y comunidades

1. Detén la API anterior y vuelve a ejecutarla para cargar los cambios.
2. Arranca PostgreSQL y la API con las instrucciones siguientes. Al iniciar con
   conexión configurada, se aplica la migración SQL inicial de forma transaccional
   e idempotente, con un bloqueo para evitar migraciones simultáneas.
3. Ejecuta UrbaLink.Mobile de nuevo desde Visual Studio.
4. La app abre directamente el acceso a tu cuenta. Para probar contra la API local, compila con `UrbaLinkServerUrl` como se indica en la sección de Wi-Fi.
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
3. Inicia sesión y pulsa **Ver toda la actividad** en tu comunidad.
4. Usa **Nueva publicación** para elegir categoría, título y mensaje.
5. Entra con otro miembro y pulsa **Actualizar** para ver el mensaje.
6. Prueba el filtro por categorías y **Ver publicaciones anteriores**.
7. El autor o un administrador pueden **Retirar publicación**, previa confirmación.

El tablón carga 20 publicaciones por página, de más recientes a más antiguas.
Las categorías visibles son General, Necesito ayuda, Servicios, Recomendaciones,
Material, Coche y Aviso oficial. Ayuda y servicios reúne solicitudes, ofertas y
recomendaciones con filtros; los servicios tienen contacto privado.
Material conserva las publicaciones antiguas. El catálogo Objetos es un módulo
independiente; los trayectos siguen siendo publicaciones. No hay reservas de objetos
ni coincidencias automáticas de trayectos.
No hay actualización en tiempo real: usa Actualizar para ver nuevas publicaciones.

Las pruebas automatizadas se ejecutan con PowerShell 7:

    pwsh -File scripts/Test-Posts.ps1

Necesitan el contenedor PostgreSQL del proyecto funcionando. Crean una base
temporal, verifican permisos, validación, autoría, filtros, paginación y
persistencia tras reiniciar, y eliminan solo esa base temporal al terminar.
Compilan en .artifacts para evitar los archivos bloqueados por Visual Studio.

## Comentarios en publicaciones

- En el tablón, abre **⋮ → Ver comentarios / Responder**.
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
6. Para una compilación local de desarrollo, selecciona el servidor al compilar:

       dotnet build src/UrbaLink.Mobile -f net10.0-android -p:UrbaLinkServerUrl=http://IP_DE_TU_PC:5080

   Despliega esa compilación en el teléfono. La app no muestra un selector de servidor.
   Sin esta propiedad, tanto Debug como Release utilizan el servidor de producción.

En el emulador Android, compila con `-p:UrbaLinkServerUrl=http://10.0.2.2:5080`.
localhost en el teléfono se refiere al propio teléfono.
Una vez instalada la app, puedes desconectar el USB y seguir probando por Wi-Fi.
El ordenador debe seguir encendido con la API en marcha.

En Visual Studio puedes configurar API y Mobile como proyectos de inicio
múltiples, eligiendo el perfil lan para la API.

## 4. Generar un APK para instalar en el móvil

Con los requisitos de .NET MAUI y Android anteriores instalados, ejecuta estos
comandos en PowerShell desde la raíz del repositorio (donde está `UrbaLink.slnx`):

```powershell
$env:DOTNET_CLI_HOME = Join-Path $env:TEMP 'UrbaLink-dotnet'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.artifacts/nuget'

dotnet build src/UrbaLink.Mobile/UrbaLink.Mobile.csproj `
    -c Debug -f net10.0-android `
    --artifacts-path .artifacts/pilot-apk `
    -p:EmbedAssembliesIntoApk=true `
    -p:AndroidPackageFormats=apk `
    -p:RuntimeIdentifier=android-arm64

if ($LASTEXITCODE -ne 0) { throw 'No se ha podido generar el APK.' }

$apk = '.artifacts/pilot-apk/bin/UrbaLink.Mobile/debug_net10.0-android_android-arm64/com.urbalink.mobile-Signed.apk'
$installer = '.artifacts/pilot-apk/UrbaLink-1.0.3-pruebas-arm64.apk'
Copy-Item -LiteralPath $apk -Destination $installer
Get-Item -LiteralPath $installer
```

El instalador queda en `.artifacts/pilot-apk/UrbaLink-1.0.3-pruebas-arm64.apk`.
La carpeta `.artifacts` está excluida de Git. Es una compilación **Debug para
pruebas**, firmada con la clave de depuración del equipo, para móviles Android
ARM de 64 bits. `EmbedAssembliesIntoApk=true` incluye los ensamblados en el APK:
no necesita Visual Studio ni Fast Deployment para ejecutarse. No es el paquete
Release con firma de distribución para publicar en Google Play.

Para instalar y conectar con el piloto:

1. Copia el APK a **Descargas** del móvil, por ejemplo mediante transferencia USB.
2. Ábrelo desde **Archivos** y pulsa **Instalar**. Si Android lo solicita, permite
   instalar aplicaciones desde esa aplicación.
3. Abre UrbaLink: conecta automáticamente con `https://urbalink-production.up.railway.app`
   y muestra el inicio de sesión o recupera tu sesión guardada.
4. Registra una cuenta en el servidor publicado si todavía no tienes una allí.
   Las cuentas de la base local no se trasladan automáticamente a Railway.

Con Railway puedes usar Wi-Fi o datos móviles y apagar el ordenador. La dirección
del servidor se configura al compilar; los vecinos no necesitan introducirla. Para actualizar una instalación
existente, conserva la misma clave de firma; una compilación firmada con otra
clave no puede instalarse como actualización sobre ella.

### Comprobación en el teléfono (1.0.1)

- Actualiza sobre la versión anterior sin desinstalar y comprueba que conserva la sesión.
- Con Android en modo oscuro, revisa las horas y plazas de un trayecto y el cierre
  de una subasta. La aplicación mantiene su tema claro, incluidos los selectores.
- Escribe un título, una descripción y un mensaje: el teclado debe ofrecer mayúscula
  al comenzar una frase, si la configuración del teclado lo permite. Correo,
  contraseña, invitaciones, direcciones web e importes usan sus teclados específicos.
- En **Elegir foto**, selecciona una imagen y prueba también a cancelar: el borrador
  y la foto anterior deben conservarse al cancelar. Se usa el selector de fotos de
  Android; su aspecto y los álbumes disponibles dependen del dispositivo.
- Tras iniciar sesión, guardar credenciales solo debe ofrecerse en ese momento,
  nunca al elegir una foto ni al escribir contenido. Android decide si muestra
  la oferta según el gestor de contraseñas configurado.
- Comprueba que **Inicio** conserva los accesos a Objetos, Ir juntos, Ayuda y
  servicios y toda la actividad; la barra inferior contiene solo Inicio y Perfil.

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

La app utiliza el servidor configurado al compilar y abre la sesión guardada al iniciar. La navegación principal
se divide en Inicio y Perfil. Inicio muestra la comunidad seleccionada,
recuentos reales, accesos a ayuda, material y coche, avisos y actividad reciente.
Objetos abre el catálogo de venta, alquiler y subastas. Ir juntos abre fichas de
trayectos con horarios y condiciones; Ayuda sigue utilizando publicaciones. El alquiler se acuerda por conversación, sin reservas.

En Perfil, los administradores pueden gestionar cada comunidad y enviarla a la
papelera escribiendo su nombre exacto. La comunidad deja de estar disponible para
sus miembros y se invalidan sus invitaciones. Perfil → Papelera permite restaurarla
con sus miembros y contenido; será necesario generar nuevas invitaciones.
No se realiza un borrado permanente de los datos.

Pruebas de este ciclo con una base temporal aislada:

    ./scripts/Test-Posts.ps1 -Suite lifecycle

Después de actualizar, reinicia la API y vuelve a ejecutar la app desde Visual Studio
para cargar las nuevas pantallas y las reglas de acceso.


## Objetos: venta, alquiler y subastas

1. Reinicia la API para aplicar las migraciones 005 y 006 y ejecuta de nuevo la app.
2. En **Inicio**, entra en **Objetos → Explorar**.
3. **Ofrecer un objeto** permite añadir una foto, título, descripción y modalidad:
   - **Venta:** precio fijo en euros; el propietario puede marcarlo vendido en ⋮.
   - **Alquiler / préstamo:** precio por hora, día o semana; 0 € indica préstamo gratuito.
     El propietario marca disponible/alquilado en ⋮. Un objeto alquilado sigue visible
     y admite interesados. Disponibilidad, duración, precio final y entrega se acuerdan
     directamente: no hay calendario, reserva ni cobro automático.
   - **Subasta:** precio de salida, incremento mínimo y cierre entre 5 minutos y 30 días.
     No se puede pujar por un objeto propio, retirar pujas ni retirar una subasta que ya
     tiene pujas. El cierre usa la hora del servidor; gana la puja más alta registrada.
4. Toca una tarjeta para ver el objeto. **Hablar con el propietario** abre una conversación
   privada; solo sus dos participantes pueden leerla y escribir, también tras vender o
   retirar el objeto. Los administradores de la comunidad no tienen acceso a chats ajenos.
5. **Mis objetos** reúne lo publicado. **Mis gestiones** conserva conversaciones y pujas,
   incluidas las subastas cerradas. Tras el cierre, propietario y ganador pueden hablar
   y el propietario puede confirmar la entrega. Esto no confirma ni ejecuta un pago.
6. Usa **Actualizar** para ver pujas y mensajes nuevos. No hay notificaciones push ni
   actualización en tiempo real en esta entrega.

La foto es opcional: Android corrige la orientación, reduce a una miniatura JPEG de
hasta 40 KB y elimina los metadatos. Las fotos se entregan con la API autenticada,
no mediante enlaces públicos. Las conversaciones y el catálogo se paginan de 20 en 20.
Los importes se guardan como céntimos enteros; el máximo admitido es 1.000.000 €.

Pruebas en una base temporal, sin usar datos reales:

    ./scripts/Test-Posts.ps1 -Suite market

Comprueban fotos y precios, idempotencia de envíos, alquiler manual, privacidad
(incluido el administrador), venta, filtros, paginación, pujas simultáneas,
incremento mínimo, cierre durante una espera por bloqueo, ganador, persistencia
tras reiniciar y bloqueo de acceso al archivar una comunidad.

La migración 006 es aditiva. La tabla del prototipo de reservas se conserva por
compatibilidad, pero ninguna pantalla ni ruta de la API nueva la utiliza.


## Campana de mensajes sin leer

La cabecera de Inicio y Perfil muestra una campana con el total de mensajes
sin leer de todas las comunidades activas de la cuenta. Se consulta cada 10 segundos
mientras esa pantalla está visible. No cuenta los mensajes enviados por uno mismo.
La campana abre una bandeja agrupada por conversación; tocar una tarjeta lleva al chat.
Al cargar la conversación se confirma su lectura hasta el último mensaje mostrado,
sin consumir mensajes que hayan llegado después. La lectura se conserva entre dispositivos.
Los mensajes anteriores a esta actualización aparecen inicialmente como no leídos.

La bandeja también se actualiza cada 10 segundos. No son notificaciones push de Android:
con la app cerrada no hay sonido ni aviso del sistema. El chat mantiene el botón Actualizar.
La migración 007 añade únicamente el estado de lectura, sin modificar mensajes existentes.


## Trayectos en coche

En **Inicio → Ir juntos → Ofrecer un trayecto** se publican rutas semanales:

- Origen, destino y punto de encuentro opcional.
- Días de salida de ida (lunes a domingo) y hora local de salida.
- Vuelta opcional, con su hora de salida y opción de volver al día siguiente.
- De 1 a 8 plazas para pasajeros, sin contar al conductor.
- Precio por asiento y sentido; 0 € es gratuito. No hay pagos ni reservas automáticos.
- Posibilidad de desvío y detalles (por ejemplo, zonas o minutos de margen).
- Observaciones para equipaje, mascotas, espera u otras condiciones.

El listado permite filtrar por día de salida. El menú ⋮ abre los detalles y preguntas
(visibles para los miembros de la comunidad) o retira el trayecto si eres su autor o
administrador. Los trayectos también aparecen resumidos en la actividad vecinal.
Las publicaciones antiguas de Coche se conservan y se pueden abrir desde el listado.
Los horarios se interpretan como horas locales del recorrido; no se calculan rutas,
duración, coincidencias, reservas ni plazas restantes automáticamente.

La migración 008 añade los detalles estructurados sin cambiar publicaciones existentes.
Validación aislada:

    ./scripts/Test-Posts.ps1 -Suite rides

## Ayuda y servicios

Inicio reúne las antiguas secciones de ayuda y talento en **Ayuda y servicios**.
El filtro Todas incluye únicamente Necesito ayuda, Servicios y Recomendaciones.
Los filtros aparecen como botones visibles y se adaptan al ancho de la pantalla.
Tocar el título o el resumen de una tarjeta abre su detalle; el menú ⋮ conserva
las acciones disponibles. Las vistas previas se limitan a tres líneas y el texto
completo se puede leer en el detalle. Actualizar está en la barra superior.
Al publicar, el listado muestra la categoría elegida para localizar la publicación.

- **Necesito ayuda:** solicitudes, tanto gratuitas como remuneradas (buscar clases o una gestoría).
- **Servicios:** ofertas propias de clases, oficios o negocios. Cada interesado abre una
  conversación privada con quien publica. Solo los dos participantes pueden leerla;
  ser administrador no permite acceder a conversaciones ajenas.
- **Recomendaciones:** experiencias con terceros, por ejemplo recomendar un fontanero.
  Las solicitudes y recomendaciones conservan comentarios visibles para la comunidad.

El formulario explica la visibilidad antes de publicar. Los mensajes privados de servicios
aparecen en la campana y permiten responder desde ella. Al retirar un servicio se ocultan
su ficha, sus conversaciones y sus notificaciones, conservándose los datos en la base.

La migración 009 incorpora conversaciones de servicios; la 010 permite recomendaciones.
Las publicaciones existentes mantienen su categoría. Los antiguos comentarios públicos
de ofertas se conservan en la base, pero dejan de exponerse por la API; no se convierten
automáticamente en conversaciones privadas ni se reasignan a participantes.

Pruebas de filtros, privacidad, notificaciones, reintentos y persistencia:

    ./scripts/Test-Posts.ps1 -Suite services

### Si el emulador sigue mostrando una versión antigua

Una instalación manual del APK de depuración puede seguir cargando ensamblados
antiguos desplegados por Visual Studio mediante Fast Deployment. Detén la depuración
y vuelve a compilar y desplegar desde Visual Studio. Para una instalación manual,
genera un APK completo con `-p:EmbedAssembliesIntoApk=true`; comprueba también que
no quedan ensamblados antiguos en `files/.__override__` del almacenamiento privado
de la app. No borres los datos de la aplicación para resolverlo: perderías su sesión
y preferencias. En la revisión local se apartó esa carpeta como `files/qa-override-backup`.

## Animales: perdidos y encontrados (MVP)

Inicio → **Animales** permite publicar avisos de perros y gatos perdidos o encontrados.
El formulario pide especie, zona aproximada, fecha/hora y descripción; admite nombre
opcional en pérdidas y una foto opcional. En hallazgos se indica si el animal está contigo.
Las imágenes usan el mismo tratamiento de Objetos: JPEG reducido, sin metadatos desde
Android, validación en backend y entrega solo a miembros de la comunidad.

Los avisos activos aparecen primero en su propia lista paginada. **Ver avisos cerrados**
permite consultar los anteriores. El autor puede editar y cerrar con **Ha vuelto a casa**
o **Retirar aviso**; un administrador de esa comunidad solo puede retirarlo. Los cambios
y cierres quedan registrados con actor y fecha. La edición detecta versiones obsoletas.

**Contactar en privado** abre una conversación entre el autor y cada vecino. Reutiliza
los mensajes, reintentos y confirmaciones de lectura del marketplace; aparece en la
campana existente. Los administradores no pueden leer chats ajenos. Tras cerrar el aviso
se conservan las conversaciones existentes; no se abren otras nuevas. No hay alertas
masivas ni notificaciones push: la campana avisa de mensajes mientras la app está abierta.

La migración **012_animal_notices.sql** es aditiva: crea avisos y su historial, y añade
un tipo de conversación a las tablas existentes. No elimina ni transforma publicaciones,
objetos o chats anteriores. Se aplica mediante el mecanismo habitual de migraciones
(en desarrollo al arrancar; en despliegue mediante el paso de migración documentado).
No se encontraron modelos ni tablas de gatos previos en este repositorio.

No incluye registro de mascotas, acreditación de propiedad, microchips, colonias/CER,
mapas, coincidencias ni IA. Un aviso representa una pérdida o hallazgo, no una identidad
permanente ni una prueba de propiedad. No publiques teléfonos ni direcciones precisas.

Pruebas de integración en una base temporal, con PostgreSQL local arrancado:

    pwsh -File scripts/Test-Posts.ps1 -Suite animals

Cubren pérdida, hallazgo, fotografía, contacto, campana, lectura, recuperación, retirada,
privacidad incluso frente a administradores, aislamiento, reintentos concurrentes,
edición obsoleta, paginación y persistencia tras reiniciar. Antes de distribuir, revisar
visualmente en un dispositivo Android el formulario, selección de foto y navegación.

## Administración web de plataforma

La API incluye un panel en **`/admin/`** para el operador de UrbaLink: aprobación
 y suspensión de comunidades, consulta de miembros, bloqueos, moderación de contenido,
denuncias e historial de actuaciones. El acceso requiere un UUID autorizado en el
servidor y contraseña; no se concede por crear una comunidad.

La migración **013** conserva las comunidades existentes como activas y exige aprobación
para nuevas comunidades. La app muestra el estado pendiente e incorpora **Denunciar**
en comunidades, publicaciones, comentarios, objetos, animales y mensajes privados.
Solo los participantes pueden denunciar un mensaje y solo ese mensaje se comparte
con el equipo de plataforma. El panel no ofrece lectura general de chats privados.

Consulta [DEPLOYMENT.md](DEPLOYMENT.md#panel-web-de-administración-de-la-plataforma)
para autorizar tu cuenta en Railway. El panel se despliega
con la API; los botones de denuncia requieren actualizar el APK.

    pwsh -File scripts/Test-Posts.ps1 -Suite platform


## Inicio automático (1.0.3)

La app abre directamente la pantalla de acceso a UrbaLink y recupera la sesión
cuando existe. Los vecinos no introducen direcciones, IP ni comprobaciones técnicas.
Si falla la conexión, la pantalla de acceso permite reintentar y conserva el acceso
guardado. Se mantiene el mismo almacenamiento de sesión para la URL de producción.
Las direcciones `server_address` de versiones anteriores ya no seleccionan el destino;
una sesión de un servidor de pruebas no se envía al servidor de producción.

La propiedad MSBuild `UrbaLinkServerUrl` selecciona el servidor al compilar. Su valor
predeterminado es `https://urbalink-production.up.railway.app`; solo Debug permite
HTTP para pruebas locales. Release exige HTTPS. Si cambia el dominio de producción,
actualiza esta propiedad y distribuye la app; usar un dominio propio estable evita
que los cambios del proveedor obliguen a cambiar la dirección en el APK.
