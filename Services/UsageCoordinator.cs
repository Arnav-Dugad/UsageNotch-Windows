using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UsageNotch.Models;
using UsageNotch.Providers;

namespace UsageNotch.Services;

public sealed class UsageCoordinator : IDisposable, INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IUsageProvider> _providers = [];
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ClaudeSubscriptionProvider _claude;
    private int _refreshQueued;
    private bool _isRefreshing;
    private bool _disposed;

    public ObservableCollection<ProviderViewModel> Items { get; } = [];
    public AlertService Alerts { get; }
    public UsageHistory History { get; }
    public AppSettings Settings => _settings;
    public DateTimeOffset? LastRefresh { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Refreshed;

    public UsageCoordinator(AppSettings settings, UsageHistory? history = null)
    {
        _settings = settings;
        History = history ?? new UsageHistory();
        Alerts = new AlertService(settings);
        _claude = new ClaudeSubscriptionProvider(_http);
        RebuildProviders();
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set { if (_isRefreshing == value) return; _isRefreshing = value; OnPropertyChanged(); }
    }

    public void RebuildProviders()
    {
        var desired = new List<IUsageProvider>();
        if (_settings.ClaudeEnabled) desired.Add(_claude);
        if (_settings.CodexEnabled) desired.Add(new CodexSubscriptionProvider());
        if (_settings.GeminiEnabled) desired.Add(new GeminiSubscriptionProvider(_http, _settings));
        if (_settings.CursorEnabled) desired.Add(new CursorSubscriptionProvider(_http));
        if (_settings.OpenAiApiEnabled) desired.Add(new OpenAiApiProvider(_http, _settings));
        if (_settings.AnthropicApiEnabled) desired.Add(new AnthropicApiProvider(_http, _settings));

        _providers.Clear();
        _providers.AddRange(desired);

        for (var i = Items.Count - 1; i >= 0; i--)
            if (!desired.Any(provider => provider.Id == Items[i].Id)) Items.RemoveAt(i);

        for (var i = 0; i < desired.Count; i++)
        {
            var existingIndex = Items.ToList().FindIndex(item => item.Id == desired[i].Id);
            if (existingIndex < 0)
                Items.Insert(i, new ProviderViewModel(LoadingSnapshot(desired[i]), _settings));
            else if (existingIndex != i)
                Items.Move(existingIndex, i);
        }
        foreach (var item in Items) item.RefreshAppearance();
    }

    public async Task StartAsync()
    {
        await RefreshAllAsync();
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_settings.PollSeconds, 30, 900)), _cts.Token);
                await RefreshAllAsync();
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RefreshAllAsync()
    {
        if (_settings.ReviewSession || _disposed) return;
        if (!await _refreshLock.WaitAsync(0))
        {
            Interlocked.Exchange(ref _refreshQueued, 1);
            return;
        }
        try
        {
            await OnUi(() => IsRefreshing = true);
            var providers = _providers.ToArray();
            var tasks = providers.Select(async provider =>
            {
                ProviderSnapshot snapshot;
                try
                {
                    // CLI startup, credential reads and JSON parsing must not stall dock input.
                    snapshot = await Task.Run(() => provider.FetchAsync(_cts.Token));
                }
                catch (Exception exception)
                {
                    snapshot = new ProviderSnapshot(provider.Id, provider.DisplayName, GlyphFor(provider.Id),
                        Fidelity.Derived, SnapshotStatus.Error, [], exception.Message);
                }
                await Task.Run(() => { lock (History) History.Record(snapshot, DateTimeOffset.UtcNow); });
                await OnUi(() => Apply(snapshot));
            }).ToArray();
            await Task.WhenAll(tasks);

            await OnUi(() =>
            {
                LastRefresh = DateTimeOffset.Now;
                IsRefreshing = false;
                try { Alerts.Evaluate(Items); } catch { }
                Refreshed?.Invoke();
            });
        }
        finally
        {
            _refreshLock.Release();
            if (!_cts.IsCancellationRequested && Interlocked.Exchange(ref _refreshQueued, 0) == 1)
                _ = RefreshAllAsync();
        }
    }

    private static async Task OnUi(Action action)
    {
        var application = System.Windows.Application.Current;
        if (application is null) { action(); return; }
        await application.Dispatcher.InvokeAsync(action);
    }

    private void Apply(ProviderSnapshot snapshot)
    {
        if (!_providers.Any(provider => provider.Id == snapshot.Id)) return;
        var existing = Items.FirstOrDefault(item => item.Id == snapshot.Id);
        if (existing is null) Items.Add(new ProviderViewModel(snapshot, _settings));
        else existing.Snapshot = snapshot;
    }

    private static ProviderSnapshot LoadingSnapshot(IUsageProvider provider) => new(
        provider.Id, provider.DisplayName, GlyphFor(provider.Id), Fidelity.Official,
        SnapshotStatus.Loading, [], "Securely checking your account…");

    private static string GlyphFor(string id) => id switch
    {
        "claude" or "anthropic_api" => "✳",
        "codex" => "✦",
        "gemini" => "◆",
        "cursor" => "⌾",
        "openai_api" => "◎",
        _ => "•"
    };

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _http.Dispose();
        // In-flight refreshes still own the semaphore and cancellation source.
        // Let them finish and release normally; both are reclaimed with this coordinator.
    }
}
