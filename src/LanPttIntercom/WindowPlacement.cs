using System.Drawing;

namespace LanPttIntercom;

internal static class WindowPlacement
{
    public static Rectangle NormalizeToVisibleWorkArea(
        Rectangle bounds,
        IEnumerable<Rectangle> workingAreas,
        Size minimumSize,
        Rectangle? preferredWorkingArea = null)
    {
        var areas = workingAreas.Where(area => area.Width > 0 && area.Height > 0).ToArray();
        if (areas.Length == 0)
        {
            return bounds;
        }

        var invalidOrTooSmall = bounds.IsEmpty ||
                                bounds.Width < minimumSize.Width ||
                                bounds.Height < minimumSize.Height;
        var width = Math.Max(Math.Max(bounds.Width, minimumSize.Width), 1);
        var height = Math.Max(Math.Max(bounds.Height, minimumSize.Height), 1);
        var normalized = new Rectangle(bounds.Location, new Size(width, height));
        var targetArea = invalidOrTooSmall
            ? ScreenFallbackWorkingArea(areas, preferredWorkingArea)
            : FindBestWorkingArea(normalized, areas, preferredWorkingArea);

        if (!invalidOrTooSmall && Intersects(normalized, targetArea))
        {
            return normalized;
        }

        width = Math.Min(normalized.Width, targetArea.Width);
        height = Math.Min(normalized.Height, targetArea.Height);
        var x = targetArea.Left + Math.Max(0, (targetArea.Width - width) / 2);
        var y = targetArea.Top + Math.Max(0, (targetArea.Height - height) / 2);
        return new Rectangle(x, y, width, height);
    }

    private static Rectangle FindBestWorkingArea(Rectangle bounds, Rectangle[] workingAreas, Rectangle? preferredWorkingArea)
    {
        var bestArea = workingAreas[0];
        var bestIntersectionArea = 0;

        foreach (var workingArea in workingAreas)
        {
            var intersection = Rectangle.Intersect(bounds, workingArea);
            var intersectionArea = intersection.Width * intersection.Height;
            if (intersectionArea > bestIntersectionArea)
            {
                bestArea = workingArea;
                bestIntersectionArea = intersectionArea;
            }
        }

        return bestIntersectionArea > 0 ? bestArea : ScreenFallbackWorkingArea(workingAreas, preferredWorkingArea);
    }

    private static Rectangle ScreenFallbackWorkingArea(Rectangle[] workingAreas, Rectangle? preferredWorkingArea = null)
    {
        if (preferredWorkingArea.HasValue)
        {
            var preferred = preferredWorkingArea.Value;
            foreach (var workingArea in workingAreas)
            {
                if (workingArea == preferred)
                {
                    return workingArea;
                }
            }
        }

        return workingAreas
            .OrderBy(area => Math.Abs(area.Left) + Math.Abs(area.Top))
            .ThenByDescending(area => area.Width * area.Height)
            .First();
    }

    private static bool Intersects(Rectangle bounds, Rectangle workingArea)
    {
        var intersection = Rectangle.Intersect(bounds, workingArea);
        return intersection.Width > 0 && intersection.Height > 0;
    }
}
