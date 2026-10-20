namespace PersonalServerManager.Services.Dialogs;

/// <summary>확인이 필요한 화면 동작을 ViewModel에서 요청하는 경계다.</summary>
public interface IConfirmationService
{
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmButtonText,
        CancellationToken cancellationToken = default);
}
