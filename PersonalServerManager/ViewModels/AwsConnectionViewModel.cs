using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using PersonalServerManager.Services.Aws;
using PersonalServerManager.Services.Persistence;
using PersonalServerManager.Commands;
using PersonalServerManager.Services.Dialogs;

namespace PersonalServerManager.ViewModels;

/// <summary>프로필 선택과 명시적 연결 요청을 화면 상태로 표현한다.</summary>
public sealed class AwsConnectionViewModel : ViewModelBase
{
    private readonly AwsConnectionService _connection;
    private readonly ISelectedProfileStore _selectedProfileStore;
    private readonly IConfirmationService _confirmation;
    private readonly SemaphoreSlim _selectionSaveGate = new(1, 1);
    private readonly AsyncRelayCommand _refreshProfilesCommand;
    private readonly AsyncRelayCommand _connectCommand;
    private readonly AsyncRelayCommand _verifyCommand;
    private readonly AsyncRelayCommand _disconnectCommand;
    private string? _selectedProfile;
    private string _profileListMessage = "로컬 AWS 프로필을 읽는 중입니다.";
    private string _busyMessage = string.Empty;
    private string _errorMessage = string.Empty;
    private string _successMessage = string.Empty;
    private bool _isBusy;
    private bool _profilesLoaded;
    private bool _isApplyingProfileList;
    private string? _lastSelectedProfileName;
    private string? _selectionSaveError;
    private Task _pendingSelectionSave = Task.CompletedTask;
    private int _selectionSaveVersion;
    private int _successMessageGeneration;

