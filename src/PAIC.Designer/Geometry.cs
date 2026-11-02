using PAIC.Controls;

namespace PAIC.Designer;

public enum ResizeHandle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }
public enum AlignKind { Left, CenterHorizontal, Right, Top, CenterVertical, Bottom }
public enum DistributeAxis { Horizontal, Vertical }
public enum LayerMove { ToFront, ToBack, Forward, Backward }

public static class ResizeMath
{
    /// <summary>Applies a drag of (dx, dy) on a handle to a rectangle. The opposite edge stays fixed; size never drops below the minimum.</summary>
    public static Rect Resize(Rect start, ResizeHandle handle, double dx, double dy, double minWidth = 8, double minHeight = 8, bool keepAspect = false)
    {
        double left = start.X, top = start.Y, right = start.Right, bottom = start.Bottom;
        var movesLeft = handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft;
        var movesRight = handle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight;
        var movesTop = handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight;
        var movesBottom = handle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight;

        if (movesLeft) left = Math.Min(left + dx, right - minWidth);
        if (movesRight) right = Math.Max(right + dx, left + minWidth);
        if (movesTop) top = Math.Min(top + dy, bottom - minHeight);
        if (movesBottom) bottom = Math.Max(bottom + dy, top + minHeight);

        if (keepAspect && start.Width > 0 && start.Height > 0 && (movesLeft || movesRight) && (movesTop || movesBottom))
        {
            var ratio = start.Width / start.Height;
            var w = right - left;
            var h = w / ratio;
            if (movesTop) top = bottom - h; else bottom = top + h;
        }
        return new Rect(left, top, right - left, bottom - top);
    }
}

public sealed record SnapResult(double Dx, double Dy, IReadOnlyList<double> VerticalGuides, IReadOnlyList<double> HorizontalGuides);

public static class SnapEngine
{
    /// <summary>
    /// Suggests an adjustment that aligns the proposed rectangle's edges or centre with other elements' edges/centres or the canvas,
    /// falling back to the grid for the left/top edge. Guides are the coordinates that matched.
    /// </summary>
    public static SnapResult Snap(Rect proposed, IEnumerable<Rect> others, double grid, double threshold, double canvasWidth, double canvasHeight)
    {
        var xs = new List<double> { 0, canvasWidth / 2, canvasWidth };
        var ys = new List<double> { 0, canvasHeight / 2, canvasHeight };
        foreach (var o in others)
        {
            xs.AddRange(new[] { o.X, o.X + o.Width / 2, o.Right });
            ys.AddRange(new[] { o.Y, o.Y + o.Height / 2, o.Bottom });
        }

        var (dx, gx) = Best(new[] { proposed.X, proposed.X + proposed.Width / 2, proposed.Right }, xs, threshold);
        var (dy, gy) = Best(new[] { proposed.Y, proposed.Y + proposed.Height / 2, proposed.Bottom }, ys, threshold);

        if (gx is null && grid > 0) dx = Math.Round(proposed.X / grid) * grid - proposed.X;
        if (gy is null && grid > 0) dy = Math.Round(proposed.Y / grid) * grid - proposed.Y;
        return new SnapResult(dx, dy, gx is null ? Array.Empty<double>() : new[] { gx.Value }, gy is null ? Array.Empty<double>() : new[] { gy.Value });
    }

    private static (double delta, double? guide) Best(double[] edges, List<double> targets, double threshold)
    {
        double best = double.MaxValue; double? guide = null; var delta = 0.0;
        foreach (var e in edges)
            foreach (var t in targets)
            {
                var d = t - e;
                if (Math.Abs(d) <= threshold && Math.Abs(d) < best) { best = Math.Abs(d); delta = d; guide = t; }
            }
        return (delta, guide);
    }
}
