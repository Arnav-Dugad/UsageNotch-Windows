using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UsageNotch.Services;

namespace UsageNotch.Controls;

/// <summary>
/// A short sheet shown once: a welcome on the first launch, or what changed after an update. Plain English, one idea per
/// line, and a way straight to the thing it describes.
/// </summary>
public sealed class WhatsNewWindow : Window
{
    public sealed record Item(string Glyph, string Title, string Text);

    /// <summary>What 2.8 adds, in the order people will notice it.</summary>
    public static readonly Item[] Release =
    [
        new("", "This week, in plain English", "History opens with your week: how it compares with the week before, limits you hit, and your busiest day and hours."),
        new("", "Share your week", "One click makes a story-sized image of your week, saves it to Pictures and copies it, ready to paste."),
        new("", "Kept until you delete it", "History now shows 7, 30 or 90 days, plus all-time totals since your first reading."),
        new("", "Forecast ranges", "\"Limit around 4:10 PM (likely 3:40–4:50 PM)\": the window you'll probably hit it in, not just one time."),
        new("", "Classic style and a live tray ring", "Prefer solid cards? Choose Classic in Appearance. The tray icon now shows your usage as a ring."),
        new("", "Official logos", "Gemini's new colours and the Codex mark, from the providers' published artwork."),
    ];

    public static readonly Item[] Welcome =
    [
        new("", "Your AI limits, always in view", "The dock on the edge of your screen shows a ring for each provider. Click a ring for its details and reset time."),
        new("", "Nothing to sign in to", "UsageNotch reads the sign-ins Claude Code, Codex, Gemini CLI and Cursor already keep on this PC. They never leave it."),
        new("", "History that keeps growing", "Every reading is kept on this PC until you delete it, so your weeks, streaks and all-time totals keep building."),
        new("", "Take it with you", "Pair the Android app in Settings › Phone to see the same rings and history on your phone."),
    ];

    public WhatsNewWindow(AppSettings settings, bool firstRun, Action? openHistory, Action? openSettings)
    {
        Title = firstRun ? "Welcome to UsageNotch" : "What's new in UsageNotch";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Theme.Apply(Resources, settings);
        SetResourceReference(BackgroundProperty, "Ui.Background");

        var version = typeof(WhatsNewWindow).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}" : "";
        var page = new StackPanel { Margin = new Thickness(32, 28, 32, 28) };
        var mark = new System.Windows.Controls.Image { Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Center };
        try { mark.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/UsageNotch;component/Assets/UsageNotch-logo.png")); } catch { }
        var heading = Ui.Row(14, mark, Ui.Stack(
            Ui.Text(firstRun ? "Welcome to UsageNotch" : $"What's new in {version}", DesignTokens.TypeScale.Display - 4, FontWeights.SemiBold, "Ui.Text", false),
            Ui.Text(firstRun ? "Your AI usage at a glance, on your desktop and your phone." : "Here's what changed. Everything you had is still here.", DesignTokens.TypeScale.Body, FontWeights.Normal, "Ui.Muted")));
        heading.Margin = new Thickness(0, 0, 0, 22);
        page.Children.Add(heading);

        var reduced = Motion.IsReduced(settings);
        var index = 0;
        foreach (var item in firstRun ? Welcome : Release)
        {
            var icon = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(12), Child = Ui.Icon(item.Glyph, 17, "Ui.Accent"), VerticalAlignment = VerticalAlignment.Top };
            icon.SetResourceReference(Border.BackgroundProperty, "Ui.Inner");
            ((FrameworkElement)icon.Child).HorizontalAlignment = System.Windows.HorizontalAlignment.Center; ((FrameworkElement)icon.Child).VerticalAlignment = VerticalAlignment.Center;
            var text = Ui.Stack(Ui.Text(item.Title, DesignTokens.TypeScale.Body + 1, FontWeights.SemiBold), Ui.Text(item.Text, DesignTokens.TypeScale.Callout, FontWeights.Normal, "Ui.Muted"));
            var row = Ui.Columns((icon.WithMargin(new Thickness(0, 0, 14, 0)), GridLength.Auto), (text, new GridLength(1, GridUnitType.Star)));
            row.Margin = new Thickness(0, 0, 0, 16);
            page.Children.Add(row);
            Motion.Rise(row, index++, reduced);
        }

        var primary = Ui.Button(firstRun ? "Get started" : "Continue", Close, true);
        var secondary = firstRun
            ? Ui.Button("Open Settings", () => { Close(); openSettings?.Invoke(); }, false, "")
            : Ui.Button("See your week", () => { Close(); openHistory?.Invoke(); }, false, "");
        secondary.Margin = new Thickness(0, 0, 10, 0);
        var buttons = Ui.Row(0, secondary, primary); buttons.HorizontalAlignment = System.Windows.HorizontalAlignment.Right; buttons.Margin = new Thickness(0, 10, 0, 0);
        page.Children.Add(buttons);

        var root = new Grid();
        root.Children.Add(new AmbientBackground { Animate = false, Colors = [] });
        root.Children.Add(page);
        Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter) { e.Handled = true; Close(); } };
        SourceInitialized += (_, _) => WindowMaterials.TitleBar(this, Theme.Current.Dark);
    }
}
