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
    private volatile ProviderViewModel[] _itemsCopy = [];
    /// <summary>A copy of Items that background threads (the phone server, reports) can read safely.</summary>
    public IReadOnlyList<ProviderViewModel> ItemsSnapshot => _itemsCopy;
    public AlertService Alerts { get; }
    public UsageHistory History { get; }
    public LocalTokenUsage Tokens { get; } = new();
    public StatusPages Health { get; }
    private readonly Dictionary<string, IUsageProvider> _accountProviders = new();
    private DateTimeOffset _lastTokenScan;
    private DateTimeOffset _lastChange;
    private CancellationTokenSource _wake = new();
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
        Health = new StatusPages(_http);
        Tokens.Changed += () => _tokenReports = null;
        // Fewer gaps in history: read again as soon as the PC wakes or you unlock it.
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += SessionSwitched;
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
        foreach (var account in _settings.Accounts.Where(a => a.Enabled && ExtraAccount.SlugPattern.IsMatch(a.Slug)))
        {
            // Keep each account's provider (and its cooldown and cache) for as long as the account exists.
            var key = account.ProviderId + "|" + account.Folder + "|" + account.Label;
            if (!_accountProviders.TryGetValue(key, out var provider))
                _accountProviders[key] = provider = account.Kind == "codex" ? new CodexSubscriptionProvider(account) : new ClaudeSubscriptionProvider(_http, account);
            desired.Add(provider);
        }
        // Your own order (shared with your phone) first; everything else keeps the default order.
        var order = _settings.ProviderOrder;
        desired = desired.Select((p, i) => (p, i)).OrderBy(x => order.IndexOf(x.p.Id) is var at && at >= 0 ? at : order.Count + x.i).Select(x => x.p).ToList();

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
        _itemsCopy = Items.ToArray();
    }

    public async Task StartAsync()
    {
        await RefreshAllAsync();
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // While usage is moving, read twice as often (at least every 30 s) so pace and daily totals stay precise.
                var interval = Math.Clamp(_settings.PollSeconds, 30, 900);
                if (DateTimeOffset.UtcNow - _lastChange < TimeSpan.FromMinutes(10)) interval = Math.Max(30, interval / 2);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _wake.Token);
                try { await Task.Delay(TimeSpan.FromSeconds(interval), wait.Token); }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { _wake = new(); await Task.Delay(TimeSpan.FromSeconds(4), _cts.Token); }
                await RefreshAllAsync();
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private void PowerChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e) { if (e.Mode == Microsoft.Win32.PowerModes.Resume) Wake(); }
    private void SessionSwitched(object? sender, Microsoft.Win32.SessionSwitchEventArgs e) { if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.SessionLogon) Wake(); }
    /// <summary>Cuts the current wait short; the loop reads again a few seconds later, once the network is back.</summary>
    private void Wake() { try { _wake.Cancel(); } catch (ObjectDisposedException) { } }

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

            if (DateTimeOffset.UtcNow - _lastTokenScan > TimeSpan.FromMinutes(5))
            {
                _lastTokenScan = DateTimeOffset.UtcNow;
                _ = Tokens.ScanAsync(_settings, _cts.Token);
            }
            _ = Health.RefreshAsync(_settings, providers.Select(p => p.Id));
            var items = await OnUiResult(() => Items.ToArray());
            var alertContext = await Task.Run(() => AlertContextFor(items));
            await OnUi(() =>
            {
                LastRefresh = DateTimeOffset.Now;
                IsRefreshing = false;
                try { Alerts.Evaluate(Items, alertContext); } catch { }
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

    /// <summary>
    /// A refresh a paired phone asked for: "done" after a fresh round, "throttled" when one finished under 20 seconds ago,
    /// "timeout" after 25 seconds. Providers keep their own cooldowns, so this never hammers an endpoint.
    /// </summary>
    public async Task<string> RefreshForPhoneAsync()
    {
        if (LastRefresh is { } last && DateTimeOffset.Now - last < TimeSpan.FromSeconds(20)) return "throttled";
        var done = new TaskCompletionSource();
        void Finished() => done.TrySetResult();
        Refreshed += Finished;
        try
        {
            _ = OnUi(() => _ = RefreshAllAsync());
            return await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(25))) == done.Task ? "done" : "timeout";
        }
        finally { Refreshed -= Finished; }
    }

    private static async Task<T> OnUiResult<T>(Func<T> func)
    {
        var application = System.Windows.Application.Current;
        return application is null ? func() : await application.Dispatcher.InvokeAsync(func);
    }

    private readonly Dictionary<string, (DateTimeOffset At, WindowSummary? Value)> _summaries = new();

    /// <summary>The pace forecast for one window from the last three hours of readings.</summary>
    public PaceForecast? PaceFor(ProviderSnapshot snapshot, LimitWindow window)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            lock (History)
            {
                var points = History.Read(snapshot.Id, History.AccountKey(snapshot), window.Id, now.AddHours(-3));
                return points.Count == 0 ? null : UsageForecast.Calculate(points, snapshot.Status, now, _settings);
            }
        }
        catch { return null; }
    }

    /// <summary>90 days of history for one window, summarised; cached for five minutes.</summary>
    public WindowSummary? SummaryFor(ProviderSnapshot snapshot, LimitWindow window, bool fresh = false)
    {
        var now = DateTimeOffset.UtcNow;
        string key;
        lock (History) key = snapshot.Id + "|" + History.AccountKey(snapshot) + "|" + window.Id;
        lock (_summaries) if (!fresh && _summaries.TryGetValue(key, out var cached) && now - cached.At < TimeSpan.FromMinutes(5)) return cached.Value;
        WindowSummary? value = null;
        try
        {
            lock (History)
            {
                var account = History.AccountKey(snapshot);
                var points = History.Read(snapshot.Id, account, window.Id, now.AddDays(-HistoryInsights.CalendarDays - 1));
                var events = History.ReadEvents(snapshot.Id, account, window.Id, now.AddDays(-30));
                if (points.Count > 0) value = HistoryInsights.Summarize(points, events, now);
            }
        }
        catch { }
        lock (_summaries) _summaries[key] = (now, value);
        return value;
    }

    private readonly Dictionary<string, (DateTimeOffset At, AllTimeTotals? Value)> _allTime = new();

    /// <summary>Deletes this PC's usage history and forgets every summary built from it.</summary>
    public bool DeleteHistory()
    {
        bool ok;
        lock (History) ok = History.DeleteAll();
        lock (_summaries) _summaries.Clear();
        lock (_allTime) _allTime.Clear();
        lock (_reports) _reports.Clear();
        Phone.PhoneSnapshot.ClearCache();
        return ok;
    }

    /// <summary>Everything ever recorded for one window, streamed from the database; cached for 30 minutes.</summary>
    public AllTimeTotals? AllTimeFor(ProviderSnapshot snapshot, LimitWindow window)
    {
        var now = DateTimeOffset.UtcNow;
        string key, account;
        lock (History) { account = History.AccountKey(snapshot); key = snapshot.Id + "|" + account + "|" + window.Id; }
        lock (_allTime) if (_allTime.TryGetValue(key, out var cached) && now - cached.At < TimeSpan.FromMinutes(30)) return cached.Value;
        var counter = new HistoryInsights.AllTimeCounter();
        try { lock (History) History.ForEach(snapshot.Id, account, window.Id, counter.Add); } catch { }
        var value = counter.Result();
        lock (_allTime) _allTime[key] = (now, value);
        return value;
    }

    private readonly Dictionary<bool, (DateTimeOffset At, WeekReport? Value)> _reports = new();

    /// <summary>"This week" across every provider, in dock order; cached for five minutes. Project names only when asked for.</summary>
    public WeekReport? WeekReportFor(bool projects = true, bool fresh = false)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_reports) if (!fresh && _reports.TryGetValue(projects, out var cached) && now - cached.At < TimeSpan.FromMinutes(5)) return cached.Value;
        WeekReport? value = null;
        IReadOnlyList<DayNote> notes; lock (History) notes = History.Notes();
        try { value = WeeklyReport.Build(ItemsSnapshot.Where(i => i.Status != SnapshotStatus.Loading).ToArray(), (p, w) => SummaryFor(p.Snapshot, w), TokenReports(), projects, now, _settings.Use24HourTime, notes); }
        catch { }
        lock (_reports) _reports[projects] = (now, value);
        return value;
    }

    /// <summary>One combined number for every provider (see <see cref="UsageScore"/>).</summary>
    public UsageScore? Score() => UsageScore.Compute(ItemsSnapshot.Where(i => i.Status != SnapshotStatus.Loading).ToArray(), (p, w) => SummaryFor(p.Snapshot, w));

    /// <summary>Tokens per limit and the month against the plan, for one token source.</summary>
    public TokenFacts? FactsFor(TokenReport report)
    {
        var p = ItemsSnapshot.FirstOrDefault(i => i.Id == report.Source);
        var w = p?.SessionWindow is { UsedFraction: not null } s ? s : p?.WeeklyWindow;
        double? used = p is not null && w is not null ? SummaryFor(p.Snapshot, w)?.Total(7) : null;
        return WeeklyReport.Facts(report, p, used, w?.Label);
    }

    /// <summary>Saves a day note made on this PC (empty text deletes it) and forgets the cached report.</summary>
    public bool SetNote(DateTime date, string text)
    {
        bool changed;
        lock (History) changed = History.SetNote(date, text, DateTimeOffset.UtcNow);
        lock (_reports) _reports.Clear();
        return changed;
    }

    /// <summary>Forgets the cached report after notes arrive from a phone.</summary>
    public void NotesChanged() { lock (_reports) _reports.Clear(); }

    public StartHint? BestStartFor(ProviderViewModel item)
    {
        if (item.SessionWindow is not { } session || SummaryFor(item.Snapshot, session) is not { } summary) return null;
        return BestStart.Hint(session, BestStart.PeriodOf(session), summary.Heat, summary.Observed, DateTimeOffset.Now);
    }

    private AlertContext AlertContextFor(IReadOnlyList<ProviderViewModel> items)
    {
        var paces = new Dictionary<string, PaceForecast?>(); var starts = new Dictionary<string, StartHint?>(); var summaries = new Dictionary<string, WindowSummary?>();
        foreach (var item in items.Where(i => i.Status == SnapshotStatus.Ok))
        {
            foreach (var w in item.Windows.Where(w => w.UsedFraction is not null)) paces[AlertContext.Key(item.Id, w.Id)] = PaceFor(item.Snapshot, w);
            if (item.SessionWindow is { } session) summaries[AlertContext.Key(item.Id, session.Id)] = SummaryFor(item.Snapshot, session);
            if (_settings.AlertGoodMoment) starts[item.Id] = BestStartFor(item);
        }
        var local = DateTimeOffset.Now;
        var report = _settings.WeeklyRecap && local.DayOfWeek == DayOfWeek.Sunday && local.Hour >= 18 ? WeekReportFor() : null;
        return new AlertContext(paces, starts, summaries, report, _settings.ProjectBudgets.Count > 0 ? TokenReports() : null);
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
        if (existing is not null && snapshot.Status == SnapshotStatus.Ok && existing.Snapshot.Status == SnapshotStatus.Ok
            && snapshot.Windows.Any(w => existing.Windows.FirstOrDefault(o => o.Id == w.Id)?.UsedFraction is { } before && w.UsedFraction is { } after && Math.Abs(after - before) > .0001))
            _lastChange = DateTimeOffset.UtcNow;
        if (existing is null) Items.Add(new ProviderViewModel(snapshot, _settings));
        else existing.Snapshot = snapshot;
        _itemsCopy = Items.ToArray();
    }

    private static ProviderSnapshot LoadingSnapshot(IUsageProvider provider) => new(
        provider.Id, provider.DisplayName, GlyphFor(provider.Id), Fidelity.Official,
        SnapshotStatus.Loading, [], "Securely checking your account…");

    private static string GlyphFor(string id) => ProviderKind.Of(id) switch
    {
        "claude" or "anthropic_api" => "✳",
        "codex" => "✦",
        "gemini" => "◆",
        "cursor" => "⌾",
        "openai_api" => "◎",
        _ => "•"
    };

    private (DateTimeOffset At, IReadOnlyList<TokenReport> Reports)? _tokenReports;
    /// <summary>Token reports for every local source, recomputed at most once a minute.</summary>
    public IReadOnlyList<TokenReport> TokenReports(bool fresh = false)
    {
        var now = DateTimeOffset.Now;
        if (!fresh && _tokenReports is { } cached && now - cached.At < TimeSpan.FromMinutes(1)) return cached.Reports;
        var reports = Tokens.Reports(_settings, now).ToList();
        // Codex accounts report their own totals across devices; prefer those to this PC's transcripts.
        foreach (var item in ItemsSnapshot.Where(i => i.Kind == "codex" && i.Snapshot.Tokens is { Days.Count: > 0 }))
        {
            var local = reports.FirstOrDefault(r => r.Source == item.Id);
            var merged = TokenReport.WithAccount(local, item.Id, item.DisplayName, item.Snapshot.Tokens!, now);
            if (local is null) reports.Add(merged); else reports[reports.IndexOf(local)] = merged;
        }
        _tokenReports = (now, reports);
        return reports;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= SessionSwitched;
        _cts.Cancel();
        _http.Dispose();
        // In-flight refreshes still own the semaphore and cancellation source.
        // Let them finish and release normally; both are reclaimed with this coordinator.
    }
}
