using System.Windows;

namespace PersonalServerManager.Views.Dialogs;

/// <summary>AWS 연결 변경을 확인하는 앱 테마 모달 창이다.</summary>
public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog(string title, string message, string confirmButtonText)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmButtonText;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CancelButton.Focus();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
