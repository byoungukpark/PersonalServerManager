using System.Windows.Input;
using PersonalServerManager.Commands;
using PersonalServerManager.Services.Persistence;

namespace PersonalServerManager.ViewModels;

public sealed record HistoryStatusChoice(string Label, string Value);

/// <summary>로컬 작업/단계 기록 검색과 선택 상세를 제공한다.</summary>
public sealed class HistoryViewModel : ViewModelBase
{
    private readonly IControlHistoryStore _store;
    private IReadOnlyList<ControlJobRecord> _jobs = [];
    private IReadOnlyList<ControlStepRecord> _steps = [];
    private ControlJobRecord? _selectedJob;
    private string _keyword = "";
    private string _status = "";
    private string _message = "최신 기록 200건까지 표시합니다.";
    private bool _isLoading;
    private int _selectionVersion;

    public HistoryViewModel(IControlHistoryStore store)
    {
        _store = store;
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsLoading, exception => Message = exception.Message);
    }

    public IReadOnlyList<HistoryStatusChoice> StatusChoices { get; } =
        [new("전체", ""), new("진행 중", "진행 중"), new("완료", "완료"),
         new("실패", "실패"), new("시간 초과", "시간 초과"),
         new("사용자 취소", "사용자 취소"), new("완료 미확인", "완료 미확인"),
         new("요청 접수", "요청 접수"), new("건너뜀", "건너뜀")];
    public string Keyword { get => _keyword; set => SetProperty(ref _keyword, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public IReadOnlyList<ControlJobRecord> Jobs { get => _jobs; private set => SetProperty(ref _jobs, value); }
    public IReadOnlyList<ControlStepRecord> Steps { get => _steps; private set => SetProperty(ref _steps, value); }
    public ControlJobRecord? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (!SetProperty(ref _selectedJob, value)) return;
            _ = LoadStepsAsync(value, ++_selectionVersion);
        }
    }
    public ICommand SearchCommand { get; }

    public async Task SearchAsync(CancellationToken token = default)
    {
        IsLoading = true;
        try
        {
            if (!_store.IsAvailable) { Message = _store.FailureMessage; Jobs = []; Steps = []; return; }
            var selectedId = SelectedJob?.Id;
            Jobs = await _store.SearchAsync(Keyword.Trim(), Status, token);
            var selected = Jobs.FirstOrDefault(job => job.Id == selectedId);
            if (Equals(SelectedJob, selected))
                await LoadStepsAsync(selected, ++_selectionVersion);
            else SelectedJob = selected;
            Message = Jobs.Count == 0 ? "조건에 맞는 작업 기록이 없습니다." : $"작업 {Jobs.Count}건을 표시합니다.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { Message = exception.Message; Jobs = []; Steps = []; }
        finally { IsLoading = false; }
    }

    private async Task LoadStepsAsync(ControlJobRecord? job, int version)
    {
        Steps = [];
        if (job is null) return;
        try
        {
            var steps = await _store.StepsAsync(job.Id, CancellationToken.None);
            if (version == _selectionVersion) Steps = steps;
        }
        catch (Exception exception)
        { if (version == _selectionVersion) Message = exception.Message; }
    }
}
