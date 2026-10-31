using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UsageNotch.Services;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace UsageNotch.Controls;

/// <summary>Local token history: totals, the API-price equivalent, 30 days of bars and where the tokens went.</summary>
public static class TokenViews
{
    public static UIElement Summary(TokenReport r, Color accent, bool reduced, bool compact = false)
    {
        var stack = new StackPanel();
        Grid Tile(string label, TokenTotals t)
        {
            var value = Ui.Text(Ui.Tokens(t.Total), 26, FontWeights.Light, "Ui.Text", false);
            var cost = Ui.Text(t.Cost is { } c ? $"≈ {Ui.Money(c)} at API prices" : r.AllDevices ? "tokens, all devices" : "tokens", 11.5, FontWeights.Normal, "Ui.Muted", false);
            var grid = new Grid(); grid.Children.Add(Ui.Inner(Ui.Stack(Ui.Text(label, 11.5, FontWeights.SemiBold, "Ui.Faint", false), value, cost), new Thickness(14, 10, 14, 12)));
            grid.Margin = new Thickness(0, 0, 10, 10);
            grid.ToolTip = $"Input {Ui.Tokens(t.Input)} · output {Ui.Tokens(t.Output)} · cache read {Ui.Tokens(t.CacheRead)} · cache write {Ui.Tokens(t.CacheWrite)}";
            return grid;
        }
        var tiles = new UniformGrid2(2);
        tiles.Add(Tile("Today", r.Today)); tiles.Add(Tile("Last 7 days", r.Week));
        stack.Children.Add(tiles.Panel);
        if (!compact)
        {
            var bars = new DailyBars { Accent = accent, Reduced = reduced, Height = 130, Margin = new Thickness(0, 6, 0, 10) };
            var max = Math.Max(1, r.Days.Max(d => d.Total));
            bars.Days = r.Days.Select(d => new DayUsage(d.Date, d.Total > 0 ? (double)d.Total / max : 0)).ToArray();
            bars.ToolTip = "Tokens per day, relative to your busiest day in this period";
            stack.Children.Add(bars);
        }
        if (r.Projects.Count > 0)
        {
            stack.Children.Add(Section("Projects · last 7 days"));
            stack.Children.Add(ShareList.Build(r.Projects.Take(compact ? 4 : 8).Select(x => (x.Name, x.Share, Ui.Tokens(x.Total) + (x.Cost is { } c ? $" · {Ui.Money(c)}" : ""))), accent, reduced));
        }
        if (r.Models.Count > 0 && !compact)
        {
            stack.Children.Add(Section("Models · last 7 days"));
            stack.Children.Add(ShareList.Build(r.Models.Select(x => (x.Name, x.Share, Ui.Tokens(x.Total) + (x.Cost is { } c ? $" · {Ui.Money(c)}" : ""))), Palette.Lerp(accent, Theme.Current.Text, .25), reduced));
        }
        if (r.AllDevices && (r.Lifetime is not null || r.PeakDaily is not null))
        {
            var facts = Ui.Text($"Lifetime {Ui.Tokens(r.Lifetime ?? 0)} tokens" + (r.PeakDaily is { } peak ? $" · busiest day {Ui.Tokens(peak)}" : "") + (r.LongestStreak is { } streak ? $" · longest streak {streak} days" : ""), 12, FontWeights.Normal, "Ui.Muted");
            facts.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Insert(1, facts);
        }
        var note = Ui.Text(r.AllDevices
            ? "Totals come from OpenAI for this account, across every device. Projects and models come from the Codex transcripts on this PC."
            : "From the transcripts Claude Code and Codex keep on this PC. API-price equivalent: what the same tokens would cost on the pay-as-you-go API, not what your subscription charges.", 11, FontWeights.Normal, "Ui.Faint");
        note.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(note);
        return stack;
    }

    private static TextBlock Section(string text) { var t = Ui.Text(text, 11.5, FontWeights.SemiBold, "Ui.Muted", false); t.Margin = new Thickness(0, 6, 0, 8); return t; }
}

/// <summary>A tiny equal-columns panel built on Grid.</summary>
public sealed class UniformGrid2(int columns)
{
    public Grid Panel { get; } = CreateGrid(columns);
    private int _count;
    private static Grid CreateGrid(int columns) { var g = new Grid(); for (var i = 0; i < columns; i++) g.ColumnDefinitions.Add(new ColumnDefinition()); return g; }
    public void Add(UIElement e)
    {
        var cols = Panel.ColumnDefinitions.Count; var row = _count / cols;
        while (Panel.RowDefinitions.Count <= row) Panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(e, _count % cols); Grid.SetRow(e, row); Panel.Children.Add(e); _count++;
    }
}
