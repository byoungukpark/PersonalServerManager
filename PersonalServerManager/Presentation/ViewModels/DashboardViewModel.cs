using System.Windows.Input;
using PersonalServerManager.Presentation.Commands;
using PersonalServerManager.Presentation.Navigation;

namespace PersonalServerManager.Presentation.ViewModels;

public sealed class DashboardViewModel : ViewModelBase
{
    public DashboardViewModel(NavigationService navigation)
    {
        CreateGroupCommand = new RelayCommand(_ => navigation.NavigateTo(AppPage.GroupWorkspace));
    }

    public ICommand CreateGroupCommand { get; }
}
