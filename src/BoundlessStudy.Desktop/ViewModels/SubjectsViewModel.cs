using System.Collections.ObjectModel;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Desktop.Services;

namespace BoundlessStudy.Desktop.ViewModels;

/// <summary>
/// ViewModel for managing study subjects (BS-STU-001).
/// Communicates through <see cref="IAgentClient"/> without opening SQLite directly.
/// </summary>
public sealed class SubjectsViewModel : ViewModelBase
{
    private readonly IAgentClient _agentClient;
    private readonly INavigationService? _navigation;

    private string _name = string.Empty;
    private string? _description;
    private SubjectDto? _selectedSubject;
    private bool _isLoading;
    private string? _errorMessage;
    private string? _statusMessage;
    private string? _nameValidationError;
    private string? _duplicateWarning;

    public SubjectsViewModel(IAgentClient agentClient, INavigationService? navigation = null)
    {
        _agentClient = agentClient ?? throw new ArgumentNullException(nameof(agentClient));
        _navigation = navigation;

        Subjects = new ObservableCollection<SubjectDto>();

        LoadCommand = new AsyncRelayCommand(LoadSubjectsAsync, () => !IsLoading);
        CreateCommand = new AsyncRelayCommand(CreateSubjectAsync, () => CanCreate);
        UpdateCommand = new AsyncRelayCommand(UpdateSubjectAsync, () => CanUpdate);
        ArchiveCommand = new AsyncRelayCommand(ArchiveSubjectAsync, p => CanArchive(p));
        RestoreCommand = new AsyncRelayCommand(RestoreSubjectAsync, p => CanRestore(p));
        ClearFormCommand = new RelayCommand(ClearForm);

        if (_navigation is not null)
        {
            BackCommand = new RelayCommand(() => _navigation.NavigateToHome());
        }
    }

    public SubjectsViewModel(IFocusClient? focusClient, INavigationService? navigation = null)
        : this(focusClient is NamedPipeFocusClient npfc ? npfc.AgentClient : new InMemoryAgentClient(), navigation)
    {
    }

