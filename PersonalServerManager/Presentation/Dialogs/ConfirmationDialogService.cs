using System.Windows;

namespace PersonalServerManager.Presentation.Dialogs;

/// <summary>앱 테마 확인창을 소유 창 가운데에 모달로 표시한다.</summary>
public sealed class ConfirmationDialogService : IConfirmationService
{
    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmButtonText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dialog = new ConfirmationDialog(title, message, confirmButtonText);
        if (System.Windows.Application.Current.MainWindow is { } owner)
        {
            dialog.Owner = owner;
        }

        var confirmed = dialog.ShowDialog() == true;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(confirmed);
    }
}
