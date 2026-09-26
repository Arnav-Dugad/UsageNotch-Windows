using System.Windows;
using UsageNotch.Controls;
using UsageNotch.Services;

internal static class DragChecks
{
    public static void Run()
    {
        var area = new Rect(0, 0, 1920, 1080);
        var predictor = new DockIntent();
        DockIntentResult result = default;
        for (var i = 0; i <= 12; i++)
            result = predictor.Update(new Rect(1400 + i * 22, 350, 76, 330), area, 1d / 60, true, true);
        if (result.PreviewEdge != DockEdge.Right || result.Preview < .45 || result.Pull != 0)
            throw new Exception("Prediction did not reach early, or enlarged the actual snap zone.");
        var early = result.Preview;
        for (var i = 0; i < 90; i++) result = predictor.Update(new Rect(1664, 350, 76, 330), area, 1d / 60, true, true);
        if (result.Preview >= early / 2) throw new Exception("Prediction did not relax when movement stopped.");
        for (var i = 0; i < 12; i++) result = predictor.Update(new Rect(1664 - i * 15, 350 - i * 20, 76, 330), area, 1d / 60, true, true);
        if (result.PreviewEdge != DockEdge.Top) throw new Exception("Direction reversal did not choose the top edge.");
        result = predictor.Update(new Rect(8, 300, 76, 330), area, .016, false, true);
        if (result.Pull != 0 || result.Preview != 0) throw new Exception("Disabled magnet still previews or snaps.");
        result = predictor.Update(new Rect(8, 300, 76, 330), area, .016, true, false);
        if (result.Preview != result.Pull || result.DropEdge != DockEdge.Left || result.Pull < .9)
            throw new Exception("Non-predictive mode changed the actual magnet.");

        predictor.Reset();
        predictor.Update(new Rect(80, 85, 76, 330), area, .016, true, true);
        result = predictor.Update(new Rect(82, 81, 76, 330), area, .016, true, true);
        if (result.PreviewEdge != DockEdge.Left) throw new Exception("Corner jitter flipped visual intent.");
        var secondMonitor = new Rect(-1920, -200, 1920, 1080);
        result = predictor.Update(new Rect(-1914, 100, 76, 330), secondMonitor, .2, true, true);
        if (result.DropEdge != DockEdge.Left || !double.IsFinite(result.Preview) || result.Preview is < 0 or > 1)
            throw new Exception("Negative monitor coordinates or a long frame corrupted drag intent.");
        predictor.Reset();
        result = predictor.Update(new Rect(700, 400, 76, 330), area, .016, true, true);
        if (result.Preview != 0 || result.Pull != 0) throw new Exception("Previous gesture velocity leaked into a new drag.");

        foreach (var fps in new[] { 30, 60, 90, 120, 144, 240 })
        {
            var value = 0d;
            for (var i = 0; i < fps / 2; i++) value = DockIntent.Settle(value, 1, 1d / fps);
            if (Math.Abs(value - (1 - Math.Exp(-.5 / .065))) > 1e-8)
                throw new Exception("Shape settling changed with display refresh rate.");
            var previous = value;
            for (var i = 0; i < fps; i++)
            {
                value = DockIntent.Settle(value, 0, 1d / fps);
                if (value < 0 || value > previous) throw new Exception("Shape reversal overshot its bounds.");
                previous = value;
            }
        }
        var shapes = 0;
        foreach (var top in new[] { false, true })
        foreach (var left in new[] { false, true })
        foreach (var morph in new[] { 0d, .2, .5, .8, 1 })
        foreach (var reach in new[] { new Vector(), new Vector(-1, 0), new Vector(1, 0), new Vector(0, -1), new Vector(.5, -.5) })
        foreach (var scale in new[] { .75, 1d, 1.5 })
        {
            var w = (top ? 380 : 76) * scale;
            var h = (top ? 90 : 380) * scale;
            var surface = new DockSurface { TopEdge = top, LeftEdge = left, Morph = morph, Reach = reach, ShapeScale = scale };
            surface.Measure(new Size(w, h));
            surface.Arrange(new Rect(0, 0, w, h));
            var bounds = surface.Outline!.Bounds;
            if (bounds.Left < -.01 || bounds.Top < -.01 || bounds.Right > w + .01 || bounds.Bottom > h + .01 ||
                !surface.Outline.FillContains(new Point(w / 2, h / 2)))
                throw new Exception("Anticipation shape escaped the window or lost its centre hit area.");
            shapes++;
        }
        Console.WriteLine($"PASS: predictive approach, stop, reversal, corner stability, snap independence, negative monitors, gesture reset, 6 refresh-rate timelines and {shapes} bounded drag shapes.");
    }
}
