using System.Windows;

namespace PersonalServerManager.Presentation.Dialogs;

/// <summary>현재 단계의 프로필 전환·연결 해제 확인창을 표시한다.</summary>
public sealed class MessageBoxConfirmationService : IConfirmationService
{
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var owner = System.Windows.Application.Current.MainWindow;
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result == MessageBoxResult.Yes);
    }
}
