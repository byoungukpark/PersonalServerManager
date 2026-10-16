using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using PersonalServerManager.Application.Contracts;
using PersonalServerManager.Application.Models;
using PersonalServerManager.Application.Services;
using PersonalServerManager.Presentation.Commands;

namespace PersonalServerManager.Presentation.ViewModels;

public sealed record AwsRegionOption(string DisplayName, string RegionCode);

/// <summary>그룹 생성 전 리소스 발견 기능을 서비스별로 시험할 수 있게 표시한다.</summary>
public sealed class ResourceDiscoveryViewModel : ViewModelBase
{
    private readonly AwsConnectionService _connection;
    private readonly AwsResourceQueryService _queries;
    private readonly AsyncRelayCommand _queryEc2Command;
    private readonly AsyncRelayCommand _queryRdsCommand;
    private readonly AsyncRelayCommand _queryS3Command;
    private AwsRegionOption? _selectedRegion;
    private string _ec2Status = "아직 조회하지 않았습니다.";
    private string _rdsStatus = "아직 조회하지 않았습니다.";
    private string _s3Status = "아직 조회하지 않았습니다.";
    private string _connectionMessage = string.Empty;
    private bool _isEc2Loading;
    private bool _isRdsLoading;
    private bool _isS3Loading;
    private bool _hasEc2Result;
    private bool _hasRdsResult;
    private bool _hasS3Result;
    private IAwsProfileSession? _lastSession;

    public ResourceDiscoveryViewModel(AwsConnectionService connection, AwsResourceQueryService queries)
    {
        _connection = connection;
        _queries = queries;
        Ec2Instances = new ObservableCollection<Ec2InstanceResource>();
        RdsInstances = new ObservableCollection<RdsInstanceResource>();
        S3Buckets = new ObservableCollection<S3BucketResource>();
        Regions = [
            new AwsRegionOption("서울 (ap-northeast-2)", "ap-northeast-2"),
            new AwsRegionOption("도쿄 (ap-northeast-1)", "ap-northeast-1"),
            new AwsRegionOption("싱가포르 (ap-southeast-1)", "ap-southeast-1"),
            new AwsRegionOption("버지니아 북부 (us-east-1)", "us-east-1"),
            new AwsRegionOption("오하이오 (us-east-2)", "us-east-2"),
            new AwsRegionOption("오리건 (us-west-2)", "us-west-2")
        ];
        SelectedRegion = Regions[0];
        _queryEc2Command = new AsyncRelayCommand(QueryEc2Async, () => CanQuery && !IsEc2Loading, HandleError);
        _queryRdsCommand = new AsyncRelayCommand(QueryRdsAsync, () => CanQuery && !IsRdsLoading, HandleError);
        _queryS3Command = new AsyncRelayCommand(QueryS3Async, () => CanQuery && !IsS3Loading, HandleError);
        _connection.PropertyChanged += OnConnectionPropertyChanged;
        RefreshConnectionState();
    }

    public ObservableCollection<Ec2InstanceResource> Ec2Instances { get; }
    public ObservableCollection<RdsInstanceResource> RdsInstances { get; }
    public ObservableCollection<S3BucketResource> S3Buckets { get; }
    public IReadOnlyList<AwsRegionOption> Regions { get; }
    public ICommand QueryEc2Command => _queryEc2Command;
    public ICommand QueryRdsCommand => _queryRdsCommand;
    public ICommand QueryS3Command => _queryS3Command;

