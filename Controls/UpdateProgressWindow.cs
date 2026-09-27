using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UsageNotch.Services;

namespace UsageNotch.Controls;

public sealed class UpdateProgressWindow : Window
{
    private readonly TextBlock _stage = new() { Text = "Preparing a secure update…", Foreground = System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 18) };
    public UpdateProgressWindow()
    {
        Title = "Updating UsageNotch"; Width = 440; Height = 210; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(19, 24, 34));
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = "Updating UsageNotch", FontSize = 22, Foreground = System.Windows.Media.Brushes.White });
        body.Children.Add(_stage); body.Children.Add(new System.Windows.Controls.ProgressBar { Height = 5, IsIndeterminate = true });
        body.Children.Add(new TextBlock { Text = "Your settings and history are preserved.", FontSize = 11, Margin = new Thickness(0, 16, 0, 0), Foreground = System.Windows.Media.Brushes.LightGray });
        Content = body; SourceInitialized += (_, _) => WindowMaterials.Apply(this, transient: true);
        Closing += (_, e) => { if (!Finished) e.Cancel = true; };
    }
    public bool Finished { get; private set; }
    public void SetStage(string text) => _stage.Text = text;
    public new void Close() { Finished = true; base.Close(); }
}
