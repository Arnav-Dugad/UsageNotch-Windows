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
    public static UIElement Summary(TokenReport r, Color accent, bool reduced, bool compact = false, TokenFacts? facts = null, IReadOnlyDictionary<string, long>? budgets = null)
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

        // What the tokens mean: this month at API prices against your plan, and tokens per full limit.
        var facts2 = new UniformGrid2(2);
        if (r.Month is { } month && month.Total > 0)
        {
            var monthName = (r.MonthFrom ?? DateTime.Today).ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture);
            string value, detail;
            if (month.Cost is { } cost && facts?.PlanPrice is { } price && price > 0)
            { value = Ui.Money(cost); detail = $"at API prices in {monthName} · your {facts.PlanName} plan is {Ui.Money(price)} a month ({cost / price:0.#}× its price)"; }
            else if (month.Cost is { } c2) { value = Ui.Money(c2); detail = $"at API prices in {monthName}"; }
            else { value = Ui.Tokens(month.Total); detail = $"tokens in {monthName}"; }
            facts2.Add(Fact("This month", value, detail));
        }
        if (facts?.PerLimit is { } per) facts2.Add(Fact($"Per {facts.LimitName} limit", Ui.Tokens((long)per), $"tokens for each full {facts.LimitName} limit used this week"));
        if (facts2.Panel.Children.Count > 0) stack.Children.Add(facts2.Panel);
        if (!compact)
        {
            var bars = new DailyBars { Accent = accent, Reduced = reduced, Height = 130, Margin = new Thickness(0, 6, 0, 10) };
            var max = Math.Max(1, r.Days.Max(d => d.Total));
            bars.Days = r.Days.Select(d => new DayUsage(d.Date, d.Total > 0 ? (double)d.Total / max : 0)).ToArray();
            var byDate = r.Days.ToDictionary(d => d.Date);
            bars.LimitLines = false;
            bars.Describe = d => $"{d.Date:dddd, MMM d}\n" + (byDate.TryGetValue(d.Date, out var td) ? $"{Ui.Tokens(td.Total)} tokens" + (td.Cost is { } c ? $" · ≈ {Ui.Money(c)} at API prices" : "") : "No tokens");
            stack.Children.Add(bars);
        }
        if (r.Projects.Count > 0)
        {
            stack.Children.Add(Section("Projects · last 7 days"));
            stack.Children.Add(ShareList.Build(r.Projects.Take(compact ? 4 : 8).Select(x => (x.Name, x.Share, Ui.Tokens(x.Total) + (x.Cost is { } c ? $" · {Ui.Money(c)}" : ""))), accent, reduced));
            // Weekly token budgets for projects (Settings → Budgets).
            foreach (var x in r.Projects)
            {
                if (budgets?.GetValueOrDefault(r.Source + "|" + x.Name) is not { } budget || budget <= 0) continue;
                var share = (double)x.Total / budget;
                var bar = new UsageBar { Height = 6, Value = Math.Min(1, share), Used = share, Reduced = reduced, Margin = new Thickness(0, 6, 0, 0) };
                var line = Ui.Columns((Ui.Text($"{x.Name} budget", 12, FontWeights.SemiBold, "Ui.Text", false), new GridLength(1, GridUnitType.Star)),
                    (Ui.Text($"{Ui.Tokens(x.Total)} of {Ui.Tokens(budget)} this week", 12, FontWeights.Normal, share >= 1 ? "Ui.Danger" : "Ui.Muted", false), GridLength.Auto));
                var block = Ui.Stack(line, bar); block.Margin = new Thickness(0, 6, 0, 6);
                stack.Children.Add(block);
            }
        }
        if (r.Models.Count > 0 && !compact)
        {
            stack.Children.Add(Section("Models · last 7 days"));
            stack.Children.Add(ShareList.Build(r.Models.Select(x => (x.Name, x.Share, Ui.Tokens(x.Total) + (x.Cost is { } c ? $" · {Ui.Money(c)}" : ""))), Palette.Lerp(accent, Theme.Current.Text, .25), reduced));
        }
        if (!compact && r.ModelWeeks is { Count: > 0 } weeks && weeks.Any(w => w.Models.Count > 0))
        {
            stack.Children.Add(Section("Model mix · last 8 weeks"));
            var chart = new ModelMixChart { Weeks = weeks, Accent = accent, Reduced = reduced };
            stack.Children.Add(chart);
            var legend = new WrapPanel { Margin = new Thickness(0, 6, 0, 8) };
            foreach (var (name, color) in chart.Legend())
                legend.Children.Add(Ui.Row(6, new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = Palette.Frozen(color), VerticalAlignment = VerticalAlignment.Center },
                    Ui.Text(name, 11.5, FontWeights.Normal, "Ui.Muted", false)).WithMargin(new Thickness(0, 0, 14, 4)));
            stack.Children.Add(legend);
        }
        if (r.AllDevices && (r.Lifetime is not null || r.PeakDaily is not null))
        {
            var lifetime = Ui.Text($"Lifetime {Ui.Tokens(r.Lifetime ?? 0)} tokens" + (r.PeakDaily is { } peak ? $" · busiest day {Ui.Tokens(peak)}" : "") + (r.LongestStreak is { } streak ? $" · longest streak {streak} days" : ""), 12, FontWeights.Normal, "Ui.Muted");
            lifetime.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Insert(1, lifetime);
        }
        var note = Ui.Text(r.AllDevices
            ? "Totals come from OpenAI for this account, across every device. Projects and models come from the Codex transcripts on this PC."
            : "From the transcripts Claude Code and Codex keep on this PC. API-price equivalent: what the same tokens would cost on the pay-as-you-go API, not what your subscription charges.", 11, FontWeights.Normal, "Ui.Faint");
        note.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(note);
        return stack;
    }

    private static Grid Fact(string label, string value, string note)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 10, 10) };
        grid.Children.Add(Ui.Inner(Ui.Stack(Ui.Text(label, 11.5, FontWeights.SemiBold, "Ui.Faint", false), Ui.Text(value, 22, FontWeights.Light, "Ui.Text", false), Ui.Text(note, 11.5, FontWeights.Normal, "Ui.Muted")), new Thickness(14, 10, 14, 12)));
        return grid;
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
