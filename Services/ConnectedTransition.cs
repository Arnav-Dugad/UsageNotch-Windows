using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UsageNotch.Controls;

namespace UsageNotch.Services;

public static class ConnectedTransition
{
    public static void Play(Window owner, FrameworkElement destination, string provider, System.Windows.Point screenOrigin)
    {
        if (!destination.IsVisible || PresentationSource.FromVisual(destination)?.CompositionTarget is not { } composition) return;
        destination.UpdateLayout();
        var transform = composition.TransformFromDevice;
        var start = transform.Transform(screenOrigin);
        var end = transform.Transform(destination.PointToScreen(new System.Windows.Point(destination.ActualWidth / 2, destination.ActualHeight / 2)));
        var overlay = new Window { Width = 38, Height = 38, Left = start.X - 19, Top = start.Y - 19, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, IsHitTestVisible = false, Topmost = true, ResizeMode = ResizeMode.NoResize,
            Content = new ProviderLogo { Provider = provider, Colored = true, Margin = new Thickness(5) } };
        overlay.Show();
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var x = new DoubleAnimation(start.X - 19, end.X - 19, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease };
        var y = new DoubleAnimation(start.Y - 19, end.Y - 19, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease };
        x.Completed += (_, _) => overlay.Close();
        overlay.BeginAnimation(Window.LeftProperty, x); overlay.BeginAnimation(Window.TopProperty, y);
    }
}
