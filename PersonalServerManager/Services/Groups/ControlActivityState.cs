using System.ComponentModel;

namespace PersonalServerManager.Services.Groups;

/// <summary>향후 전역 제어 코디네이터가 소유할 실행 여부를 연결 정책과 공유한다.</summary>
public sealed class ControlActivityState : INotifyPropertyChanged
{
    private volatile bool _isControlRunning;

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
        }
    }
}
