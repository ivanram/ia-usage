namespace ClaudeUsageTray;

/// <summary>
/// The in-app changelog shown in the "Acerca de" window — a hand-written,
/// deliberately short list per version (features described, fixes just
/// summarized as "Correcciones"), not a dump of every commit. Starts at
/// v1.19.4: earlier history was long and undifferentiated, and lives in
/// git/GitHub already, so it isn't backfilled here. Add a new entry (newest
/// first) as part of cutting each release — see the GitHub release notes
/// for the matching (and more detailed) public changelog.
/// </summary>
internal static class Changelog
{
    public sealed record Entry(string Version, string[] Changes);

    public static readonly Entry[] Entries =
    [
        new("2.0.12",
        [
            "Correcciones (la línea de créditos de Claude se quedaba mostrando \"Créditos usados: X\" para siempre en cuentas que agotaron su saldo, aunque ya no quedara nada — ahora esa línea desaparece en cuanto el saldo llega a cero).",
        ]),
        new("2.0.11",
        [
            "Correcciones (las barras de ChatGPT tenían las etiquetas cambiadas: la que se reinicia en horas se llamaba \"Límite semanal\" y la que se reinicia en días se llamaba \"Límite corto\" — ahora se llaman \"Límite de 5 horas\" y \"Límite semanal\" respectivamente, como en Claude).",
        ]),
        new("2.0.10",
        [
            "Correcciones (la app podía cerrarse sola con un aviso de error de tema/color justo tras actualizar o arrancar, si el panel, Estadísticas o un aviso emergente pedían el color activo del sistema antes de que la ventana estuviera del todo lista — ahora esa lectura es segura en todos los sitios donde ocurría, no solo en el que ya estaba cubierto).",
        ]),
        new("2.0.9",
        [
            "El uso de Fable (planes Max/Ultra) ahora se registra como una serie propia en Estadísticas, con su propia gráfica \"Claude - Fable\" separada de la de Claude — antes se leía en el panel principal pero no se guardaba en el historial, así que Estadísticas no tenía nada que mostrar de Fable.",
        ]),
        new("2.0.8",
        [
            "Correcciones (el panel en modo Blur se veía notablemente más grande, con más borde alrededor del contenido, que el mismo panel en modo Estándar — ambos modos comparten ahora la misma reserva de espacio, así que el tamaño y el grosor del borde quedan prácticamente iguales entre los dos).",
        ]),
        new("2.0.7",
        [
            "El panel compacto ahora muestra el mismo prefijo corto (S:, 5H:, U:...) y porcentaje junto a cada barra para todos los servicios, no solo para Claude — antes ChatGPT y Grok se quedaban con un formato distinto, solo el porcentaje suelto. También se ha reducido a la mitad el margen entre el contenido y el borde de la ventana en este modo.",
            "Corregido el contraste de texto en los botones con color de acento (Guardar, etc.): la fórmula de contraste real introducida en su día para elegir negro o blanco tenía un fallo — su punto de corte matemático quedaba tan bajo que, en la práctica, elegía negro para casi todos los colores del selector (morado, verde, azul...) en vez de blanco, justo lo contrario de lo que buscaba.",
        ]),
        new("2.0.6",
        [
            "Corregido un fallo grave: la versión ligera (-fx) se publicaba sin una librería nativa que SQLite necesita para arrancar, así que la app podía morir en el arranque sin abrir ninguna ventana ni avisar de nada — solo quedaba un proceso invisible. Ahora ese fallo (o cualquier otro relacionado con el historial/estadísticas) ya no impide que se abra el resto de la app.",
            "Cualquier error inesperado al arrancar, o durante el uso normal, se muestra ahora en un aviso claro en vez de fallar en silencio.",
            "Si la carpeta donde está instalada la app no tiene permisos de escritura para tu usuario (lo que rompe la actualización automática), ahora avisa de ello al arrancar. El instalador, además, ya no deja instalar en Program Files bajo ningún concepto — antes lo permitía si insistías, pidiendo permisos de administrador, pero eso es precisamente lo que causaba el problema.",
            "El aviso de actualización disponible mostraba a veces símbolos de markdown sin más (##, **) en vez de texto limpio; corregido.",
        ]),
        new("2.0.5",
        [
            "Los iconos del panel compacto (icono de servicio, y los de estadísticas/fijar/expandir-contraer arriba, refrescar/ajustes abajo) son un poco más pequeños, para que dejen de verse desproporcionados frente al resto del panel; el icono de expandir/contraer en concreto también se ha ajustado en modo completo, donde se veía más grande que sus vecinos.",
            "El cambio entre vista compacta y completa ahora se anima con un fundido en vez de aparecer de golpe a mitad del redimensionado de la ventana, que es lo que se sentía como un salto brusco.",
        ]),
        new("2.0.4",
        [
            "Correcciones (la barra semanal de Fable no llegaba a mostrarse en ninguna cuenta: se buscaba en el sitio equivocado del JSON de uso; ahora se lee del campo real, confirmado con un caso real).",
        ]),
        new("2.0.3",
        [
            "Correcciones (la actualización automática se quedaba sin hacer nada si la app estaba instalada en una carpeta que requiere permisos de administrador, como Program Files: ahora pide permisos con el aviso de Windows en ese caso, en vez de fallar en silencio).",
        ]),
        new("2.0.2",
        [
            "Nuevo modo compacto para el panel: un botón alterna entre la vista completa y una versión reducida con solo lo esencial (icono, % y una barra fina por servicio). Qué servicios se muestran en modo compacto se elige aparte, en Ajustes → Servicios — Claude activado por defecto. Si un servicio tiene varias barras (el límite de 5 horas y el semanal de Claude, por ejemplo), en compacto se apilan juntas con un prefijo corto (S:, 5H:).",
            "Si tu cuenta de Claude tiene un límite semanal aparte para el modelo Fable (planes Max), ahora se muestra como una barra propia junto a las demás, tanto en la vista completa como en la compacta.",
            "Correcciones (contraste de texto en botones con color de acento, orden de los iconos del panel).",
        ]),
        new("2.0.1",
        [
            "Correcciones (el instalador podía dar un error de permisos al elegir instalarlo manualmente en Program Files; ahora avisa y no deja continuar con esa carpeta).",
        ]),
        new("2.0.0",
        [
            "El instalador (ClaudeUsageTraySetup.exe) ahora tiene una imagen de portada propia en el asistente de instalación, en vez del fondo genérico de Inno Setup.",
        ]),
        new("1.19.9",
        [
            "Nuevo instalador (ClaudeUsageTraySetup.exe) como alternativa a los ejecutables sueltos: instala en la carpeta del usuario (sin pedir permisos de administrador), crea acceso directo en el menú Inicio y opción de inicio automático con Windows, y evita de raíz los problemas de permisos de quien lo guardaba en Program Files.",
        ]),
        new("1.19.8",
        [
            "Correcciones (el archivo de diagnóstico de arranque no se creaba si la app estaba instalada en una carpeta sin permisos de escritura, como Program Files, sin ser administrador; ahora cae automáticamente a la carpeta de logs habitual).",
        ]),
        new("1.19.7",
        [
            "Nuevo archivo \"diagnostico_inicio.txt\" junto al ejecutable: registra cada paso del arranque (mutex, ventana de bandeja, errores) para poder saber por qué no abre en un equipo donde falla sin dejar ningún proceso ni ventana.",
        ]),
        new("1.19.6",
        [
            "El enlace de \"Buscar actualizaciones\" en Acerca de ahora va junto a la versión, como texto, en vez de un icono junto al nombre.",
            "Mejor contraste en los botones con color de acento (azul, morado, verde, naranja, rosa, oliva...): el texto ahora se calcula con una fórmula de contraste real en vez de dejarlo a la librería.",
            "Los avisos de reinicio y de límite agotado (en el escritorio y por Telegram) ahora indican entre paréntesis si es el límite semanal, el de 5 horas, etc.",
            "Correcciones (tooltips del panel flotante que se quedaban en español al cambiar el idioma a inglés).",
        ]),
        new("1.19.5",
        [
            "Nueva ventana \"Acerca de\" (desde el menú de la bandeja o pulsando la versión en Ajustes): icono, versión, ruta del ejecutable, botón para buscar actualizaciones junto al nombre de la app, y esta misma lista de novedades.",
            "El aviso de actualización disponible ahora muestra la lista de cambios de la nueva versión antes de descargarla.",
            "Correcciones (vista de calendario en Estadísticas ya no muestra scroll).",
        ]),
        new("1.19.4",
        [
            "Nueva vista previa en vivo en Ajustes → Apariencia: los cambios de estilo, opacidad, blur y color de acento se ven al instante en el panel, sin pulsar Guardar antes.",
            "El panel fijado ahora tiene botón de cerrar (X) y se mantiene fijado mientras lo arrastras.",
            "Correcciones (cambio de modo Estándar/Blur, vista de calendario en Estadísticas).",
        ]),
    ];
}
