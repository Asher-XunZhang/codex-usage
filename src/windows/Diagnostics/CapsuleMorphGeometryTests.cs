using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Windows;

namespace CodexUsage;

internal static class CapsuleMorphGeometryTests
{
    // Pure geometry: no application, HWND, render target, account or input is needed.
    public static JsonObject Run()
    {
        var checks = new JsonArray();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Capsule morph geometry: " + message);
            checks.Add(message);
        }
        bool Near(double a, double b) => Math.Abs(a - b) < .000001;
        bool Same(Rect a, Rect b) => !a.IsEmpty && !b.IsEmpty && Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Width, b.Width) && Near(a.Height, b.Height);
        var compact = new Rect(700, 300, 76, 76);
        var panel = new Rect(440, 300, 336, 410);
        var first = CapsuleMorph.Frame(compact, panel, 0); var last = CapsuleMorph.Frame(compact, panel, 1);
        Check(Same(first.Shape, compact) && Near(first.Radius, 38) && Near(first.PrimaryFontSize, 18)
            && first.ArcAlpha == 1 && first.SecondaryAlpha == 1 && first.DetailsAlpha == 0, "compact endpoint retains the circle and its outgoing content");
        Check(Same(last.Shape, panel) && Near(last.Radius, 22) && Near(last.PrimaryFontSize, 20)
            && last.ArcAlpha == 0 && last.SecondaryAlpha == 0 && last.DetailsAlpha == 1, "expanded endpoint contains only the panel content");
        Check(Same(last.PrimaryBounds, new Rect(panel.Left + 20, panel.Top + 12, 64, 30))
            && last.PrimaryCenter == new Point(panel.Left + 52, panel.Top + 27), "expanded primary text keeps the agreed header position");
        Check(first.PrimaryBounds.Width == 64 && first.PrimaryBounds.Height == 30
            && CapsuleMorph.FullyContainsRounded(first.PrimaryBounds, first.Shape, first.Radius), "the initial complete percentage box fits inside the circle without shrinking its font");
        Check(CapsuleMorph.ExpandMilliseconds == 280 && CapsuleMorph.CollapseMilliseconds == 220, "host duration constants match the reviewed preview");
        var outgoingGone = CapsuleMorph.Frame(compact, panel, .12);
        Check(Same(outgoingGone.Shape, compact) && outgoingGone.ArcAlpha == 0 && outgoingGone.SecondaryAlpha == 0
            && outgoingGone.DetailsAlpha == 0, "outgoing arc and labels disappear before the shape starts changing");
        var widthDone = CapsuleMorph.Frame(compact, panel, .76);
        Check(Near(widthDone.Shape.Width, panel.Width) && widthDone.Shape.Height < panel.Height && widthDone.DetailsAlpha == 0,
            "horizontal sides finish slightly before vertical sides while details remain hidden");
        var shapeDone = CapsuleMorph.Frame(compact, panel, .84);
        Check(Same(shapeDone.Shape, panel) && shapeDone.DetailsAlpha == 0, "the entire panel is open before details fade in");
        Check(CapsuleMorph.Frame(compact, panel, .86).DetailsAlpha == 0
            && Near(CapsuleMorph.Frame(compact, panel, .93).DetailsAlpha, .5), "details follow the preview's delayed smooth fade");
        Check(Same(CapsuleMorph.Frame(compact, panel, -1).Shape, compact)
            && Same(CapsuleMorph.Frame(compact, panel, 2).Shape, panel)
            && Same(CapsuleMorph.Frame(compact, panel, double.NaN).Shape, compact), "invalid or out of range progress has a stable endpoint");

        var circle = new Rect(0, 0, 76, 76);
        var safe = CapsuleMorph.SafeText(new(0, 0, 64, 30), circle, 38);
        Check(!safe.IsEmpty && safe.Width == 64 && safe.Height == 30, "SafeText moves a complete box away from rounded corners without changing its size");
        bool CircleCornerSafe(Point p) => Math.Pow(p.X - 38, 2) + Math.Pow(p.Y - 38, 2) <= 36 * 36 + .000001;
        Check(CircleCornerSafe(safe.TopLeft) && CircleCornerSafe(safe.TopRight) && CircleCornerSafe(safe.BottomLeft) && CircleCornerSafe(safe.BottomRight),
            "safe initial glyph corners satisfy the independent inset-circle distance equation");
        Check(Same(CapsuleMorph.SafeText(safe, circle, 38), safe), "already safe text is not unnecessarily repositioned");
        Check(CapsuleMorph.SafeText(new(0, 0, 76, 30), circle, 38).IsEmpty,
            "text wider than the inset circle is deferred instead of clipped");
        Check(CapsuleMorph.SafeText(new(0, 0, 40, 66), circle, 38).IsEmpty,
            "a box whose diagonal cannot fit the circle is rejected even if its axis sizes fit");
        Check(CapsuleMorph.FullyContainsRounded(new(2, 2, 72, 72), circle, 0)
            && !CapsuleMorph.FullyContainsRounded(new(1, 2, 72, 72), circle, 0), "the text safety margin applies to straight edges as well as corners");
        Check(!CapsuleMorph.FullyContainsRounded(new(2, 2, 8, 8), circle, 38), "axis containment alone cannot admit text in a transparent rounded corner");
        Check(CapsuleMorph.SafeText(Rect.Empty, circle, 38).IsEmpty
            && CapsuleMorph.SafeText(new(0, 0, 1, 1), Rect.Empty, 38).IsEmpty
            && CapsuleMorph.SafeText(new(0, 0, 1, 1), circle, double.NaN).IsEmpty
            && CapsuleMorph.Frame(Rect.Empty, panel, .5).Shape.IsEmpty, "unavailable geometry never produces invalid drawing coordinates");

        var cases = new List<(string Name, Rect Compact, Rect Panel)>
        {
            ("left-down", compact, new(440, 300, 336, 410)),
            ("right-down", compact, new(700, 300, 336, 410)),
            ("left-up", compact, new(440, -34, 336, 410)),
            ("right-up", compact, new(700, -34, 336, 410)),
            ("top-left-inset", new(0, 0, 76, 76), new(12, 12, 336, 410)),
            ("top-right-inset", new(1844, 0, 76, 76), new(1572, 12, 336, 410)),
            ("bottom-left-inset", new(0, 964, 76, 76), new(12, 618, 336, 410)),
            ("bottom-right-inset", new(1844, 964, 76, 76), new(1572, 618, 336, 410))
        };
        int frames = 0;
        foreach (var test in cases)
            foreach (Point offset in new[] { new Point(), new Point(-1920, -1080), new Point(2560, 720) })
            {
                var source = test.Compact; source.Offset(offset.X, offset.Y);
                var destination = test.Panel; destination.Offset(offset.X, offset.Y);
                var envelope = Rect.Union(source, destination); var previous = CapsuleMorph.Frame(source, destination, 0);
                for (int i = 0; i <= 200; i++)
                {
                    double p = i / 200d; var frame = CapsuleMorph.Frame(source, destination, p);
                    if (frame.Shape.Left < envelope.Left - .000001 || frame.Shape.Right > envelope.Right + .000001
                        || frame.Shape.Top < envelope.Top - .000001 || frame.Shape.Bottom > envelope.Bottom + .000001)
                        throw new InvalidOperationException($"Capsule morph leaves fixed envelope: {test.Name}, offset={offset}, p={p}");
                    if (frame.PrimaryBounds.IsEmpty || frame.PrimaryBounds.Width != 64 || frame.PrimaryBounds.Height != 30
                        || !CapsuleMorph.FullyContainsRounded(frame.PrimaryBounds, frame.Shape, frame.Radius))
                        throw new InvalidOperationException($"Capsule morph clips percentage: {test.Name}, offset={offset}, p={p}");
                    if (frame.DetailsAlpha > 0 && !Same(frame.Shape, destination))
                        throw new InvalidOperationException($"Capsule morph reveals details before they fit: {test.Name}, p={p}");
                    if (frame.Shape.Width < previous.Shape.Width - .000001 || frame.Shape.Height < previous.Shape.Height - .000001
                        || frame.ArcAlpha > previous.ArcAlpha + .000001 || frame.DetailsAlpha < previous.DetailsAlpha - .000001)
                        throw new InvalidOperationException($"Capsule morph reverses unexpectedly: {test.Name}, p={p}");
                    previous = frame; frames++;
                }
            }
        checks.Add($"{frames} sampled frames across four expansion directions, four inset corners and three screen origins keep the entire percentage box inside the shape and the shape inside its fixed envelope");
        int fractionalCases = 0;
        foreach (double origin in new[] { -11.366888992687974, -11.366888992687972, -76d / 3, -1d / 7, 1d / 3, 1920d + 1d / 7 })
            for (int i = 0; i <= 512; i++)
            {
                double x = origin - i / 7d, y = origin + i / 11d;
                var movingCircle = new Rect(x, y, 76, 76);
                var insetSafe = CapsuleMorph.SafeText(new(x + 2, y + 4, 64, 30), movingCircle, 38);
                if (insetSafe.IsEmpty || !CapsuleMorph.FullyContainsRounded(insetSafe, movingCircle, 38))
                    throw new InvalidOperationException($"Capsule morph fractional circle text: x={x:R}, y={y:R}");
                // AnimateEdge uses the current moving circle as both compact and panel
                // at p=0; the ordinary morph also starts from fractional screen origins.
                foreach (double p in new[] { 0d, .001, .14, .141, .5, 1 })
                {
                    var destination = p == 0 ? movingCircle : new Rect(x - 260, y, 336, 410);
                    var frame = CapsuleMorph.Frame(movingCircle, destination, p);
                    if (frame.PrimaryBounds.IsEmpty || !CapsuleMorph.FullyContainsRounded(frame.PrimaryBounds, frame.Shape, frame.Radius))
                        throw new InvalidOperationException($"Capsule morph fractional frame: x={x:R}, y={y:R}, p={p}");
                    fractionalCases++;
                }
            }
        checks.Add($"{fractionalCases} fractional-origin frames, including the reported negative circle coordinate, keep ordered corner-center bounds and complete text");
        return J.Obj(("success", true), ("checks", checks), ("sampledFrames", frames), ("fractionalFrames", fractionalCases));
    }
}
