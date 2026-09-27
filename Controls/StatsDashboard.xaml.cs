using System.Windows;
using UsageNotch.Services;

namespace UsageNotch.Controls;

public partial class StatsDashboard : System.Windows.Controls.UserControl
{
    private UsageCoordinator? _coordinator;
    public StatsDashboard()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (_coordinator is not null) { _coordinator.Refreshed -= Refresh; _coordinator.Refreshed += Refresh; } Refresh(); };
        Unloaded += (_, _) => { if (_coordinator is not null) _coordinator.Refreshed -= Refresh; };
    }
    public void Connect(UsageCoordinator coordinator) { _coordinator = coordinator; Refresh(); }
    private void Refresh()
    {
        if (_coordinator is null) return;
        var ids = _coordinator.Items.Select(p => p.Id).ToList();
        // Both key providers remain visible, even when disabled or awaiting authentication.
        foreach (var id in new[] { "claude", "codex" }) if (!ids.Contains(id)) ids.Insert(0, id);
        ids = ids.OrderBy(id => id == "claude" ? 0 : id == "codex" ? 1 : 2).ToList();
        foreach (var obsolete in Cards.Children.OfType<ProviderStatsCard>().Where(c => !ids.Contains(c.ProviderId)).ToArray()) Cards.Children.Remove(obsolete);
        foreach (var id in ids)
        {
            var card = Cards.Children.OfType<ProviderStatsCard>().FirstOrDefault(c => c.ProviderId == id);
            if (card is null) { card = new ProviderStatsCard(_coordinator, id); Cards.Children.Add(card); }
            card.Refresh();
        }
        ResizeCards();
    }
    private void Cards_SizeChanged(object sender, SizeChangedEventArgs e) => ResizeCards();
    private void ResizeCards()
    {
        var width = Math.Max(300, Cards.ActualWidth);
        var columns = width >= 850 ? 2 : 1;
        foreach (var card in Cards.Children.OfType<ProviderStatsCard>()) card.Width = Math.Max(280, width / columns - 10);
    }
    public FrameworkElement? FocusProvider(string id)
    {
        var card = Cards.Children.OfType<ProviderStatsCard>().FirstOrDefault(c => c.ProviderId == id);
        card?.BringIntoView(); return card?.Logo;
    }
}
