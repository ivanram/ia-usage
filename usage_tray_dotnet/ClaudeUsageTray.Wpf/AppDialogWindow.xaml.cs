using System.Windows;

namespace ClaudeUsageTray;

public partial class AppDialogWindow : Window
{
    private bool _result;

    public AppDialogWindow()
    {
        InitializeComponent();
    }

    private void OnYesClick(object sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    private void OnNoClick(object sender, RoutedEventArgs e)
    {
        _result = false;
        Close();
    }

    /// <summary>Two-button Sí/No prompt. Returns true if the user picked Sí.</summary>
    public static bool ShowYesNo(string title, string message)
    {
        var dialog = new AppDialogWindow();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ShowDialog();
        return dialog._result;
    }

    /// <summary>Single-button acknowledgement dialog — hides "No", relabels "Sí" to "Vale".</summary>
    public static void ShowInfo(string title, string message)
    {
        var dialog = new AppDialogWindow();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.NoButton.Visibility = Visibility.Collapsed;
        dialog.YesButton.Content = "Vale";
        dialog.YesButton.Width = 110;
        dialog.ShowDialog();
    }
}
