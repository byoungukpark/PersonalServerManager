using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using PersonalServerManager.Commands;
using PersonalServerManager.Models;
using PersonalServerManager.Services.Groups;

namespace PersonalServerManager.ViewModels;

public sealed class HealthEndpointDraft : ViewModelBase
{
    private string _name;
    private string _url;
    private HealthCheckRule _rule;
    public HealthEndpointDraft(HealthEndpoint endpoint)
    { Id = endpoint.Id; Ec2Id = endpoint.Ec2Id; _name = endpoint.Name; _url = endpoint.Url; _rule = endpoint.Rule; }
    public Guid Id { get; }
    public string Ec2Id { get; }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Url { get => _url; set => SetProperty(ref _url, value); }
    public HealthCheckRule Rule { get => _rule; set => SetProperty(ref _rule, value); }
    public HealthEndpoint ToEndpoint() => new(Id, Ec2Id, Name?.Trim() ?? "", Url?.Trim() ?? "", Rule);
}

public sealed partial class GroupManagementViewModel
{
    private ResourceChoice? _healthEc2;
    private string _newHealthName = "";
    private string _newHealthUrl = "";
    private HealthCheckRule _newHealthRule;
    public ObservableCollection<HealthEndpointDraft> HealthApps { get; } = [];
    public IReadOnlyList<HealthCheckRule> HealthRules { get; } = Enum.GetValues<HealthCheckRule>();
    public ICommand AddHealthCommand { get; private set; } = null!;
    public ICommand RemoveHealthCommand { get; private set; } = null!;
    public ResourceChoice? HealthEc2 { get => _healthEc2; set => SetProperty(ref _healthEc2, value); }
    public string NewHealthName { get => _newHealthName; set => SetProperty(ref _newHealthName, value); }
    public string NewHealthUrl { get => _newHealthUrl; set => SetProperty(ref _newHealthUrl, value); }
    public HealthCheckRule NewHealthRule { get => _newHealthRule; set => SetProperty(ref _newHealthRule, value); }

    private void InitializeHealth()
    {
        AddHealthCommand = new RelayCommand(_ => AddHealth());
        RemoveHealthCommand = new RelayCommand(item =>
        {
            if (item is not HealthEndpointDraft draft) return;
            draft.PropertyChanged -= OnHealthChanged;
            HealthApps.Remove(draft);
            NotifyChanges();
        });
    }

    private void ClearHealth()
    {
        foreach (var draft in HealthApps) draft.PropertyChanged -= OnHealthChanged;
        HealthApps.Clear();
        HealthEc2 = null;
        NewHealthName = NewHealthUrl = "";
    }

    private void LoadHealth(ServiceGroup group)
    {
        foreach (var endpoint in group.HealthEndpoints ?? [])
        {
            var draft = new HealthEndpointDraft(endpoint);
            draft.PropertyChanged += OnHealthChanged;
            HealthApps.Add(draft);
        }
        HealthEc2 = Ec2Choices.FirstOrDefault(item => item.IsSelected);
        NotifyChanges();
    }

    private void AddHealth()
    {
        if (HealthEc2 is not { IsSelected: true } ec2)
        { Message = "먼저 그룹에 연결한 EC2를 선택하세요."; return; }
        var endpoint = new HealthEndpoint(Guid.NewGuid(), ec2.Id, NewHealthName.Trim(), NewHealthUrl.Trim(), NewHealthRule);
        if (!ValidateHealthEndpoint(endpoint)) return;
        var draft = new HealthEndpointDraft(endpoint);
        draft.PropertyChanged += OnHealthChanged;
        HealthApps.Add(draft);
        NewHealthName = NewHealthUrl = "";
        Message = "Health 앱을 초안에 추가했습니다. 변경사항 저장을 눌러 확정하세요.";
        NotifyChanges();
    }

    private bool ValidateHealthEndpoint(HealthEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Name) || endpoint.Name.Length > 80 ||
            !Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        { Message = "앱 이름(1~80자)과 인증 없는 HTTP/HTTPS URL을 확인하세요."; return false; }
        return true;
    }

    private IReadOnlyList<HealthEndpoint> BuildHealthEndpoints(IReadOnlyList<GroupResource> resources)
    {
        var endpoints = HealthApps.Select(item => item.ToEndpoint()).ToArray();
        foreach (var endpoint in endpoints)
        {
            if (!ValidateHealthEndpoint(endpoint)) throw new ServiceGroupException(Message);
            if (!resources.Any(resource => resource.Kind == AwsResourceKind.Ec2 && resource.Id == endpoint.Ec2Id))
                throw new ServiceGroupException("Health 앱에 연결된 EC2가 그룹에서 해제되었습니다. 앱 설정도 제거하세요.");
        }
        return endpoints;
    }

    private bool HealthChanged(ServiceGroup original) =>
        !HealthApps.Select(item => item.ToEndpoint()).SequenceEqual(original.HealthEndpoints ?? []);

    private void OnHealthChanged(object? sender, PropertyChangedEventArgs args) => NotifyChanges();
}
