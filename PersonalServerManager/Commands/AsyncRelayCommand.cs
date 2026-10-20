using System.Windows.Input;

namespace PersonalServerManager.Commands;

/// <summary>비동기 화면 명령의 중복 실행과 취소를 관리한다.</summary>
public sealed class AsyncRelayCommand(
    Func<CancellationToken, Task> execute,
    Func<bool> canExecute,
    Action<Exception> onError) : ICommand
{
    private CancellationTokenSource? _cancellation;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _cancellation is null && canExecute();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        RaiseCanExecuteChanged();

        try
        {
            await execute(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 화면 종료 등으로 취소된 작업은 사용자 오류로 표시하지 않는다.
        }
        catch (Exception exception)
        {
            onError(exception);
        }
        finally
        {
            _cancellation = null;
            RaiseCanExecuteChanged();
        }
    }

    public void Cancel() => _cancellation?.Cancel();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
