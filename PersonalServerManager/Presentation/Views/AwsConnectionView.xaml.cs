using System.Windows;
using System.Windows.Controls;
using PersonalServerManager.Presentation.ViewModels;

namespace PersonalServerManager.Presentation.Views;

public partial class AwsConnectionView : UserControl
{
    public AwsConnectionView()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AwsConnectionViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }
}
