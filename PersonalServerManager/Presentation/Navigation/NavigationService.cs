using PersonalServerManager.Presentation.ViewModels;

namespace PersonalServerManager.Presentation.Navigation;

/// <summary>등록된 화면 ViewModel 사이의 이동과 현재 화면 상태를 관리한다.</summary>
public sealed class NavigationService : ViewModelBase
{
    private readonly Dictionary<AppPage, ViewModelBase> _pages = [];
    private AppPage _currentPage;
    private ViewModelBase? _currentViewModel;

    public AppPage CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public ViewModelBase? CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetProperty(ref _currentViewModel, value);
    }

    public void Register(AppPage page, ViewModelBase viewModel)
    {
        _pages.Add(page, viewModel);
    }

    public void NavigateTo(AppPage page)
    {
        if (!_pages.TryGetValue(page, out var viewModel))
        {
            throw new InvalidOperationException($"등록되지 않은 화면: {page}");
        }

        CurrentPage = page;
        CurrentViewModel = viewModel;
    }
}
