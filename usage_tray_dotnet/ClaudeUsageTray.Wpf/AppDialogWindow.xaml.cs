using System.Windows;

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

    /// <summary>Two-button Sí/No prompt. Returns true if the user picked Sí.</summary>
    public static bool ShowYesNo(string title, string message)
    {
        var dialog = new AppDialogWindow();
        dialog.TitleText.Text = title;
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
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.NoButton.Visibility = Visibility.Collapsed;
        dialog.YesButton.Content = Strings.T("dialog.ok");
        dialog.YesButton.Width = 110;
        dialog.ShowDialog();
    }

    /// <summary>Three-way Sí / Hoy no, mañana / No prompt used for the update-available check.</summary>
    public static DialogChoice ShowUpdatePrompt(string title, string message)
    {
        var dialog = new AppDialogWindow();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.NoButton.Content = Strings.T("dialog.no");
        dialog.LaterButton.Content = Strings.T("dialog.later");
        dialog.LaterButton.Visibility = Visibility.Visible;
        dialog.YesButton.Content = Strings.T("dialog.yes");
        dialog.ShowDialog();
        return dialog._choice;
    }
}
