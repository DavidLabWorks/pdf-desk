using System.Numerics;

namespace PDFDesk.Engine;

internal sealed class InkEraser(Vector2[] trail, float radius)
{
    private readonly (Vector2 Start, Vector2 End)[] _segments = Enumerable.Range(0, Math.Max(1, trail.Length - 1))
        .Select(i => (trail[i], trail[Math.Min(i + 1, trail.Length - 1)])).ToArray();

    internal List<IReadOnlyList<(float X, float Y)>>? Trim(
        IReadOnlyList<IReadOnlyList<(float X, float Y)>> strokes, float inkWidth)
    {
        // Leave enough room for the round cap of each surviving fragment.
        float clearance = radius + inkWidth / 2;
        var result = new List<IReadOnlyList<(float X, float Y)>>();
        var overlaps = new List<(double Start, double End)>();
        bool changed = false;
        foreach (var stroke in strokes)
        {
            if (stroke.Count == 0)
            {
                continue;
            }
            int firstFragment = result.Count;
            bool strokeChanged = false;
            List<(float X, float Y)>? fragment = null;
            for (int i = 0; i < Math.Max(1, stroke.Count - 1); i++)
            {
                var a = stroke[i];
                var b = stroke[Math.Min(i + 1, stroke.Count - 1)];
                var start = new Vector2(a.X, a.Y);
                var end = new Vector2(b.X, b.Y);
                overlaps.Clear();
                foreach (var sweep in _segments)
                {
                    if (IntersectCapsule(start, end, sweep.Start, sweep.End, clearance) is { } interval)
                    {
                        overlaps.Add(interval);
                    }
                }
                overlaps.Sort();
                double keptFrom = 0;
                foreach (var (cutFrom, cutTo) in overlaps)
                {
                    if (cutFrom > keptFrom)
                    {
                        Keep(start, end, keptFrom, cutFrom);
                    }
                    FinishFragment();
                    keptFrom = Math.Max(keptFrom, cutTo);
                }
                if (keptFrom < 1)
                {
                    Keep(start, end, keptFrom, 1);
                }
                strokeChanged |= overlaps.Count != 0;
            }
            FinishFragment();
            if (!strokeChanged)
            {
                result[firstFragment] = stroke;
            }
            changed |= strokeChanged;

            void Keep(Vector2 start, Vector2 end, double from, double to)
            {
                var a = Vector2.Lerp(start, end, (float)from);
                var b = Vector2.Lerp(start, end, (float)to);
                fragment ??= [(a.X, a.Y)];
                fragment.Add((b.X, b.Y));
            }

            void FinishFragment()
            {
                if (fragment is not null)
                {
                    result.Add(fragment);
                    fragment = null;
                }
            }
        }
        return changed ? result : null;
    }

    // Clip the ink segment to a capsule, the union of two disks and the
    // rectangle between them. The intersection is one continuous interval.
    private static (double Start, double End)? IntersectCapsule(
        Vector2 start, Vector2 end, Vector2 a, Vector2 b, float radius)
    {
        var lower = Vector2.Min(a, b) - new Vector2(radius);
        var upper = Vector2.Max(a, b) + new Vector2(radius);
        var inkLower = Vector2.Min(start, end);
        var inkUpper = Vector2.Max(start, end);
        if (inkUpper.X < lower.X || inkLower.X > upper.X || inkUpper.Y < lower.Y || inkLower.Y > upper.Y)
        {
            return null;
        }

        double first = 1;
        double last = 0;
        var delta = end - start;
        var axis = b - a;
        double length = axis.Length();
        AddDisk(a);
        if (length > 0)
        {
            AddDisk(b);
            var direction = axis / (float)length;
            var normal = new Vector2(-direction.Y, direction.X);
            var offset = start - a;
            double from = 0, to = 1;
            if (Clip(Vector2.Dot(offset, direction), Vector2.Dot(delta, direction), 0, length, ref from, ref to)
                && Clip(Vector2.Dot(offset, normal), Vector2.Dot(delta, normal), -radius, radius, ref from, ref to))
            {
                Add(from, to);
            }
        }
        return first < last ? (first, last) : null;

        void Add(double from, double to)
        {
            from = Math.Max(from, 0);
            to = Math.Min(to, 1);
            if (from < to)
            {
                first = Math.Min(first, from);
                last = Math.Max(last, to);
            }
        }

        void AddDisk(Vector2 center)
        {
            var offset = start - center;
            double squaredLength = Vector2.Dot(delta, delta);
            double distance = Vector2.Dot(offset, offset) - (double)radius * radius;
            if (squaredLength == 0)
            {
                if (distance <= 0) Add(0, 1);
                return;
            }
            double projection = Vector2.Dot(offset, delta);
            double discriminant = projection * projection - squaredLength * distance;
            if (discriminant > 0)
            {
                double root = Math.Sqrt(discriminant);
                Add((-projection - root) / squaredLength, (-projection + root) / squaredLength);
            }
        }
    }

    private static bool Clip(double origin, double delta, double min, double max, ref double from, ref double to)
    {
        if (delta == 0)
        {
            return origin >= min && origin <= max;
        }
        double a = (min - origin) / delta;
        double b = (max - origin) / delta;
        from = Math.Max(from, Math.Min(a, b));
        to = Math.Min(to, Math.Max(a, b));
        return from < to;
    }
}
