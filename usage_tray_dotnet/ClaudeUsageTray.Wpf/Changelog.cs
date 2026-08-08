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