    public AwsRegionOption? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (SetProperty(ref _selectedRegion, value))
            {
                Ec2Instances.Clear();
                RdsInstances.Clear();
                _hasEc2Result = false;
                _hasRdsResult = false;
                Ec2Status = "아직 조회하지 않았습니다.";
                RdsStatus = "아직 조회하지 않았습니다.";
            }
        }
    }

    public string Ec2Status
    {
        get => _ec2Status;
        private set => SetProperty(ref _ec2Status, value);
    }

    public string RdsStatus
    {
        get => _rdsStatus;
        private set => SetProperty(ref _rdsStatus, value);
    }

    public string S3Status
    {
        get => _s3Status;
        private set => SetProperty(ref _s3Status, value);
    }

    public string ConnectionMessage
    {
        get => _connectionMessage;
        private set
        {
            if (SetProperty(ref _connectionMessage, value))
            {
                OnPropertyChanged(nameof(HasConnectionMessage));
            }
        }
    }

    public bool HasConnectionMessage => !string.IsNullOrWhiteSpace(ConnectionMessage);

    public bool IsEc2Loading
    {
        get => _isEc2Loading;
        private set
        {
            if (SetProperty(ref _isEc2Loading, value))
            {
                _queryEc2Command.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanSelectRegion));
            }
        }
    }

    public bool IsRdsLoading
    {
        get => _isRdsLoading;
        private set
        {
            if (SetProperty(ref _isRdsLoading, value))
            {
                _queryRdsCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanSelectRegion));
            }
        }
    }

    public bool IsS3Loading
    {
        get => _isS3Loading;
        private set
        {
            if (SetProperty(ref _isS3Loading, value))
            {
                _queryS3Command.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanQuery => _connection.IsConnected;
    public bool CanSelectRegion => !IsEc2Loading && !IsRdsLoading;

    private async Task QueryEc2Async(CancellationToken cancellationToken)
    {
        if (!TryGetRegion(out var region))
        {
            return;
        }
        IsEc2Loading = true;
        ConnectionMessage = string.Empty;
        Ec2Status = "조회 중…";
        try
        {
            var items = await _queries.ListEc2Async(region, cancellationToken);
            ReplaceItems(Ec2Instances, items);
            _hasEc2Result = true;
            Ec2Status = items.Count == 0 ? "조회 완료 · 리소스 없음" : $"조회 완료 · {items.Count}개";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Ec2Status = "조회 취소됨";
        }
        catch (Exception exception)
        {
            Ec2Status = FailureStatus(_hasEc2Result, exception);
            throw;
        }
        finally
        {
            IsEc2Loading = false;
        }
    }

    private async Task QueryRdsAsync(CancellationToken cancellationToken)
    {
        if (!TryGetRegion(out var region))
        {
            return;
        }
        IsRdsLoading = true;
        ConnectionMessage = string.Empty;
        RdsStatus = "조회 중…";
        try
        {
            var items = await _queries.ListRdsAsync(region, cancellationToken);
            ReplaceItems(RdsInstances, items);
            _hasRdsResult = true;
            RdsStatus = items.Count == 0 ? "조회 완료 · 리소스 없음" : $"조회 완료 · {items.Count}개";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RdsStatus = "조회 취소됨";
        }
        catch (Exception exception)
        {
            RdsStatus = FailureStatus(_hasRdsResult, exception);
            throw;
        }
        finally
        {
            IsRdsLoading = false;
        }
    }

    private async Task QueryS3Async(CancellationToken cancellationToken)
    {
        IsS3Loading = true;
        ConnectionMessage = string.Empty;
        S3Status = "조회 중…";
        try
        {
            var items = await _queries.ListS3Async(cancellationToken);
            ReplaceItems(S3Buckets, items);
            _hasS3Result = true;
            S3Status = items.Count == 0 ? "조회 완료 · 버킷 없음" : $"조회 완료 · {items.Count}개";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            S3Status = "조회 취소됨";
        }
        catch (Exception exception)
        {
            S3Status = FailureStatus(_hasS3Result, exception);
            throw;
        }
        finally
        {
            IsS3Loading = false;
        }
    }

    private bool TryGetRegion(out string region)
    {
        region = SelectedRegion?.RegionCode ?? string.Empty;
        if (string.IsNullOrWhiteSpace(region))
        {
            ConnectionMessage = "조회할 리전을 선택하세요.";
            return false;
        }

        ConnectionMessage = string.Empty;
        return true;
    }

    private void HandleError(Exception exception)
    {
        if (exception is AwsResourceQueryException { Kind: AwsQueryFailureKind.Authentication or AwsQueryFailureKind.ExpiredSession })
        {
            _connection.MarkReconnectRequired();
        }

        ConnectionMessage = GetUserMessage(exception);
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AwsConnectionService.ActiveConnection)
            or nameof(AwsConnectionService.IsConnected)
            or nameof(AwsConnectionService.RequiresReconnect))
        {
            RefreshConnectionState();
        }
    }

    private void RefreshConnectionState()
    {
        var currentSession = _connection.ActiveSession;
        if (!ReferenceEquals(_lastSession, currentSession))
        {
            // 다른 계정에 연결되면 이전 계정의 목록을 화면에서 제거한다.
            _lastSession = currentSession;
            Ec2Instances.Clear();
            RdsInstances.Clear();
            S3Buckets.Clear();
            _hasEc2Result = false;
            _hasRdsResult = false;
            _hasS3Result = false;
            Ec2Status = "아직 조회하지 않았습니다.";
            RdsStatus = "아직 조회하지 않았습니다.";
            S3Status = "아직 조회하지 않았습니다.";
        }

        OnPropertyChanged(nameof(CanQuery));
        _queryEc2Command.RaiseCanExecuteChanged();
        _queryRdsCommand.RaiseCanExecuteChanged();
        _queryS3Command.RaiseCanExecuteChanged();
        ConnectionMessage = _connection.IsConnected
            ? string.Empty
            : "리소스를 조회하려면 AWS 연결 관리에서 먼저 프로필을 연결하세요.";
    }

    private static string FailureStatus(bool hasPreviousResult, Exception exception)
    {
        var message = GetUserMessage(exception);
        return hasPreviousResult
            ? $"이전 목록 유지 · 조회 실패: {message}"
            : $"조회 실패: {message}";
    }

    private static string GetUserMessage(Exception exception)
    {
        return exception is AwsResourceQueryException or AwsConnectionException
            ? exception.Message
            : "AWS 리소스 목록을 불러오지 못했습니다. 잠시 후 다시 시도하세요.";
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
