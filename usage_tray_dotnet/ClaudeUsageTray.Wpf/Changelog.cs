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
