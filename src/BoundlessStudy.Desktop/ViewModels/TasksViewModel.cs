using System.Collections.ObjectModel;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Desktop.Services;

namespace BoundlessStudy.Desktop.ViewModels;

public sealed record SubjectFilterOption(Guid? SubjectId, string DisplayName);

/// <summary>
/// ViewModel for managing study tasks (BS-STU-002, BS-STU-004, BS-STU-007).
/// Communicates through <see cref="IAgentClient"/> and enforces reward and workflow invariants.
/// </summary>
public sealed class TasksViewModel : ViewModelBase
{
    private readonly IAgentClient _agentClient;
    private readonly INavigationService? _navigation;
    private readonly List<StudyTaskDto> _allTasks = new();

    private string _title = string.Empty;
    private string? _notes;
    private Guid? _selectedSubjectId;
    private DateTime? _dueDate;
    private int? _estimatedMinutes;
    private string _priority = "Normal";
    private DateOnly? _plannedForLocalDate;
    private StudyTaskDto? _selectedTask;

    private SubjectFilterOption? _selectedSubjectFilter;
    private string _selectedStatusFilter = "All";

    private bool _isLoading;
    private string? _errorMessage;
    private string? _statusMessage;
    private string? _titleValidationError;

    public TasksViewModel(IAgentClient agentClient, INavigationService? navigation = null)
    {
        _agentClient = agentClient ?? throw new ArgumentNullException(nameof(agentClient));
        _navigation = navigation;

        Tasks = new ObservableCollection<StudyTaskDto>();
        AvailableSubjects = new ObservableCollection<SubjectDto>();
        SubjectFilters = new ObservableCollection<SubjectFilterOption>();
        StatusFilters = new ObservableCollection<string> { "All", "Todo", "InProgress", "Done", "Archived" };
        AvailablePriorities = new ObservableCollection<string> { "Low", "Normal", "High" };

        SubjectFilters.Add(new SubjectFilterOption(null, "All Subjects"));
        _selectedSubjectFilter = SubjectFilters[0];

        LoadCommand = new AsyncRelayCommand(LoadTasksAsync, () => !IsLoading);
        CreateCommand = new AsyncRelayCommand(CreateTaskAsync, () => CanCreate);
        UpdateCommand = new AsyncRelayCommand(UpdateTaskAsync, () => CanUpdate);
        CompleteCommand = new AsyncRelayCommand(CompleteTaskAsync, p => CanComplete(p));
        ReopenCommand = new AsyncRelayCommand(ReopenTaskAsync, p => CanReopen(p));
        StartCommand = new AsyncRelayCommand(StartTaskAsync, p => CanStart(p));
        ArchiveCommand = new AsyncRelayCommand(ArchiveTaskAsync, p => CanArchive(p));
        RestoreCommand = new AsyncRelayCommand(RestoreTaskAsync, p => CanRestore(p));
        DeleteCommand = new AsyncRelayCommand(DeleteTaskAsync, p => CanDelete(p));
        ClearFormCommand = new RelayCommand(ClearForm);

        if (_navigation is not null)
        {
            BackCommand = new RelayCommand(() => _navigation.NavigateToHome());
        }
    }

    public TasksViewModel(IFocusClient? focusClient, INavigationService? navigation = null)
        : this(focusClient is NamedPipeFocusClient npfc ? npfc.AgentClient : new InMemoryAgentClient(), navigation)
    {
    }

    public ObservableCollection<StudyTaskDto> Tasks { get; }
    public ObservableCollection<SubjectDto> AvailableSubjects { get; }
    public ObservableCollection<SubjectFilterOption> SubjectFilters { get; }
    public ObservableCollection<string> StatusFilters { get; }
    public ObservableCollection<string> AvailablePriorities { get; }

    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand CreateCommand { get; }
    public AsyncRelayCommand UpdateCommand { get; }
    public AsyncRelayCommand CompleteCommand { get; }
    public AsyncRelayCommand ReopenCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand ArchiveCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand ClearFormCommand { get; }
    public RelayCommand? BackCommand { get; }

    public SubjectFilterOption? SelectedSubjectFilter
    {
        get => _selectedSubjectFilter;
        set
        {
            if (SetProperty(ref _selectedSubjectFilter, value))
            {
                ApplyFilter();
            }
        }
    }

