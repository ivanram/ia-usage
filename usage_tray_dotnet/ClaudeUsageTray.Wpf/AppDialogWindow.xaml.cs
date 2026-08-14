using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ClaudeUsageTray;

public enum DialogChoice
{
    No,
    Yes,
    Later,
}

public partial class AppDialogWindow : Window
{
    private DialogChoice _choice = DialogChoice.No;

    public AppDialogWindow()
    {
        InitializeComponent();

        // Same 18px-native-draw approach as SettingsWindow's caption icon —
        // downscaling a larger source blurred the visor/eyes into a blob.
        using var icon = IconFactory.BuildRobotIcon(18);
        AppIcon.Source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        AppNameText.Text = Strings.T("app.name");
    }

    private void OnYesClick(object sender, RoutedEventArgs e)
    {
        _choice = DialogChoice.Yes;
        Close();
    }

    private void OnNoClick(object sender, RoutedEventArgs e)
    {
        _choice = DialogChoice.No;
        Close();
    }

    private void OnLaterClick(object sender, RoutedEventArgs e)
    {
        _choice = DialogChoice.Later;
        Close();
    }

    /// <summary>The app-name header row already identifies the dialog, so a title that would just repeat it is left out (collapsed) rather than shown twice.</summary>
    private void SetTitle(string title)
    {
        TitleText.Text = title;
        TitleText.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// <paramref name="changelog"/> is the raw GitHub release body (markdown
    /// "- " bullets) fetched live from the release being offered — not the
    /// locally-embedded <see cref="Changelog"/> used by the About window,
    /// since the CURRENTLY RUNNING (old) build has no way to know what's in
    /// a version it hasn't installed yet. Hidden entirely when there's
    /// nothing to show instead of leaving an empty scroll area.
    /// </summary>
    private void SetChangelog(string? changelog)
    {
        var bullets = ParseBullets(changelog);
        if (bullets.Count == 0)
        {
            ChangelogScroll.Visibility = Visibility.Collapsed;
            return;
        }

        ChangelogText.Text = string.Join("\n", bullets.Select(b => $"•  {b}"));
        ChangelogScroll.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The raw text only ever came from OUR OWN GitHub release notes (see
    /// UpdateService.GetLatestReleaseAsync), never arbitrary markdown, but
    /// that first cut still leaked two kinds of it straight into the
    /// dialog: any non-bullet line (a "## Correcciones" / "## Descargas"
    /// heading, a blank separator) fell through untouched instead of being
    /// dropped, and "**bold**" runs inside real bullets stayed as literal
    /// asterisks — both read as broken markdown syntax rather than a
    /// changelog. Now: only genuine "- " bullets survive at all, the
    /// "## Descargas" section (asset filenames/sizes, not a change) is cut
    /// before parsing even starts, and "**...**" markers are stripped from
    /// whatever bullets remain.
    /// </summary>
    private static List<string> ParseBullets(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var normalized = text.Replace("\r\n", "\n");
        var downloadsAt = IndexOfHeading(normalized, "Descargas", "Downloads");
        if (downloadsAt >= 0) normalized = normalized[..downloadsAt];

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("- ")) continue;
            line = line[2..].Trim().Replace("**", "");
            if (line.Length == 0) continue;
            result.Add(line);
        }
        return result;
    }

    /// <summary>Index of the start of a "## &lt;name&gt;" heading line (any of the given names, case-insensitive), or -1 if none is present.</summary>
    private static int IndexOfHeading(string text, params string[] names)
    {
        var lines = text.Split('\n');
        var offset = 0;
        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith("#") && names.Any(n => trimmed.TrimStart('#').Trim().Equals(n, StringComparison.OrdinalIgnoreCase)))
            {
                return offset;
            }
            offset += rawLine.Length + 1; // +1 for the '\n' the Split already consumed
        }
        return -1;
    }

    /// <summary>Two-button Sí/No prompt. Returns true if the user picked Sí.</summary>
    public static bool ShowYesNo(string title, string message)
    {
        var dialog = new AppDialogWindow();
        dialog.SetTitle(title);
        dialog.MessageText.Text = message;
        dialog.NoButton.Content = Strings.T("dialog.no");
        dialog.YesButton.Content = Strings.T("dialog.yes");
        dialog.ShowDialog();
        return dialog._choice == DialogChoice.Yes;
    }

    /// <summary>Single-button acknowledgement dialog — hides "No", relabels "Sí" to "Vale".</summary>
    public static void ShowInfo(string title, string message)
    {
        var dialog = new AppDialogWindow();
        dialog.SetTitle(title);
        dialog.MessageText.Text = message;
        dialog.NoButton.Visibility = Visibility.Collapsed;
        dialog.YesButton.Content = Strings.T("dialog.ok");
        dialog.YesButton.Width = 110;
        dialog.ShowDialog();
    }

    /// <summary>Three-way Sí / Hoy no, mañana / No prompt used for the update-available check.</summary>
    public static DialogChoice ShowUpdatePrompt(string title, string message, string? changelog = null)
    {
        var dialog = new AppDialogWindow();
        dialog.SetTitle(title);
        dialog.MessageText.Text = message;
        dialog.SetChangelog(changelog);
        dialog.NoButton.Content = Strings.T("dialog.no");
        dialog.LaterButton.Content = Strings.T("dialog.later");
        dialog.LaterButton.Visibility = Visibility.Visible;
        dialog.YesButton.Content = Strings.T("dialog.yes");
        dialog.ShowDialog();
        return dialog._choice;
    }
}
