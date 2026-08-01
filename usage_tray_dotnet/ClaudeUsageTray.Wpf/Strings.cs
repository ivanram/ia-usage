namespace ClaudeUsageTray;

public enum AppLanguage
{
    Spanish,
    English,
}

/// <summary>
/// A flat key→text lookup rather than .resx, since almost all of this app's
/// UI is built in code (not XAML) — a plain dictionary is far less
/// ceremony than wiring up culture-specific resource assemblies for what
/// amounts to two languages. <see cref="Current"/> is process-global and
/// mutated in place (not per-window state) so every already-open window,
/// the tray context menu, and anything built fresh afterward all agree —
/// see TrayOrchestrator's language preview/apply for how live switching
/// is wired up.
/// </summary>
internal static class Strings
{
    public static AppLanguage Current { get; set; } = AppLanguage.Spanish;

    public static string T(string key) =>
        (Current == AppLanguage.Spanish ? Es : En).TryGetValue(key, out var v) ? v : key;

    public static string F(string key, params object[] args) => string.Format(T(key), args);

    private static readonly Dictionary<string, string> Es = new()
    {
        ["app.name"] = "Uso de IA",

        ["settings.title"] = "Ajustes",
        ["settings.cancel"] = "Cancelar",
        ["settings.save"] = "Guardar",
        ["settings.version.tooltip"] = "Buscar actualizaciones",
        ["settings.version.checking"] = "Buscando actualizaciones...",

        ["card.general"] = "General",
        ["card.update"] = "Actualización",
        ["card.popup"] = "Panel emergente",
        ["card.appearance"] = "Apariencia",
        ["card.services"] = "Servicios",
        ["card.telegram"] = "Bot de Telegram",

        ["general.autostart.label"] = "Iniciar con Windows",
        ["general.autostart.hint"] = "Abre la app automáticamente al encender el equipo",
        ["general.animations.label"] = "Animaciones",
        ["general.animations.hint"] = "Anima las barras de progreso del panel emergente",
        ["general.language.label"] = "Idioma",
        ["general.autoupdate.label"] = "Buscar actualizaciones automáticamente",
        ["general.autoupdate.hint"] = "Si lo desactivas, puedes comprobarlo tú mismo desde el enlace de versión de abajo",

        ["update.frequency.label"] = "Frecuencia de actualización (en minutos)",

        ["popup.mode.tooltip"] = "Tooltip sencillo",
        ["popup.mode.rich"] = "Ventana flotante (recomendado)",
        ["popup.mode.hoverhint"] = "Mostrar panel al pasar el ratón por encima",
        ["hoverdelay.instant"] = "Instantáneo",

        ["theme.system"] = "Sistema",
        ["theme.light"] = "Claro",
        ["theme.dark"] = "Oscuro",
        ["appearance.accent.hint"] = "Color de acento — \"Original\" colorea las barras del panel según el % de uso",

        ["service.loggedin"] = "Sesión iniciada",
        ["service.link"] = "Vincular cuenta",
        ["service.sound.label"] = "Recibir notificación con sonido",
        ["service.sound.hint"] = "Se te notificará con sonido cuando se reinicie el uso",
        ["service.notify.tooltip"] = "Avisar cuando se reinicie el límite o cuando se agote",

        ["telegram.enable.label"] = "Activar bot de Telegram",
        ["telegram.linked"] = "Chat vinculado",
        ["telegram.linked.hint"] = "Escríbele /uso al bot en cualquier momento para consultar tu consumo actual de todos los servicios activos, directamente desde Telegram.",
        ["telegram.setup.title"] = "Para vincular el bot:",
        ["telegram.setup.1"] = "1. Abre Telegram y busca a @BotFather.",
        ["telegram.setup.2"] = "2. Envíale /newbot y sigue los pasos para crear tu bot.",
        ["telegram.setup.3"] = "3. Pega aquí el token que te entregue y guarda los ajustes.",
        ["telegram.setup.4"] = "4. Escríbele /uso a tu nuevo bot para vincular el chat.",
        ["telegram.notifyusage.label"] = "Recibir notificaciones de uso por Telegram",
        ["telegram.notifyusage.hint"] = "Te avisará por Telegram cuando se reinicie un límite o se agote, igual que en el escritorio",
        ["telegram.notify80.label"] = "Recibir también avisos al 80%",
        ["telegram.notify80.hint"] = "Además te avisará por Telegram en cuanto un servicio llegue al 80% de uso",
        ["telegram.notify80.message"] = "Ojo, que tienes {0} al 80% 🫣",

        ["popup.noservices"] = "No hay servicios activos. Ábrelos desde Ajustes.",
        ["popup.error.generic"] = "No se pudo leer el uso",
        ["popup.updated"] = "Actualizado {0}",
        ["popup.resets"] = "Se reinicia {0}",
        ["popup.tooltip.refresh"] = "Actualizar",
        ["popup.tooltip.settings"] = "Ajustes",

        ["stats.title"] = "Estadísticas",
        ["stats.subtitle"] = "Últimas horas",
        ["stats.empty"] = "Aún no hay suficientes datos. Vuelve más tarde para ver la evolución de tu uso.",
        ["stats.noservices"] = "No hay servicios activos.",

        ["tray.tooltip.starting"] = "{0}: iniciando...",
        ["tray.tooltip.noservices"] = "{0}: no hay servicios activos (clic derecho → Ajustes)",
        ["menu.refresh"] = "Actualizar ahora",
        ["menu.settings"] = "Ajustes...",
        ["menu.login"] = "Iniciar sesión",
        ["menu.exit"] = "Salir",
        ["loading"] = "Cargando...",

        ["toast.reset"] = "El uso de {0} se ha reseteado ✨",
        ["toast.exhausted"] = "Se te ha gastado {0} 😭",

        ["dialog.yes"] = "Sí",
        ["dialog.no"] = "No",
        ["dialog.later"] = "Hoy no, mañana",
        ["dialog.ok"] = "Vale",
        ["dialog.update.title"] = "{0} — Actualización disponible",
        ["dialog.update.message"] = "Hay una nueva versión disponible (v{0}).\n¿Quieres actualizarla ahora?",
        ["dialog.checkfailed.message"] = "No se ha podido comprobar si hay actualizaciones.",
        ["dialog.ratelimited.message"] = "GitHub está limitando temporalmente las comprobaciones de actualización. Inténtalo de nuevo dentro de un rato.",
        ["dialog.toosoon.message"] = "Ya se ha comprobado hace un momento. Inténtalo de nuevo en unos segundos.",
        ["dialog.uptodate.message"] = "Ya tienes la última versión.",

        ["time.available"] = "ya disponible",
        ["time.ago.moment"] = "hace un momento",
        ["time.ago.seconds"] = "hace unos segundos",
        ["time.ago.minutes"] = "hace {0} min",
        ["time.ago.hours"] = "hace {0} h",
        ["time.ago.days"] = "hace {0} d",

        ["provider.claude.5h"] = "Límite de 5 horas",
        ["provider.weekly"] = "Límite semanal",
        ["provider.chatgpt.short"] = "Límite corto",
        ["provider.grok.usage"] = "Límite de uso",
        ["provider.timeout"] = "Tiempo de espera agotado",
        ["provider.grok.loginneeded"] = "Inicia sesión en la ventana de Grok",
        ["provider.chatgpt.credits"] = "Saldo de créditos: {0}",
        ["provider.claude.credits.used"] = "Créditos usados: {0:0.00} {1}",
        ["provider.claude.credits.used_of"] = "Créditos usados: {0:0.00} / {1:0.00} {2}",

        ["telegrambot.prompt"] = "Pulsa el botón o escribe /uso para ver tu consumo.",
        ["telegrambot.none"] = "No hay servicios activos todavía.",
        ["telegrambot.nodata"] = "sin datos",
        ["telegrambot.usebutton"] = "Usa el botón «{0}» o el comando /uso.",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["app.name"] = "AI Usage",

        ["settings.title"] = "Settings",
        ["settings.cancel"] = "Cancel",
        ["settings.save"] = "Save",
        ["settings.version.tooltip"] = "Check for updates",
        ["settings.version.checking"] = "Checking for updates...",

        ["card.general"] = "General",
        ["card.update"] = "Updates",
        ["card.popup"] = "Popup panel",
        ["card.appearance"] = "Appearance",
        ["card.services"] = "Services",
        ["card.telegram"] = "Telegram bot",

        ["general.autostart.label"] = "Start with Windows",
        ["general.autostart.hint"] = "Opens the app automatically when your PC starts",
        ["general.animations.label"] = "Animations",
        ["general.animations.hint"] = "Animates the popup panel's progress bars",
        ["general.language.label"] = "Language",
        ["general.autoupdate.label"] = "Automatically check for updates",
        ["general.autoupdate.hint"] = "Turn this off and you can still check manually from the version link below",

        ["update.frequency.label"] = "Refresh frequency (in minutes)",

        ["popup.mode.tooltip"] = "Simple tooltip",
        ["popup.mode.rich"] = "Floating window (recommended)",
        ["popup.mode.hoverhint"] = "Show panel on hover",
        ["hoverdelay.instant"] = "Instant",

        ["theme.system"] = "System",
        ["theme.light"] = "Light",
        ["theme.dark"] = "Dark",
        ["appearance.accent.hint"] = "Accent color — \"Original\" colors the panel bars based on usage %",

        ["service.loggedin"] = "Signed in",
        ["service.link"] = "Link account",
        ["service.sound.label"] = "Play a sound with notifications",
        ["service.sound.hint"] = "You'll hear a sound when your usage resets",
        ["service.notify.tooltip"] = "Notify when usage resets or runs out",

        ["telegram.enable.label"] = "Enable Telegram bot",
        ["telegram.linked"] = "Chat linked",
        ["telegram.linked.hint"] = "Message /uso to the bot any time to check your current usage across every active service, right from Telegram.",
        ["telegram.setup.title"] = "To link the bot:",
        ["telegram.setup.1"] = "1. Open Telegram and search for @BotFather.",
        ["telegram.setup.2"] = "2. Send it /newbot and follow the steps to create your bot.",
        ["telegram.setup.3"] = "3. Paste the token it gives you here and save your settings.",
        ["telegram.setup.4"] = "4. Message /uso to your new bot to link the chat.",
        ["telegram.notifyusage.label"] = "Get usage notifications on Telegram",
        ["telegram.notifyusage.hint"] = "You'll be notified on Telegram whenever a limit resets or runs out, same as on the desktop",
        ["telegram.notify80.label"] = "Also notify at 80%",
        ["telegram.notify80.hint"] = "Also pings you on Telegram as soon as a service reaches 80% usage",
        ["telegram.notify80.message"] = "Heads up, {0} is at 80% 🫣",

        ["popup.noservices"] = "No active services. Enable them from Settings.",
        ["popup.error.generic"] = "Couldn't read usage",
        ["popup.updated"] = "Updated {0}",
        ["popup.resets"] = "Resets {0}",
        ["popup.tooltip.refresh"] = "Refresh",
        ["popup.tooltip.settings"] = "Settings",

        ["stats.title"] = "Statistics",
        ["stats.subtitle"] = "Last few hours",
        ["stats.empty"] = "Not enough data yet. Check back later to see how your usage has been trending.",
        ["stats.noservices"] = "No active services.",

        ["tray.tooltip.starting"] = "{0}: starting...",
        ["tray.tooltip.noservices"] = "{0}: no active services (right-click → Settings)",
        ["menu.refresh"] = "Refresh now",
        ["menu.settings"] = "Settings...",
        ["menu.login"] = "Sign in",
        ["menu.exit"] = "Exit",
        ["loading"] = "Loading...",

        ["toast.reset"] = "{0} usage has reset ✨",
        ["toast.exhausted"] = "You've run out of {0} 😭",

        ["dialog.yes"] = "Yes",
        ["dialog.no"] = "No",
        ["dialog.later"] = "Not today",
        ["dialog.ok"] = "OK",
        ["dialog.update.title"] = "{0} — Update available",
        ["dialog.update.message"] = "A new version is available (v{0}).\nDo you want to update now?",
        ["dialog.checkfailed.message"] = "Couldn't check for updates.",
        ["dialog.ratelimited.message"] = "GitHub is temporarily rate-limiting update checks. Try again in a little while.",
        ["dialog.toosoon.message"] = "Already checked a moment ago. Try again in a few seconds.",
        ["dialog.uptodate.message"] = "You're already on the latest version.",

        ["time.available"] = "available now",
        ["time.ago.moment"] = "just now",
        ["time.ago.seconds"] = "a few seconds ago",
        ["time.ago.minutes"] = "{0} min ago",
        ["time.ago.hours"] = "{0} h ago",
        ["time.ago.days"] = "{0} d ago",

        ["provider.claude.5h"] = "5-hour limit",
        ["provider.weekly"] = "Weekly limit",
        ["provider.chatgpt.short"] = "Short-term limit",
        ["provider.grok.usage"] = "Usage limit",
        ["provider.timeout"] = "Request timed out",
        ["provider.grok.loginneeded"] = "Sign in from the Grok window",
        ["provider.chatgpt.credits"] = "Credit balance: {0}",
        ["provider.claude.credits.used"] = "Credits used: {0:0.00} {1}",
        ["provider.claude.credits.used_of"] = "Credits used: {0:0.00} / {1:0.00} {2}",

        ["telegrambot.prompt"] = "Tap the button or send /uso to check your usage.",
        ["telegrambot.none"] = "No active services yet.",
        ["telegrambot.nodata"] = "no data",
        ["telegrambot.usebutton"] = "Use the \"{0}\" button or the /uso command.",
    };
}
