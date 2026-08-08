using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ClaudeUsageTray;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // Same 40px native-draw approach StatsWindow/SettingsWindow use for
        // their own icons — downscaling a larger source blurs the detail.
        using var icon = IconFactory.BuildRobotIcon(40);
        AppIcon.Source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        AppNameText.Text = Strings.T("app.name");
        ToolTipService.SetToolTip(CheckUpdateBtn, Strings.T("about.checkupdate.tooltip"));
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : Strings.F("about.version", $"{version.Major}.{version.Minor}.{version.Build}");

        PathLabel.Text = Strings.T("about.path.label");
        PathText.Text = Environment.ProcessPath ?? "";

        ChangelogLabel.Text = Strings.T("about.changelog.label");
        BuildChangelog();
    }

    private void BuildChangelog()
    {
        for (var i = 0; i < Changelog.Entries.Length; i++)
        {
            var entry = Changelog.Entries[i];

            var header = new TextBlock
            {
                Text = $"v{entry.Version}",
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, i == 0 ? 0 : 16, 0, 6),
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "MaterialDesignBody");
            ChangelogHost.Children.Add(header);

            foreach (var change in entry.Changes)
            {
                var line = new TextBlock
                {
                    Text = $"•  {change}",
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18,
                    Opacity = 0.8,
                    Margin = new Thickness(0, 0, 0, 6),
                };
                line.SetResourceReference(TextBlock.ForegroundProperty, "MaterialDesignBody");
                ChangelogHost.Children.Add(line);
            }
        }
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        try
        {
            await UpdateService.CheckAndPromptAsync(manualCheck: true);
        }
        finally
        {
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
