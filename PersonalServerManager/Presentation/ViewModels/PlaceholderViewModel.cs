namespace PersonalServerManager.Presentation.ViewModels;

public sealed class PlaceholderViewModel(string title, string description) : ViewModelBase
{
    public string Title { get; } = title;
    public string Description { get; } = description;
}