    public AwsConnectionViewModel(
        AwsConnectionService connection,
        ISelectedProfileStore selectedProfileStore,
        IConfirmationService confirmation,
        string? restoredProfileName)
    {
        _connection = connection;
        _selectedProfileStore = selectedProfileStore;
        _confirmation = confirmation;
        _lastSelectedProfileName = restoredProfileName;

        _refreshProfilesCommand = new AsyncRelayCommand(RefreshProfilesAsync, () => !IsBusy, ShowError);
        _connectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect, ShowError);
        _verifyCommand = new AsyncRelayCommand(VerifyAsync, CanVerify, ShowError);
        _disconnectCommand = new AsyncRelayCommand(DisconnectAsync, CanDisconnect, ShowError);
        _connection.PropertyChanged += OnConnectionChanged;
    }

    public ObservableCollection<string> Profiles { get; } = [];
    public ICommand RefreshProfilesCommand => _refreshProfilesCommand;
    public ICommand ConnectCommand => _connectCommand;
    public ICommand VerifyCommand => _verifyCommand;
    public ICommand DisconnectCommand => _disconnectCommand;

    public string? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                RefreshCommandStates();
                if (_profilesLoaded && !_isApplyingProfileList)
                {
                    _lastSelectedProfileName = value;
                    _pendingSelectionSave = SaveSelectedProfileAsync(++_selectionSaveVersion);
                }
            }
        }
    }

    public string ProfileListMessage
    {
        get => _profileListMessage;
        private set => SetProperty(ref _profileListMessage, value);
    }

    public string BusyMessage
    {
        get => _busyMessage;
        private set => SetProperty(ref _busyMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string SuccessMessage
    {
        get => _successMessage;
        private set
        {
            if (SetProperty(ref _successMessage, value))
            {
                OnPropertyChanged(nameof(HasSuccessMessage));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasSuccessMessage => !string.IsNullOrEmpty(SuccessMessage);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSelectProfile));
                RefreshCommandStates();
            }
        }
    }

    public bool CanSelectProfile => !IsBusy && _connection.CanChangeConnection;
    public bool IsConnectionChangeBlocked => !_connection.CanChangeConnection;
    public bool IsConnected => _connection.IsConnected;
    public bool RequiresReconnect => _connection.RequiresReconnect;
    public string ConnectionStatusText => _connection.RequiresReconnect
        ? "재연결 필요"
        : _connection.IsConnected ? "연결됨" : "미연결";
    public string ActiveProfileName => _connection.ActiveConnection?.ProfileName ?? "—";
    public string AccountId => _connection.ActiveConnection?.AccountId ?? "—";
    public string CallerArn => _connection.ActiveConnection?.CallerArn ?? "—";
    public string LastVerifiedAtText => _connection.ActiveConnection is { } active
        ? active.LastVerifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
        : "—";

    /// <summary>연결 화면 첫 표시 시 로컬 프로필만 읽고 STS 인증은 실행하지 않는다.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_profilesLoaded || IsBusy)
        {
            return;
        }

        try
        {
            await RefreshProfilesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ProfileListMessage = "프로필 목록을 읽지 못했습니다. 다시 읽기를 눌러 재시도하세요.";
            ShowError(exception);
        }
    }

    /// <summary>창 종료 전에 마지막 사용자 선택이 디스크에 반영될 때까지 비동기로 기다린다.</summary>
    public async Task FlushSelectedProfileAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var version = _selectionSaveVersion;
            var pendingSave = _pendingSelectionSave;
            await pendingSave.WaitAsync(cancellationToken);

            if (version != _selectionSaveVersion)
            {
                continue;
            }

            if (_selectionSaveError is not null)
            {
                throw new AppSettingsException(_selectionSaveError);
            }

            return;
        }
    }

    private async Task RefreshProfilesAsync(CancellationToken cancellationToken)
    {
        BeginBusy("로컬 프로필 목록을 읽는 중입니다.");
        try
        {
            var profiles = await _connection.ListProfilesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var previousSelection = SelectedProfile;
            _isApplyingProfileList = true;
            try
            {
                Profiles.Clear();
                foreach (var profile in profiles)
                {
                    Profiles.Add(profile);
                }

                SelectedProfile = previousSelection is not null && Profiles.Contains(previousSelection)
                    ? previousSelection
                    : _lastSelectedProfileName is not null && Profiles.Contains(_lastSelectedProfileName)
                        ? _lastSelectedProfileName
                        : Profiles.Contains(ActiveProfileName) ? ActiveProfileName : null;
            }
            finally
            {
                _isApplyingProfileList = false;
            }

            ProfileListMessage = Profiles.Count == 0
                ? "등록된 AWS 프로필이 없습니다. 로컬 AWS CLI 설정을 확인한 뒤 다시 읽어 주세요."
                : $"로컬 프로필 {Profiles.Count}개를 찾았습니다. 프로필을 선택한 뒤 연결을 누르세요.";
            _profilesLoaded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ProfileListMessage = Profiles.Count == 0
                ? "프로필 목록을 읽지 못했습니다. 다시 읽기를 눌러 재시도하세요."
                : "목록을 갱신하지 못해 이전 프로필 목록을 표시합니다.";
            throw;
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var profile = SelectedProfile;
        if (string.IsNullOrWhiteSpace(profile))
        {
            return;
        }

        var activeProfile = _connection.ActiveConnection?.ProfileName;
        if (activeProfile is not null && !string.Equals(activeProfile, profile, StringComparison.Ordinal))
        {
            var confirmed = await _confirmation.ConfirmAsync(
                "AWS 프로필 전환",
                $"현재 연결된 '{activeProfile}' 프로필을 '{profile}'(으)로 전환하시겠습니까? 새 프로필 인증에 성공하면 활성 연결이 변경됩니다.",
                "프로필 전환",
                cancellationToken);
            if (!confirmed)
            {
                return;
            }
        }

        BeginBusy($"'{profile}' 프로필을 인증하는 중입니다.");
        try
        {
            await _connection.ConnectAsync(profile, cancellationToken);
            ShowSuccess($"'{profile}' 프로필에 연결했습니다.");
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        BeginBusy("AWS 연결 상태를 확인하는 중입니다.");
        try
        {
            await _connection.VerifyAsync(cancellationToken);
            ShowSuccess("AWS 연결 상태를 확인했습니다.");
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var profile = _connection.ActiveConnection?.ProfileName;
        if (profile is null)
        {
            return;
        }

        var confirmed = await _confirmation.ConfirmAsync(
            "AWS 연결 해제",
            $"'{profile}' 프로필의 AWS 연결을 해제하시겠습니까? 연결 해제 후 AWS 조회와 제어를 사용할 수 없습니다.",
            "연결 해제",
            cancellationToken);
        if (!confirmed)
        {
            return;
        }

        BeginBusy("AWS 연결을 해제하는 중입니다.");
        try
        {
            _connection.Disconnect();
            ShowSuccess("AWS 연결을 해제했습니다.");
        }
        finally
        {
            EndBusy();
        }
    }

    private bool CanConnect()
    {
        if (IsBusy || !_connection.CanChangeConnection || string.IsNullOrWhiteSpace(SelectedProfile))
        {
            return false;
        }

        return _connection.RequiresReconnect ||
               !string.Equals(_connection.ActiveConnection?.ProfileName, SelectedProfile, StringComparison.Ordinal);
    }

    private bool CanVerify() => !IsBusy && _connection.ActiveConnection is not null && !_connection.RequiresReconnect;
    private bool CanDisconnect() => !IsBusy && _connection.ActiveConnection is not null && _connection.CanChangeConnection;

    private void BeginBusy(string message)
    {
        ErrorMessage = string.Empty;
        SuccessMessage = string.Empty;
        BusyMessage = message;
        IsBusy = true;
    }

    private void EndBusy()
    {
        IsBusy = false;
        BusyMessage = string.Empty;
    }

    private void ShowError(Exception exception)
    {
        ErrorMessage = exception is AwsConnectionException or AppSettingsException
            ? exception.Message
            : "작업을 완료하지 못했습니다. 잠시 후 다시 시도해 주세요.";
    }

    /// <summary>빠른 선택 변경도 마지막 선택값 순서대로 저장되도록 직렬화한다.</summary>
    private async Task SaveSelectedProfileAsync(int version)
    {
        await _selectionSaveGate.WaitAsync();
        try
        {
            if (version != _selectionSaveVersion)
            {
                return;
            }

            await _selectedProfileStore.SaveAsync(_lastSelectedProfileName, CancellationToken.None);
            if (version == _selectionSaveVersion)
            {
                var previousSaveError = _selectionSaveError;
                _selectionSaveError = null;
                if (previousSaveError is not null && ErrorMessage == previousSaveError)
                {
                    ErrorMessage = string.Empty;
                }
            }
        }
        catch (Exception exception)
        {
            if (version == _selectionSaveVersion)
            {
                _selectionSaveError = exception is AppSettingsException
                    ? exception.Message
                    : "마지막으로 선택한 AWS 프로필을 저장하지 못했습니다.";
                ErrorMessage = _selectionSaveError;
            }
        }
        finally
        {
            _selectionSaveGate.Release();
        }
    }

    private void ShowSuccess(string message)
    {
        SuccessMessage = message;
        var generation = ++_successMessageGeneration;
        _ = DismissSuccessMessageAsync(generation);
    }

    private async Task DismissSuccessMessageAsync(int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (generation == _successMessageGeneration)
        {
            SuccessMessage = string.Empty;
        }
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(RequiresReconnect));
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(ActiveProfileName));
        OnPropertyChanged(nameof(AccountId));
        OnPropertyChanged(nameof(CallerArn));
        OnPropertyChanged(nameof(LastVerifiedAtText));
        OnPropertyChanged(nameof(CanSelectProfile));
        OnPropertyChanged(nameof(IsConnectionChangeBlocked));
        RefreshCommandStates();
    }

    private void RefreshCommandStates()
    {
        _refreshProfilesCommand.RaiseCanExecuteChanged();
        _connectCommand.RaiseCanExecuteChanged();
        _verifyCommand.RaiseCanExecuteChanged();
        _disconnectCommand.RaiseCanExecuteChanged();
    }
}