    public string SelectedStatusFilter
    {
        get => _selectedStatusFilter;
        set
        {
            if (SetProperty(ref _selectedStatusFilter, value))
            {
                ApplyFilter();
            }
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                ValidateTitle();
                UpdateCommandStates();
            }
        }
    }

    public string? Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    public Guid? SelectedSubjectId
    {
        get => _selectedSubjectId;
        set => SetProperty(ref _selectedSubjectId, value);
    }

    public DateTime? DueDate
    {
        get => _dueDate;
        set => SetProperty(ref _dueDate, value);
    }

    public int? EstimatedMinutes
    {
        get => _estimatedMinutes;
        set => SetProperty(ref _estimatedMinutes, value);
    }

    public string Priority
    {
        get => _priority;
        set => SetProperty(ref _priority, value);
    }

    public DateOnly? PlannedForLocalDate
    {
        get => _plannedForLocalDate;
        set => SetProperty(ref _plannedForLocalDate, value);
    }

    public StudyTaskDto? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                if (_selectedTask is not null)
                {
                    _title = _selectedTask.Title;
                    _notes = _selectedTask.Notes;
                    _selectedSubjectId = _selectedTask.SubjectId;
                    _dueDate = _selectedTask.DueDateLocal?.ToDateTime(TimeOnly.MinValue);
                    _estimatedMinutes = _selectedTask.EstimatedMinutes;
                    _priority = _selectedTask.Priority;
                    _plannedForLocalDate = _selectedTask.PlannedForLocalDate;

                    OnPropertyChanged(nameof(Title));
                    OnPropertyChanged(nameof(Notes));
                    OnPropertyChanged(nameof(SelectedSubjectId));
                    OnPropertyChanged(nameof(DueDate));
                    OnPropertyChanged(nameof(EstimatedMinutes));
                    OnPropertyChanged(nameof(Priority));
                    OnPropertyChanged(nameof(PlannedForLocalDate));
                    ValidateTitle();
                }

                OnPropertyChanged(nameof(HasSelectedTask));
                UpdateCommandStates();
            }
        }
    }

    public bool HasSelectedTask => _selectedTask is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public string? ErrorMessage
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

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    public string? TitleValidationError
    {
        get => _titleValidationError;
        private set
        {
            if (SetProperty(ref _titleValidationError, value))
            {
                OnPropertyChanged(nameof(HasTitleValidationError));
            }
        }
    }

    public bool HasTitleValidationError => !string.IsNullOrEmpty(TitleValidationError);

    public bool CanCreate =>
        !IsLoading &&
        !string.IsNullOrWhiteSpace(Title) &&
        Title.Trim().Length <= 200;

    public bool CanUpdate =>
        !IsLoading &&
        SelectedTask is not null &&
        !string.IsNullOrWhiteSpace(Title) &&
        Title.Trim().Length <= 200;

    public bool CanComplete(object? parameter = null)
    {
        if (IsLoading) return false;
        var target = ResolveTask(parameter);
        return target is not null && !target.IsDeleted && target.Status != "Done";
    }

    public bool CanReopen(object? parameter = null)
    {
        if (IsLoading) return false;
        var target = ResolveTask(parameter);
        return target is not null && !target.IsDeleted && target.Status == "Done";
    }

    public bool CanStart(object? parameter = null)
    {
        if (IsLoading) return false;
        var target = ResolveTask(parameter);
        return target is not null && !target.IsDeleted && target.Status == "Todo";
    }

    public bool CanArchive(object? parameter = null)
    {
        if (IsLoading) return false;
        var target = ResolveTask(parameter);
        return target is not null && !target.IsDeleted && !target.IsArchived;
    }

    public bool CanRestore(object? parameter = null)
    {
        if (IsLoading) return false;
        var target = ResolveTask(parameter);
        return target is not null && !target.IsDeleted && target.IsArchived;
    }

    public bool CanDelete(object? parameter = null)
    {
        if (IsLoading) return false;
        var target = ResolveTask(parameter);
        return target is not null && !target.IsDeleted;
    }

    public async Task LoadTasksAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var subjects = await _agentClient.GetSubjectsAsync(includeArchived: false);
            AvailableSubjects.Clear();
            foreach (var subject in subjects)
            {
                AvailableSubjects.Add(subject);
            }

            var currentFilterSubjectId = SelectedSubjectFilter?.SubjectId;
            SubjectFilters.Clear();
            SubjectFilters.Add(new SubjectFilterOption(null, "All Subjects"));
            foreach (var subject in subjects)
            {
                SubjectFilters.Add(new SubjectFilterOption(subject.Id, subject.Name));
            }

            SelectedSubjectFilter = SubjectFilters.FirstOrDefault(f => f.SubjectId == currentFilterSubjectId)
                ?? SubjectFilters[0];

            var tasks = await _agentClient.GetTasksAsync();
            _allTasks.Clear();
            _allTasks.AddRange(tasks);

            ApplyFilter();
            ValidateTitle();
            StatusMessage = $"Loaded {_allTasks.Count} tasks.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load tasks: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CreateTaskAsync()
    {
        if (!CanCreate) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            DateOnly? dueDateLocal = DueDate.HasValue
                ? DateOnly.FromDateTime(DueDate.Value)
                : null;

            var request = new CreateTaskRequest(
                Title.Trim(),
                Notes?.Trim(),
                SelectedSubjectId,
                dueDateLocal,
                EstimatedMinutes,
                Priority,
                PlannedForLocalDate);

            var created = await _agentClient.CreateTaskAsync(request);
            _allTasks.Insert(0, created);
            ApplyFilter();

            StatusMessage = $"Task '{created.Title}' created successfully.";
            ClearForm();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to create task: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task UpdateTaskAsync()
    {
        if (!CanUpdate || SelectedTask is null) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            DateOnly? dueDateLocal = DueDate.HasValue
                ? DateOnly.FromDateTime(DueDate.Value)
                : null;

            var request = new UpdateTaskRequest(
                SelectedTask.Id,
                Title.Trim(),
                Notes?.Trim(),
                SelectedSubjectId,
                dueDateLocal,
                EstimatedMinutes,
                Priority,
                PlannedForLocalDate);

            var updated = await _agentClient.UpdateTaskAsync(request);

            var index = _allTasks.FindIndex(t => t.Id == updated.Id);
            if (index >= 0)
            {
                _allTasks[index] = updated;
            }

            ApplyFilter();
            SelectedTask = updated;
            StatusMessage = $"Task '{updated.Title}' updated successfully.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to update task: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CompleteTaskAsync(object? parameter)
    {
        var target = ResolveTask(parameter);
        if (target is null) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var request = new CompleteTaskRequest(target.Id);
            var updated = await _agentClient.CompleteTaskAsync(request);

            var index = _allTasks.FindIndex(t => t.Id == updated.Id);
            if (index >= 0)
            {
                _allTasks[index] = updated;
            }

            ApplyFilter();
            if (SelectedTask?.Id == target.Id)
            {
                SelectedTask = updated;
            }

            // Reward invariant: Completion awards XP idempotently (§8.5, TV-09).
            StatusMessage = $"Task '{target.Title}' completed. XP awarded idempotently.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to complete task: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ReopenTaskAsync(object? parameter)
    {
        var target = ResolveTask(parameter);
        if (target is null) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var request = new ReopenTaskRequest(target.Id);
            var updated = await _agentClient.ReopenTaskAsync(request);

            var index = _allTasks.FindIndex(t => t.Id == updated.Id);
            if (index >= 0)
            {
                _allTasks[index] = updated;
            }

            ApplyFilter();
            if (SelectedTask?.Id == target.Id)
            {
                SelectedTask = updated;
            }

            // Reward invariant: Reopening preserves original completion timestamp and reward (§8.5, TV-18).
            StatusMessage = $"Task '{target.Title}' reopened. Original completion and reward preserved.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to reopen task: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task StartTaskAsync(object? parameter)
    {
        var target = ResolveTask(parameter);
        if (target is null) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var request = new StartTaskRequest(target.Id);
            var updated = await _agentClient.StartTaskAsync(request);

            var index = _allTasks.FindIndex(t => t.Id == updated.Id);
            if (index >= 0)
            {
                _allTasks[index] = updated;
            }

            ApplyFilter();
            if (SelectedTask?.Id == target.Id)
            {
                SelectedTask = updated;
            }

            StatusMessage = $"Task '{target.Title}' started (In Progress).";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to start task: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ArchiveTaskAsync(object? parameter)
    {
        var target = ResolveTask(parameter);
        if (target is null) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var request = new ArchiveTaskRequest(target.Id, Restore: false);
            var updated = await _agentClient.ArchiveTaskAsync(request);

            var index = _allTasks.FindIndex(t => t.Id == updated.Id);
            if (index >= 0)
            {
                _allTasks[index] = updated;
            }

            ApplyFilter();
            if (SelectedTask?.Id == target.Id)
            {
                SelectedTask = updated;
            }

            StatusMessage = $"Task '{target.Title}' archived.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to archive task: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RestoreTaskAsync(object? parameter)
    {
        var target = ResolveTask(parameter);
        if (target is null) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var request = new ArchiveTaskRequest(target.Id, Restore: true);
            var updated = await _agentClient.ArchiveTaskAsync(request);

            var index = _allTasks.FindIndex(t => t.Id == updated.Id);
            if (index >= 0)
            {
                _allTasks[index] = updated;
            }

            ApplyFilter();
            if (SelectedTask?.Id == target.Id)
            {
                SelectedTask = updated;
            }

            StatusMessage = $"Task '{target.Title}' restored.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to restore task: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task DeleteTaskAsync(object? parameter)
    {
        var target = ResolveTask(parameter);
        if (target is null) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var request = new SoftDeleteTaskRequest(target.Id);
            var updated = await _agentClient.DeleteTaskAsync(request);

            var index = _allTasks.FindIndex(t => t.Id == updated.Id);
            if (index >= 0)
            {
                _allTasks[index] = updated;
            }

            ApplyFilter();
            if (SelectedTask?.Id == target.Id)
            {
                ClearForm();
            }

            // Reward invariant: Soft delete preserves history contribution for ACH-002 & ACH-005 (§9.2).
            StatusMessage = $"Task '{target.Title}' deleted. Historical contribution preserved.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to delete task: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ClearForm()
    {
        _selectedTask = null;
        _title = string.Empty;
        _notes = null;
        _selectedSubjectId = null;
        _dueDate = null;
        _estimatedMinutes = null;
        _priority = "Normal";
        _plannedForLocalDate = null;
        _titleValidationError = null;

        OnPropertyChanged(nameof(SelectedTask));
        OnPropertyChanged(nameof(HasSelectedTask));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(SelectedSubjectId));
        OnPropertyChanged(nameof(DueDate));
        OnPropertyChanged(nameof(EstimatedMinutes));
        OnPropertyChanged(nameof(Priority));
        OnPropertyChanged(nameof(PlannedForLocalDate));
        OnPropertyChanged(nameof(TitleValidationError));
        OnPropertyChanged(nameof(HasTitleValidationError));

        UpdateCommandStates();
    }

    private void ApplyFilter()
    {
        Tasks.Clear();

        var filtered = _allTasks.Where(t => !t.IsDeleted);

        if (SelectedSubjectFilter?.SubjectId is { } filterSubjectId)
        {
            filtered = filtered.Where(t => t.SubjectId == filterSubjectId);
        }

        if (!string.Equals(SelectedStatusFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(SelectedStatusFilter, "Archived", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(t => t.IsArchived || string.Equals(t.Status, "Archived", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                filtered = filtered.Where(t => !t.IsArchived && string.Equals(t.Status, SelectedStatusFilter, StringComparison.OrdinalIgnoreCase));
            }
        }

        foreach (var task in filtered)
        {
            Tasks.Add(task);
        }

        UpdateCommandStates();
    }

    private void ValidateTitle()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            TitleValidationError = "Task title is required.";
            return;
        }

        var trimmed = Title.Trim();
        if (trimmed.Length > 200)
        {
            TitleValidationError = "Task title cannot exceed 200 characters.";
        }
        else
        {
            TitleValidationError = null;
        }
    }

    private StudyTaskDto? ResolveTask(object? parameter) =>
        parameter as StudyTaskDto ?? SelectedTask;

    private void UpdateCommandStates()
    {
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CanUpdate));
        LoadCommand.RaiseCanExecuteChanged();
        CreateCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
        CompleteCommand.RaiseCanExecuteChanged();
        ReopenCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        ArchiveCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }
}
