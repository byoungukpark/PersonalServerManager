using System.ComponentModel;
using PersonalServerManager.Application.Contracts;
using PersonalServerManager.Application.Models;

namespace PersonalServerManager.Application.Services;

/// <summary>앱 전체의 활성 AWS 연결 한 건과 수동 인증 확인을 관리한다.</summary>
public sealed class AwsConnectionService : INotifyPropertyChanged
{
    private readonly IAwsProfileProvider _profiles;
    private readonly IStsIdentityGateway _identityGateway;
    private readonly ControlActivityState _controlActivity;
    private readonly SynchronizationContext? _notificationContext;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private AwsConnectionInfo? _activeConnection;
    private bool _requiresReconnect;

    public AwsConnectionService(
        IAwsProfileProvider profiles,
        IStsIdentityGateway identityGateway,
        ControlActivityState controlActivity,
        SynchronizationContext? notificationContext = null,
        TimeProvider? timeProvider = null)
    {
        _profiles = profiles;
        _identityGateway = identityGateway;
        _controlActivity = controlActivity;
        _notificationContext = notificationContext;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _controlActivity.PropertyChanged += OnControlActivityChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AwsConnectionInfo? ActiveConnection => _activeConnection;
    public bool IsConnected => _activeConnection is not null && !_requiresReconnect;
    public bool RequiresReconnect => _requiresReconnect;
    public bool CanChangeConnection => !_controlActivity.IsControlRunning;

    public Task<IReadOnlyList<string>> ListProfilesAsync(CancellationToken cancellationToken) =>
        _profiles.ListProfilesAsync(cancellationToken);

    /// <summary>후보 프로필 인증이 성공한 경우에만 활성 연결을 바꾼다.</summary>
    public async Task ConnectAsync(string profileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new AwsConnectionException(AwsConnectionFailureKind.ProfileMissing, "연결할 AWS 프로필을 선택하세요.");
        }

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            EnsureConnectionChangeAllowed();
            var identity = await _identityGateway.GetCallerIdentityAsync(profileName, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureConnectionChangeAllowed();

            _activeConnection = new AwsConnectionInfo(
                profileName,
                identity.AccountId,
                identity.CallerArn,
                _timeProvider.GetUtcNow());
            _requiresReconnect = false;
            NotifySessionChanged();
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>현재 프로필의 인증만 다시 확인하고 인증 무효 시 재연결을 요구한다.</summary>
    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var active = _activeConnection ?? throw new AwsConnectionException(
                AwsConnectionFailureKind.ProfileMissing, "현재 연결된 AWS 프로필이 없습니다.");

            try
            {
                var identity = await _identityGateway.GetCallerIdentityAsync(active.ProfileName, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(identity.AccountId, active.AccountId, StringComparison.Ordinal))
                {
                    throw new AwsConnectionException(
                        AwsConnectionFailureKind.Authentication,
                        "현재 프로필이 이전과 다른 AWS 계정을 가리킵니다. 다시 연결해 계정을 확인하세요.");
                }

                _activeConnection = active with
                {
                    AccountId = identity.AccountId,
                    CallerArn = identity.CallerArn,
                    LastVerifiedAt = _timeProvider.GetUtcNow()
                };
                _requiresReconnect = false;
                NotifySessionChanged();
            }
            catch (AwsConnectionException exception) when (IsFatalAuthenticationFailure(exception.Kind))
            {
                _requiresReconnect = true;
                NotifySessionChanged();
                throw;
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public void Disconnect()
    {
        if (!_sessionGate.Wait(0))
        {
            throw new AwsConnectionException(AwsConnectionFailureKind.Busy, "AWS 연결 작업이 진행 중입니다. 완료 후 다시 시도하세요.");
        }

        try
        {
            EnsureConnectionChangeAllowed();
            _activeConnection = null;
            _requiresReconnect = false;
            NotifySessionChanged();
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private void EnsureConnectionChangeAllowed()
    {
        if (_controlActivity.IsControlRunning)
        {
            throw new AwsConnectionException(
                AwsConnectionFailureKind.Busy,
                "AWS 제어 작업이 진행 중이어서 프로필을 변경하거나 연결을 해제할 수 없습니다.");
        }
    }

    private static bool IsFatalAuthenticationFailure(AwsConnectionFailureKind kind) =>
        kind is AwsConnectionFailureKind.ProfileMissing
            or AwsConnectionFailureKind.Authentication
            or AwsConnectionFailureKind.ExpiredSession
            or AwsConnectionFailureKind.AccessDenied;

    private void OnControlActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ControlActivityState.IsControlRunning))
        {
            RaisePropertyChanged(nameof(CanChangeConnection));
        }
    }

    private void NotifySessionChanged()
    {
        RaisePropertyChanged(nameof(ActiveConnection));
        RaisePropertyChanged(nameof(IsConnected));
        RaisePropertyChanged(nameof(RequiresReconnect));
    }

    private void RaisePropertyChanged(string propertyName)
    {
        if (_notificationContext is null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return;
        }

        _notificationContext.Post(
            state => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs((string)state!)),
            propertyName);
    }
}
