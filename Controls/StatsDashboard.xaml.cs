using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageNotch.Models;
using UsageNotch.Services;

namespace UsageNotch.Controls;

public partial class StatsDashboard : System.Windows.Controls.UserControl
{
    private UsageCoordinator? _coordinator;
    private bool _updating;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    public StatsDashboard()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => UpdateClock();
        Loaded += (_, _) => { if (_coordinator is not null) _coordinator.Refreshed += Refresh; Refresh(); _clock.Start(); };
        Unloaded += (_, _) => { _clock.Stop(); if (_coordinator is not null) _coordinator.Refreshed -= Refresh; };
    }
    public void Connect(UsageCoordinator coordinator) { _coordinator = coordinator; ProviderPicker.ItemsSource = coordinator.Items; ProviderPicker.SelectedIndex = 0; Refresh(); }
    private void ProviderChanged(object sender, SelectionChangedEventArgs e) => Refresh();
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updating) Render(); }
    private void Refresh()
    {
        if (_coordinator is null || WindowPicker is null) return;
        _updating = true;
        var selected = (WindowPicker.SelectedItem as LimitWindow)?.Id;
        var provider = ProviderPicker.SelectedItem as ProviderViewModel;
        WindowPicker.ItemsSource = provider?.Windows;
        WindowPicker.SelectedItem = provider?.Windows.FirstOrDefault(w => w.Id == selected) ?? provider?.Windows.FirstOrDefault();
        _updating = false;
        Render();
    }
    private void Render()
    {
        if (_coordinator is null || Chart is null) return;
        var now = DateTimeOffset.UtcNow;
        var provider = ProviderPicker.SelectedItem as ProviderViewModel;
        var window = WindowPicker.SelectedItem as LimitWindow;
        StatusLabel.Text = provider?.StatusPill ?? "No enabled providers";
        UsageLabel.Text = window?.UsedFraction is { } used ? $"{used * 100:0.0}% used" : "No usage reported";
        ObservationLabel.Text = provider?.Snapshot.FetchedAt is { } at ? $"Last observation · {at.ToLocalTime():MMM d, HH:mm:ss zzz}" : "Waiting for a successful provider reading";
        Chart.End = now;
        Chart.Start = now.AddDays(RangePicker.SelectedIndex switch { 1 => -7, 2 => -30, _ => -1 });
        IReadOnlyList<UsagePoint> points = [];
        if (provider is not null && window is not null)
            lock (_coordinator.History) points = _coordinator.History.Read(provider.Id, _coordinator.History.AccountKey(provider.Snapshot), window.Id, Chart.Start);
        Chart.Points = points;
        Chart.InvalidateVisual();
        var forecast = UsageForecast.Calculate(points, provider?.Status ?? SnapshotStatus.Error, now);
        ForecastLabel.Text = forecast.Summary;
        ForecastDetail.Text = forecast.Explanation;
        HistoryNote.Text = _coordinator.History.Error ?? "Private by design. Up to 30 days stored on this PC. Collected only while UsageNotch runs. Account identity changes separate history; providers without identity begin a new series each launch.";
        UpdateClock();
    }
    private void UpdateClock()
    {
        var reset = (WindowPicker.SelectedItem as LimitWindow)?.ResetsAt;
        ResetLabel.Text = ResetClock.Describe(reset, DateTimeOffset.UtcNow);
        ResetDate.Text = reset is { } at ? $"Provider-reported · {at.ToLocalTime():dddd, MMM d yyyy · HH:mm:ss zzz}" : "This provider has not supplied an exact reset time.";
        if (Chart.Points.Count > 0)
        {
            var forecast = UsageForecast.Calculate(Chart.Points, (ProviderPicker.SelectedItem as ProviderViewModel)?.Status ?? SnapshotStatus.Error, DateTimeOffset.UtcNow);
            ForecastLabel.Text = forecast.Summary;
            ForecastDetail.Text = forecast.Explanation;
        }
    }
}
