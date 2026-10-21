using System.ComponentModel;

namespace PersonalServerManager.Services.Groups;

/// <summary>향후 전역 제어 코디네이터가 소유할 실행 여부를 연결 정책과 공유한다.</summary>
public sealed class ControlActivityState : INotifyPropertyChanged
{
    private volatile bool _isControlRunning;
    private string _message = "진행 중인 제어 작업이 없습니다.";
    private string _target = "작업 진행";
    private IReadOnlyList<string> _recent = [];

    public string Message => _message;
    public string Target => _target;
    public IReadOnlyList<string> Recent => _recent;
    public string Tone => Message.StartsWith("실패", StringComparison.Ordinal) ? "error" :
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
        }
    }
}
