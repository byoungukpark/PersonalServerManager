using System.Collections.ObjectModel;
using System.Windows.Input;
using PersonalServerManager.Commands;
using PersonalServerManager.Models;
using PersonalServerManager.Services.Groups;

namespace PersonalServerManager.ViewModels;

public sealed class HealthAppViewModel : ViewModelBase
{
    private HealthCheckResult? _latest;
    private IReadOnlyList<HealthCheckResult> _history = [];
    private bool _isChecking;
    public HealthAppViewModel(HealthEndpoint endpoint) { Endpoint = endpoint; }
    public HealthEndpoint Endpoint { get; }
    public string Name => Endpoint.Name;
    public string Ec2Id => Endpoint.Ec2Id;
    public string Url => Endpoint.Url;
    public string Rule => Endpoint.Rule.ToString();
    public HealthCheckResult? Latest { get => _latest; private set { if (SetProperty(ref _latest, value)) OnPropertyChanged(nameof(StatusText)); } }
    public IReadOnlyList<HealthCheckResult> History { get => _history; private set => SetProperty(ref _history, value); }
    public bool IsChecking { get => _isChecking; set => SetProperty(ref _isChecking, value); }
    public string StatusText => Latest is null ? "○ 확인 전" :
        $"{Latest.Display} · {Latest.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Latest.Message}";
    public void Update(HealthCheckService service)
    {
        History = service.History(Endpoint.Id);
        Latest = History.FirstOrDefault();
    }
}

/// <summary>선택 EC2의 앱 상태와 최근 10회 기록을 보여주며 수동 점검을 실행한다.</summary>
public sealed class GroupHealthViewModel : ViewModelBase
{
    private readonly HealthCheckService _service;
    private readonly Func<int> _timeoutSeconds;
    private string? _selectedEc2Id;
    private string _message = "앱 Health는 EC2 상태와 독립적으로 확인합니다.";
    public GroupHealthViewModel(HealthCheckService service, Func<int> timeoutSeconds)
    {
        _service = service;
        _timeoutSeconds = timeoutSeconds;
        _service.ResultChanged += OnResultChanged;
        CheckAllCommand = new AsyncRelayCommand(CheckAllAsync, () => true, ex => Message = ex.Message);
        CheckOneCommand = new AsyncRelayCommand(CheckOneAsync, () => true, ex => Message = ex.Message);
    }
    public event Action? ResultsChanged;
    public ObservableCollection<HealthAppViewModel> Apps { get; } = [];
    public IReadOnlyList<HealthAppViewModel> VisibleApps => Apps.Where(app =>
        _selectedEc2Id is null || app.Ec2Id == _selectedEc2Id).ToArray();
    public HealthAppViewModel? SelectedApp { get; set; }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public ICommand CheckAllCommand { get; }
    public ICommand CheckOneCommand { get; }

    public void SetGroup(ServiceGroup? group)
    {
        Apps.Clear();
        foreach (var endpoint in group?.HealthEndpoints ?? [])
        {
            _service.EnsureDefinition(endpoint);
            var app = new HealthAppViewModel(endpoint);
            app.Update(_service);
            Apps.Add(app);
        }
        SelectedApp = null;
        SetSelectedEc2(null);
        OnPropertyChanged(nameof(VisibleApps));
    }

    public void SetSelectedEc2(string? ec2Id)
    {
        _selectedEc2Id = ec2Id;
        OnPropertyChanged(nameof(VisibleApps));
    }

    public string Summary(string ec2Id)
    {
        var apps = Apps.Where(app => app.Ec2Id == ec2Id).ToArray();
        if (apps.Length == 0) return "앱 미등록";
        if (apps.Any(app => app.Latest?.State == HealthCheckState.Unhealthy)) return "앱 비정상";
        if (apps.Any(app => app.Latest?.State == HealthCheckState.ConnectionFailed)) return "앱 연결 실패";
        if (apps.Any(app => app.Latest is null || app.Latest.State == HealthCheckState.Unknown)) return "앱 확인 불가";
        return "앱 정상";
    }

    public async Task CheckAllAsync(CancellationToken token = default)
    {
        await Task.WhenAll(Apps.Select(app => CheckAsync(app, token)));
        Message = "등록 앱 점검을 마쳤습니다.";
    }

    private async Task CheckOneAsync(CancellationToken token)
    {
        if (SelectedApp is { } app) await CheckAsync(app, token);
        else Message = "점검할 앱을 선택하세요.";
    }

    private async Task CheckAsync(HealthAppViewModel app, CancellationToken token)
    {
        app.IsChecking = true;
        try { await _service.CheckAsync(app.Endpoint, _timeoutSeconds(), token); }
        finally { app.IsChecking = false; }
    }

    private void OnResultChanged(HealthCheckResult result)
    {
        var app = Apps.FirstOrDefault(item => item.Endpoint.Id == result.EndpointId);
        if (app is null) return;
        app.Update(_service);
        ResultsChanged?.Invoke();
    }
}
