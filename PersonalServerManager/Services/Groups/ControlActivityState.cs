using System.ComponentModel;

namespace PersonalServerManager.Services.Groups;

/// <summary>그룹 제어의 각 자원별 진행 결과다.</summary>
public sealed record ControlStepState(int Position, string Target, string Status, string Message);

/// <summary>앱 전체 제어의 실행 여부와 화면에 표시할 진행 상황을 공유한다.</summary>
public sealed class ControlActivityState : INotifyPropertyChanged
{
    private volatile bool _isControlRunning;
    private string _message = "진행 중인 제어 작업이 없습니다.";
    private string _target = "작업 진행";
    private IReadOnlyList<string> _recent = [];
    private IReadOnlyList<ControlStepState> _steps = [];
    private bool _isGroupControl;
    private volatile bool _cancelRemainingRequested;
    private int _currentStepIndex = -1;
    private string _historyWarning = "";

    public string Message => _message;
    public string Target => _target;
    public IReadOnlyList<string> Recent => _recent;
    public IReadOnlyList<ControlStepState> Steps => _steps;
    public bool HasSteps => _steps.Count > 0;
    public bool CanCancelRemaining => IsControlRunning && _isGroupControl && _steps.Count > 1 && !_cancelRemainingRequested &&
        _currentStepIndex < _steps.Count - 1;
    public bool CancelRemainingRequested => _cancelRemainingRequested;
    public string HistoryWarning => _historyWarning;
    public bool HasHistoryWarning => !string.IsNullOrEmpty(_historyWarning);

    public void ReportHistoryFailure(string message)
    {
        _historyWarning = message;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HistoryWarning)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasHistoryWarning)));
    }
    public string Tone => Message.StartsWith("실패", StringComparison.Ordinal) ||
        Message.StartsWith("시간 초과", StringComparison.Ordinal) ? "error" :
        Message.StartsWith("완료", StringComparison.Ordinal) ? "success" :
        IsControlRunning ? "transitioning" : "unknown";
    public string DisplayMessage => $"{(Tone == "error" ? "⚠" : Tone == "success" ? "✓" : "○")} {Message}";

    public void Report(string target, string message)
    {
        _target = target;
        _message = message;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Target)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tone)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayMessage)));
    }

    public void RememberResult()
    {
        _recent = new[] { $"{DateTimeOffset.Now:HH:mm:ss} · {Target} · {Message}" }
            .Concat(_recent).Take(5).ToArray();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Recent)));
    }

    public void BeginGroup(IReadOnlyList<ControlStepState> steps)
    {
        _steps = steps.ToArray();
        _isGroupControl = true;
        _cancelRemainingRequested = false;
        _currentStepIndex = -1;
        NotifySteps();
    }

    public void BeginIndividual()
    {
        _steps = [];
        _isGroupControl = false;
        _cancelRemainingRequested = false;
        _currentStepIndex = -1;
        NotifySteps();
    }

    public void UpdateStep(int index, string status, string message)
    {
        var updated = _steps.ToArray();
        updated[index] = updated[index] with { Status = status, Message = message };
        _steps = updated;
        _currentStepIndex = index;
        NotifySteps();
    }

    /// <summary>현재 단계는 계속 확인하고 다음 단계의 명령 제출만 막는다.</summary>
    public void RequestCancelRemaining()
    {
        if (!CanCancelRemaining) return;
        _cancelRemainingRequested = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CancelRemainingRequested)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancelRemaining)));
    }

    public void EndGroup()
    {
        _isGroupControl = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancelRemaining)));
    }

    private void NotifySteps()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Steps)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSteps)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CancelRemainingRequested)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancelRemaining)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsControlRunning
    {
        get => _isControlRunning;
        set
        {
            if (_isControlRunning == value)
            {
                return;
            }

            _isControlRunning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsControlRunning)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tone)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayMessage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancelRemaining)));
        }
    }
}