    public ObservableCollection<SubjectDto> Subjects { get; }

    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand CreateCommand { get; }
    public AsyncRelayCommand UpdateCommand { get; }
    public AsyncRelayCommand ArchiveCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand ClearFormCommand { get; }
    public RelayCommand? BackCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                ValidateName();
                UpdateCommandStates();
            }
        }
    }

    public string? Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public SubjectDto? SelectedSubject
    {
        get => _selectedSubject;
        set
        {
            if (SetProperty(ref _selectedSubject, value))
            {
                if (_selectedSubject is not null)
                {
                    _name = _selectedSubject.Name;
                    _description = _selectedSubject.Description;
                    OnPropertyChanged(nameof(Name));
                    OnPropertyChanged(nameof(Description));
                    ValidateName();
                }

                OnPropertyChanged(nameof(HasSelectedSubject));
                UpdateCommandStates();
            }
        }
    }

    public bool HasSelectedSubject => _selectedSubject is not null;

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

    public string? NameValidationError
    {
        get => _nameValidationError;
        private set
        {
            if (SetProperty(ref _nameValidationError, value))
            {
                OnPropertyChanged(nameof(HasNameValidationError));
            }
        }
    }

    public bool HasNameValidationError => !string.IsNullOrEmpty(NameValidationError);

    public string? DuplicateWarning
    {
        get => _duplicateWarning;
        private set
        {
            if (SetProperty(ref _duplicateWarning, value))
            {
                OnPropertyChanged(nameof(HasDuplicateWarning));
            }
        }
    }

    public bool HasDuplicateWarning => !string.IsNullOrEmpty(DuplicateWarning);

    public bool CanCreate =>
        !IsLoading &&
        !string.IsNullOrWhiteSpace(Name) &&
        Name.Trim().Length <= 100;

    public bool CanUpdate =>
        !IsLoading &&
        SelectedSubject is not null &&
        !string.IsNullOrWhiteSpace(Name) &&
        Name.Trim().Length <= 100;

    public bool CanArchive(object? parameter = null)
    {
        if (IsLoading) return false;
        var target = ResolveSubject(parameter);
        return target is not null && !target.IsArchived;
    }

    public bool CanRestore(object? parameter = null)
    {
        if (IsLoading) return false;
        var target = ResolveSubject(parameter);
        return target is not null && target.IsArchived;
    }

    public async Task LoadSubjectsAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var subjects = await _agentClient.GetSubjectsAsync(includeArchived: true);
            Subjects.Clear();
            foreach (var subject in subjects)
            {
                Subjects.Add(subject);
            }

            ValidateName();
            StatusMessage = $"Loaded {subjects.Count} subjects.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load subjects: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CreateSubjectAsync()
    {
        if (!CanCreate) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var request = new CreateSubjectRequest(Name.Trim(), Description?.Trim());
            var created = await _agentClient.CreateSubjectAsync(request);

            Subjects.Add(created);
            StatusMessage = $"Subject '{created.Name}' created successfully.";
            ClearForm();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to create subject: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task UpdateSubjectAsync()
    {
        if (!CanUpdate || SelectedSubject is null) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var request = new UpdateSubjectRequest(SelectedSubject.Id, Name.Trim(), Description?.Trim());
            var updated = await _agentClient.UpdateSubjectAsync(request);

            var index = Subjects.IndexOf(SelectedSubject);
            if (index >= 0)
            {
                Subjects[index] = updated;
                SelectedSubject = updated;
            }

            StatusMessage = $"Subject '{updated.Name}' updated successfully.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to update subject: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ArchiveSubjectAsync(object? parameter)
    {
        var target = ResolveSubject(parameter);
        if (target is null) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var request = new ArchiveSubjectRequest(target.Id, Restore: false);
            var updated = await _agentClient.ArchiveSubjectAsync(request);

            var index = Subjects.IndexOf(target);
            if (index >= 0)
            {
                Subjects[index] = updated;
                if (SelectedSubject?.Id == target.Id)
                {
                    SelectedSubject = updated;
                }
            }

            StatusMessage = $"Subject '{target.Name}' archived.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to archive subject: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RestoreSubjectAsync(object? parameter)
    {
        var target = ResolveSubject(parameter);
        if (target is null) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var request = new ArchiveSubjectRequest(target.Id, Restore: true);
            var updated = await _agentClient.ArchiveSubjectAsync(request);

            var index = Subjects.IndexOf(target);
            if (index >= 0)
            {
                Subjects[index] = updated;
                if (SelectedSubject?.Id == target.Id)
                {
                    SelectedSubject = updated;
                }
            }

            StatusMessage = $"Subject '{target.Name}' restored.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to restore subject: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ClearForm()
    {
        _selectedSubject = null;
        _name = string.Empty;
        _description = null;
        _nameValidationError = null;
        _duplicateWarning = null;

        OnPropertyChanged(nameof(SelectedSubject));
        OnPropertyChanged(nameof(HasSelectedSubject));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(NameValidationError));
        OnPropertyChanged(nameof(HasNameValidationError));
        OnPropertyChanged(nameof(DuplicateWarning));
        OnPropertyChanged(nameof(HasDuplicateWarning));

        UpdateCommandStates();
    }

    private void ValidateName()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            NameValidationError = "Subject name is required.";
            DuplicateWarning = null;
            return;
        }

        var trimmed = Name.Trim();
        if (trimmed.Length > 100)
        {
            NameValidationError = "Subject name cannot exceed 100 characters.";
        }
        else
        {
            NameValidationError = null;
        }

        // Check for duplicate name warning (BS-STU-001: duplicates allowed, UI warns)
        var hasDuplicate = Subjects.Any(s =>
            s.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
            && (_selectedSubject is null || s.Id != _selectedSubject.Id));

        DuplicateWarning = hasDuplicate
            ? $"Warning: A subject named '{trimmed}' already exists. Duplicate names are allowed, but may cause confusion."
            : null;
    }

    private SubjectDto? ResolveSubject(object? parameter) =>
        parameter as SubjectDto ?? SelectedSubject;

    private void UpdateCommandStates()
    {
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CanUpdate));
        LoadCommand.RaiseCanExecuteChanged();
        CreateCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
        ArchiveCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
    }
}
