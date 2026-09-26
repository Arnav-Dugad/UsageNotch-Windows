using System.Windows;
using Point = System.Windows.Point;

namespace UsageNotch.Services;

public enum DockEdge { Left, Right, Top }

public readonly record struct DockIntentResult(DockEdge DropEdge, double Pull, DockEdge PreviewEdge, double Preview);

/// <summary>Short, bounded pointer-velocity prediction. Visual intent never expands the drop zone.</summary>
public sealed class DockIntent
{
    public const double MagnetRange = 150;
    private Point? _previous;
    private Vector _velocity;
    private DockEdge? _previewEdge;

    public void Reset() { _previous = null; _velocity = default; _previewEdge = null; }

    public DockIntentResult Update(Rect dock, Rect area, double seconds, bool snap, bool predict)
    {
        var point = dock.TopLeft;
        var dt = Math.Clamp(seconds, .001, .1);
        if (_previous is { } previous)
        {
            var velocity = (point - previous) / dt;
            // A monitor/DPI jump must not create an enormous look-ahead distance.
            velocity.X = Math.Clamp(velocity.X, -2400, 2400);
            velocity.Y = Math.Clamp(velocity.Y, -2400, 2400);
            _velocity += (velocity - _velocity) * (1 - Math.Exp(-dt / .065));
        }
        _previous = point;
        var gaps = new[] { dock.Left - area.Left, area.Right - dock.Right, dock.Top - area.Top };
        var actual = Array.IndexOf(gaps, gaps.Min());
        if (!snap) { _previewEdge = null; return new((DockEdge)actual, 0, (DockEdge)actual, 0); }
        var pull = Strength(gaps[actual], MagnetRange);
        if (!predict) return new((DockEdge)actual, pull, (DockEdge)actual, pull);

        var toward = new[] { -_velocity.X, _velocity.X, -_velocity.Y };
        var projected = gaps.Select((gap, i) => gap - Math.Clamp(toward[i] * .16, -70, 110)).ToArray();
        var candidate = Array.IndexOf(projected, projected.Min());
        // Keep the previous intent through small corner jitters; deliberate turns still win.
        if (_previewEdge is { } old && projected[(int)old] <= projected[candidate] + 24) candidate = (int)old;
        _previewEdge = (DockEdge)candidate;
        return new((DockEdge)actual, pull, (DockEdge)candidate, Strength(projected[candidate], 190));
    }

    public static double Strength(double gap, double range = MagnetRange)
    {
        var value = Math.Clamp(1 - Math.Max(0, gap) / range, 0, 1);
        return value * value * (3 - 2 * value);
    }

    // Exponential settling is time-based, not tied to a particular display refresh rate.
    public static double Settle(double current, double target, double seconds, double timeConstant = .065) =>
        target + (current - target) * Math.Exp(-Math.Max(0, seconds) / timeConstant);
}
