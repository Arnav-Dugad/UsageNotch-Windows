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
    public static Border Card(WeekReport r, IReadOnlyList<ProviderViewModel> items, AppSettings s, bool reduced, Action<TextBlock>? share = null)
    {
        var stack = new StackPanel();
        var title = Ui.Columns((Ui.Icon("", 15, "Ui.Muted", new Thickness(0, 0, 10, 0)), GridLength.Auto),
            (Ui.Text("This week", DesignTokens.TypeScale.Headline, FontWeights.SemiBold, "Ui.Text", false), new GridLength(1, GridUnitType.Star)),
            (Ui.Text(r.Summary, DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Faint", false), GridLength.Auto));
        title.Margin = new Thickness(0, 0, 0, 10);
        stack.Children.Add(title);
        var headline = Ui.Text(r.Headline, 24, FontWeights.Light); headline.Margin = new Thickness(0, 0, 0, 14);
        stack.Children.Add(headline);

        foreach (var row in r.Providers)
        {
            var p = items.FirstOrDefault(i => i.Id == row.Id);
            var accent = p?.AccentColor ?? Palette.Accent;
            var name = Ui.Stack(Ui.Text(row.Name, DesignTokens.TypeScale.Body, FontWeights.SemiBold, "Ui.Text", false),
                Ui.Text(row.Label + (row.Hits > 0 ? $" · limit reached {Times(row.Hits)}" : ""), DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Muted", false));
            var value = Ui.Text(Ui.Limits(row.Used), 22, FontWeights.Light, "Ui.Text", false); value.HorizontalAlignment = HorizontalAlignment.Right;
            var before = Ui.Text(row.Previous is { } pv ? $"{Ui.Limits(pv)} the week before" : row.Used is >= Ui.MultipleFrom ? "limits' worth" : "of one limit", DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Faint", false);
            before.HorizontalAlignment = HorizontalAlignment.Right;
            var numbers = Ui.Stack(value, before);
            UIElement change = row.Change is { } c
                ? Ui.Pill(Math.Abs(c) < .1 ? "Steady" : (c > 0 ? "▲ " : "▼ ") + (c >= 3 ? $"{c + 1:0.#}×" : $"{Math.Round(Math.Abs(c) * 100):0}%"), Math.Abs(c) < .1 ? Palette.Unknown : accent, 11)
                : new Border { Width = 0 };
            ((FrameworkElement)change).Margin = new Thickness(12, 0, 0, 0); ((FrameworkElement)change).VerticalAlignment = VerticalAlignment.Center;
            ((FrameworkElement)change).ToolTip = row.Change is null ? null : "Compared with the 7 days before, from the same daily totals as History";
            var line = Ui.Columns((Ui.LogoTile(row.Id, 34).WithMargin(new Thickness(0, 0, 12, 0)), GridLength.Auto), (name, new GridLength(1, GridUnitType.Star)), (numbers, GridLength.Auto), (change, GridLength.Auto));
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
    public static Border AllTime(AllTimeTotals a, LimitWindow window, Color accent)
    {
        var limit = WeeklyReport.Lower(window.Label);
        var tiles = new UniformGrid2(4);
        tiles.Add(Tile("Total", Ui.Limits(a.Used), a.Used >= Ui.MultipleFrom ? $"full {limit} limits' worth" : $"of one {limit} limit"));
        tiles.Add(Tile("Active days", a.ActiveDays.ToString("N0", CultureInfo.InvariantCulture), $"of {a.RecordedDays:N0} days with readings"));
        tiles.Add(Tile("Limits reached", a.Hits.ToString("N0", CultureInfo.InvariantCulture), a.Hits == 1 ? "time it hit 100%" : "times it hit 100%"));
        tiles.Add(Tile("Best day", a.BestDay is { } best ? best.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) : "—", a.BestDay is null ? "no usage yet" : $"{Ui.Limits(a.BestUsed)} of the limit"));
        var title = Ui.Columns((Ui.Icon("", 15, "Ui.Muted", new Thickness(0, 0, 10, 0)), GridLength.Auto),
            (Ui.Text("All time", DesignTokens.TypeScale.Headline, FontWeights.SemiBold, "Ui.Text", false), new GridLength(1, GridUnitType.Star)),
            (Ui.Text($"{window.Label} · since {a.Since.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}", DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Faint", false), GridLength.Auto));
        title.Margin = new Thickness(0, 0, 0, 12);
        var note = Ui.Text("Kept on this PC with no time limit until you delete it in Settings › Data.", DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Faint");
        note.Margin = new Thickness(0, 2, 0, 0);
        return Ui.Card(Ui.Stack(title, tiles.Panel, note), accent);
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
