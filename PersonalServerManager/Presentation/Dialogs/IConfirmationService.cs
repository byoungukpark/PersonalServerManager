namespace PersonalServerManager.Presentation.Dialogs;

/// <summary>확인이 필요한 화면 동작을 ViewModel에서 요청하는 경계다.</summary>
public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);
}
