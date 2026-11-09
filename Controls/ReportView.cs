using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UsageNotch.Models;
using UsageNotch.Services;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace UsageNotch.Controls;

/// <summary>"This week" and "All time", shared by History and Overview. Every number here can also be found on History.</summary>
public static class ReportView
{
    /// <summary>The weekly report: a headline, one row per provider against the week before, and the week in sentences.</summary>
    public static Border Card(WeekReport r, IReadOnlyList<ProviderViewModel> items, AppSettings s, bool reduced, Action<TextBlock>? share = null, UsageScore? score = null)
    {
        var stack = new StackPanel();
        var title = Ui.Columns((Ui.Icon("", 15, "Ui.Muted", new Thickness(0, 0, 10, 0)), GridLength.Auto),
            (Ui.Text("This week", DesignTokens.TypeScale.Headline, FontWeights.SemiBold, "Ui.Text", false), new GridLength(1, GridUnitType.Star)),
            (Ui.Text(r.Summary, DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Faint", false), GridLength.Auto));
        title.Margin = new Thickness(0, 0, 0, 10);
        stack.Children.Add(title);
        // The headline, with your usage score counting up beside it.
        var headline = Ui.Text(r.Headline, 24, FontWeights.Light);
        UIElement scoreView = new Border();
        if (score is not null)
        {
            var number = new CountUpText { Target = score.Value, Reduced = reduced, FontSize = 24, FontWeight = FontWeights.Light, FontFamily = Ui.Display, HorizontalAlignment = HorizontalAlignment.Right };
            number.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
            var caption = Ui.Text("usage score", DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Faint", false); caption.HorizontalAlignment = HorizontalAlignment.Right;
            scoreView = Ui.Stack(number, caption);
            ((FrameworkElement)scoreView).ToolTip = "The average of how much of each provider's weekly limit you've used. See Overview.";
        }
        var top = Ui.Columns((headline, new GridLength(1, GridUnitType.Star)), (scoreView, GridLength.Auto)); top.Margin = new Thickness(0, 0, 0, r.Note is null ? 14 : 6);
        stack.Children.Add(top);
        if (r.Note is { } note)
        {
            var info = Ui.Columns((Ui.Icon("\uE946", 13, "Ui.Faint", new Thickness(0, 1, 8, 0)), GridLength.Auto), (Ui.Text(note, DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Faint"), new GridLength(1, GridUnitType.Star)));
            info.Margin = new Thickness(0, 0, 0, 14);
            stack.Children.Add(info);
        }

        var index = 0;
        foreach (var row in r.Providers)
        {
            var p = items.FirstOrDefault(i => i.Id == row.Id);
            var accent = p?.AccentColor ?? Palette.Accent;
            var delay = TimeSpan.FromMilliseconds(160 * index++);
            // A ring per provider: this week against your typical week (a full ring), or the week before when there's no typical week yet.
            var basis = row.Typical is >= .05 ? row.Typical : row.Previous is >= .05 ? row.Previous : null;
            FrameworkElement badge;
            if (basis is { } b && row.Used is { } u)
            {
                var ring = new ArcRing { Width = 40, Height = 40, Value = u / b, Ink = accent, Thickness = 4.5, Delay = delay, Reduced = reduced };
                var mark = Ui.Logo(row.Id, 16, s.ColoredLogos); mark.HorizontalAlignment = HorizontalAlignment.Center; mark.VerticalAlignment = VerticalAlignment.Center;
                var g = new Grid { Width = 40, Height = 40 }; g.Children.Add(ring); g.Children.Add(mark);
                g.ToolTip = row.Typical is >= .05 ? $"This week is {Math.Round(u / b * 100):0}% of your typical week (a full ring)" : $"This week is {Math.Round(u / b * 100):0}% of the week before (a full ring)";
                badge = g;
            }
            else badge = Ui.LogoTile(row.Id, 40);
            badge.Margin = new Thickness(0, 0, 12, 0); badge.VerticalAlignment = VerticalAlignment.Center;
            var details = row.Label + (row.Hits > 0 ? $" · limit reached {Times(row.Hits)}" : "");
            var name = Ui.Stack(Ui.Text(row.Name, DesignTokens.TypeScale.Body, FontWeights.SemiBold, "Ui.Text", false), Ui.Text(details, DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Muted", false));
            if (row.Typical is { } typical) name.Children.Add(Ui.Text($"Typical week: {Ui.Limits(typical)}", DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Faint", false));
            var value = new CountUpText { Target = row.Used ?? 0, Format = v => row.Used is null ? "—" : Ui.Limits(v), Delay = delay, Reduced = reduced, FontSize = 22, FontWeight = FontWeights.Light, FontFamily = Ui.Display, HorizontalAlignment = HorizontalAlignment.Right };
            value.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
            var before = Ui.Text(row.Previous is { } pv ? $"{Ui.Limits(pv)} the week before" : row.Used is >= Ui.MultipleFrom ? "limits' worth" : "of one limit", DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Faint", false);
            before.HorizontalAlignment = HorizontalAlignment.Right;
            var numbers = Ui.Stack(value, before); numbers.VerticalAlignment = VerticalAlignment.Center;
            UIElement change = row.Change is { } c
                ? Ui.Pill(Math.Abs(c) < .1 ? "Steady" : (c > 0 ? "▲ " : "▼ ") + (c >= 3 ? $"{c + 1:0.#}×" : $"{Math.Round(Math.Abs(c) * 100):0}%"), Math.Abs(c) < .1 ? Palette.Unknown : accent, 11)
                : new Border { Width = 0 };
            ((FrameworkElement)change).Margin = new Thickness(12, 0, 0, 0); ((FrameworkElement)change).VerticalAlignment = VerticalAlignment.Center;
            ((FrameworkElement)change).ToolTip = row.Change is null ? null : "Compared with the 7 days before, from the same daily totals as History";
            var line = Ui.Columns((badge, GridLength.Auto), (name, new GridLength(1, GridUnitType.Star)), (numbers, GridLength.Auto), (change, GridLength.Auto));
            var inner = Ui.Inner(line, new Thickness(12, 10, 14, 10)); inner.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(inner);
        }

        if (r.Insights.Count > 0)
        {
            var list = new StackPanel { Margin = new Thickness(2, 8, 0, 0) };
            foreach (var i in r.Insights)
            {
                var color = items.FirstOrDefault(p => p.Id == i.Provider)?.AccentColor ?? Accents.FromArgb(DesignTokens.Live);
                var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = Palette.Frozen(color), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 12, 0) };
                var sentence = Ui.Text(i.Text, DesignTokens.TypeScale.Body, FontWeights.Normal, "Ui.Text");
                var row = Ui.Columns((dot, GridLength.Auto), (sentence, new GridLength(1, GridUnitType.Star))); row.Margin = new Thickness(0, 0, 0, 8);
                list.Children.Add(row);
            }
            stack.Children.Add(list);
        }

        if (r.Notes is { Count: > 0 } weekNotes)
        {
            var label = Ui.Text("Your notes", DesignTokens.TypeScale.Caption, FontWeights.SemiBold, "Ui.Muted", false); label.Margin = new Thickness(2, 6, 0, 6);
            stack.Children.Add(label);
            foreach (var n in weekNotes)
            {
                var row = Ui.Columns((Ui.Text(n.Date.ToString("ddd", CultureInfo.InvariantCulture), DesignTokens.TypeScale.Callout, FontWeights.SemiBold, "Ui.Muted", false).WithMargin(new Thickness(2, 0, 12, 0)), GridLength.Auto),
                    (Ui.Text(n.Text, DesignTokens.TypeScale.Body, FontWeights.Normal, "Ui.Text"), new GridLength(1, GridUnitType.Star)));
                row.Margin = new Thickness(0, 0, 0, 6);
                stack.Children.Add(row);
            }
        }

        if (share is not null)
        {
            var status = Ui.Text("", DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Muted"); status.Margin = new Thickness(12, 0, 0, 0); status.VerticalAlignment = VerticalAlignment.Center;
            var button = Ui.Button("Share your week", () => share(status), false, "");
            var footer = Ui.Columns((button, GridLength.Auto), (status, new GridLength(1, GridUnitType.Star)));
            footer.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(footer);
        }
        return Ui.Card(stack, items.FirstOrDefault()?.AccentColor);
    }

    /// <summary>Everything this PC has recorded for one limit, with no time limit.</summary>
    public static Border AllTime(AllTimeTotals a, LimitWindow window, Color accent, IReadOnlyList<PlanChange>? plans = null, bool use24 = false)
    {
        var limit = WeeklyReport.Lower(window.Label);
        var tiles = new UniformGrid2(4);
        tiles.Add(Tile("Total", Ui.Limits(a.Used), a.Used >= Ui.MultipleFrom ? $"full {limit} limits' worth" : $"of one {limit} limit"));
        tiles.Add(Tile("Active days", a.ActiveDays.ToString("N0", CultureInfo.InvariantCulture), $"of {a.RecordedDays:N0} recorded day{(a.RecordedDays == 1 ? "" : "s")}"));
        tiles.Add(Tile("Limits reached", a.Hits.ToString("N0", CultureInfo.InvariantCulture), a.Hits == 1 ? "time it hit 100%" : "times it hit 100%"));
        tiles.Add(Tile("Best day", a.BestDay is { } best ? best.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) : "—", a.BestDay is null ? "no usage yet" : $"{Ui.Limits(a.BestUsed)} of the limit"));
        var title = Ui.Columns((Ui.Icon("", 15, "Ui.Muted", new Thickness(0, 0, 10, 0)), GridLength.Auto),
            (Ui.Text("All time", DesignTokens.TypeScale.Headline, FontWeights.SemiBold, "Ui.Text", false), new GridLength(1, GridUnitType.Star)),
            (Ui.Text($"{window.Label} · since {a.Since.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}", DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Faint", false), GridLength.Auto));
        title.Margin = new Thickness(0, 0, 0, 12);
        var note = Ui.Text("Kept on this PC with no time limit until you delete it in Settings › Data.", DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Faint");
        note.Margin = new Thickness(0, 2, 0, 0);
        var stack = Ui.Stack(title, tiles.Panel);
        if (plans is { Count: > 0 } && PlanTimeline.Build(plans, accent, use24) is { } timeline)
        {
            var label = Ui.Text("Plan history", DesignTokens.TypeScale.Caption, FontWeights.SemiBold, "Ui.Muted", false); label.Margin = new Thickness(0, 6, 0, 10);
            stack.Children.Add(label); stack.Children.Add(timeline);
            note.Margin = new Thickness(0, 12, 0, 0);
        }
        stack.Children.Add(note);
        return Ui.Card(stack, accent);
    }

    private static Grid Tile(string label, string value, string note)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 10, 10) };
        g.Children.Add(Ui.Inner(Ui.Stack(Ui.Text(label, DesignTokens.TypeScale.Caption, FontWeights.SemiBold, "Ui.Faint", false),
            Ui.Text(value, 22, FontWeights.Light, "Ui.Text", false), Ui.Text(note, 11, FontWeights.Normal, "Ui.Muted", false)), new Thickness(14, 10, 14, 12)));
        return g;
    }

    private static string Times(int n) => n switch { 1 => "once", 2 => "twice", _ => $"{n} times" };
}
