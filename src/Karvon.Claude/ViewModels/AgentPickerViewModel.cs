using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Claude;

namespace Karvon.ViewModels;

/// <summary>An agent in the picker: whether it is there (installed, with its key) and what it needs otherwise.</summary>
public sealed partial class AgentProviderItem(IAgentBackend backend) : ObservableObject
{
    public IAgentBackend Backend => backend;

    public string Name => backend.Name;

    public string Description => backend.Description;

    /// <summary>What <see cref="IAgentBackend.Find"/> gave: its program or its address; null when it is not there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(Status), nameof(NeedsKey), nameof(IsMissing))]
    public partial string? Executable { get; private set; } = backend.Find();

    public bool IsReady => Executable is not null;

    /// <summary>It waits for the user's key (OpenRouter's).</summary>
    public bool NeedsKey => !IsReady && backend.NeedsKey;

    /// <summary>It is not installed (and no key would help).</summary>
    public bool IsMissing => !IsReady && !backend.NeedsKey;

    public string Status => IsReady ? "Ready" : backend.NeedsKey ? "Needs an API key" : "Not installed";

    public void Refresh() => Executable = backend.Find();
}

/// <summary>
/// Choose Agent and Model: the agents on the left (each with whether it is there), the chosen one's models on the right with
/// a search over their names, ids and descriptions (OpenRouter has hundreds), its API key asked for when it has none.
/// </summary>
public sealed partial class AgentPickerViewModel : ObservableObject
{
    readonly Func<string> _workingDirectory;
    readonly Func<IAgentBackend, IReadOnlyList<AgentModel>?> _known;
    readonly Dictionary<string, IReadOnlyList<AgentModel>> _models = [];
    readonly string? _currentModel;
    readonly IAgentBackend? _current;
    CancellationTokenSource? _loading;

    /// <param name="known">The models already known of an agent (the running conversation's); null to ask it.</param>
    public AgentPickerViewModel(IReadOnlyList<IAgentBackend> backends, IAgentBackend? current, string? currentModel, Func<string> workingDirectory,
        Func<IAgentBackend, IReadOnlyList<AgentModel>?>? known = null)
    {
        _workingDirectory = workingDirectory;
        _known = known ?? (_ => null);
        _current = current;
        _currentModel = currentModel;
        foreach (var backend in backends)
            Providers.Add(new AgentProviderItem(backend));
        SelectedProvider = Providers.FirstOrDefault(p => p.Backend == current) ?? Providers.FirstOrDefault(p => p.IsReady) ?? Providers.FirstOrDefault();
    }

    public ObservableCollection<AgentProviderItem> Providers { get; } = [];

    /// <summary>The chosen agent's models the search keeps.</summary>
    public ObservableCollection<AgentModel> Models { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseCommand))]
    public partial AgentProviderItem? SelectedProvider { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseCommand))]
    public partial AgentModel? SelectedModel { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Why the models could not be listed; null when they were.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>How many models the agent has, and how many the search keeps.</summary>
    [ObservableProperty]
    public partial string Count { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand))]
    public partial string Key { get; set; } = "";

    /// <summary>What was chosen (the model: null for the agent's own default); null until Use.</summary>
    public (IAgentBackend Backend, AgentModel? Model)? Chosen { get; private set; }

    /// <summary>The dialog closes (chosen or cancelled).</summary>
    public event Action? CloseRequested;

    /// <summary>The models are listed; a task the dialog may wait for.</summary>
    public Task Loaded { get; private set; } = Task.CompletedTask;

    partial void OnSelectedProviderChanged(AgentProviderItem? value)
    {
        Key = "";
        Search = "";
        Loaded = LoadAsync(value);
    }

    partial void OnSearchChanged(string value) => Filter();

    async Task LoadAsync(AgentProviderItem? provider)
    {
        _loading?.Cancel();
        Models.Clear();
        SelectedModel = null;
        Problem = null;
        Count = "";
        if (provider?.Executable is not { } executable)
            return;
        var backend = provider.Backend;
        if (!_models.ContainsKey(backend.Id) && _known(backend) is { Count: > 0 } known)
            _models[backend.Id] = known;
        if (!_models.ContainsKey(backend.Id))
        {
            var loading = _loading = new CancellationTokenSource();
            IsLoading = true;
            try
            {
                _models[backend.Id] = await Task.Run(() => backend.ModelsAsync(executable, _workingDirectory(), loading.Token), loading.Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception or HttpRequestException)
            {
                if (loading.IsCancellationRequested)
                    return;
                Problem = ex.Message;
                return;
            }
            finally
            {
                if (_loading == loading)
                    IsLoading = false;
            }
            if (SelectedProvider != provider)
                return;
        }
        Filter();
        if (backend == _current && _currentModel is { } model)
            SelectedModel = Models.FirstOrDefault(m => m.Id == model);
    }

    /// <summary>The models whose name, id or description has every word of the search.</summary>
    void Filter()
    {
        if (SelectedProvider is not { } provider || !_models.TryGetValue(provider.Backend.Id, out var all))
            return;
        var selected = SelectedModel;
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Models.Clear();
        foreach (var model in all.Where(m => words.All(w => m.Id.Contains(w, StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || m.Description?.Contains(w, StringComparison.OrdinalIgnoreCase) == true)))
            Models.Add(model);
        SelectedModel = selected is not null && Models.Contains(selected) ? selected : null;
        Count = words.Length == 0 ? $"{all.Count} models" : $"{Models.Count} of {all.Count} models";
    }

    bool CanSaveKey => Key.Trim().Length > 0 && SelectedProvider?.NeedsKey == true;

    /// <summary>The key goes to the agent (and Karvon's vault); its models are listed then.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveKey))]
    void SaveKey()
    {
        if (SelectedProvider is not { } provider || !provider.Backend.SetKey(Key))
            return;
        Key = "";
        provider.Refresh();
        UseCommand.NotifyCanExecuteChanged();
        Loaded = LoadAsync(provider);
    }

    bool CanUse => SelectedProvider?.IsReady == true;

    [RelayCommand(CanExecute = nameof(CanUse))]
    void Use()
    {
        if (SelectedProvider is not { } provider)
            return;
        Chosen = (provider.Backend, SelectedModel);
        _loading?.Cancel();
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    void Cancel()
    {
        _loading?.Cancel();
        CloseRequested?.Invoke();
    }
}
