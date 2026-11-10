using System;
using System.Collections.Generic;
using System.Linq;
using GuidingAnalyzer.Models;

namespace GuidingAnalyzer.Services
{
    internal static class GuidingDataQuality
    {
        internal static bool IsUsable(GuidingPoint p) => p.IsGuideStep && !p.IsDitherOrSettling
            && double.IsFinite(p.RaError) && double.IsFinite(p.DecError);

        internal static List<List<GuidingPoint>> Segments(IEnumerable<GuidingPoint> source)
        {
            var points = source.ToList();
            var intervals = points.Zip(points.Skip(1), (a, b) => (b.Timestamp - a.Timestamp).TotalSeconds)
                .Where(t => t > 0).OrderBy(t => t).ToArray();
            double gap = intervals.Length == 0 ? double.PositiveInfinity : 3 * intervals[intervals.Length / 2];
            var result = new List<List<GuidingPoint>>();
            var current = new List<GuidingPoint>();
            foreach (var p in points)
            {
                if (!IsUsable(p) || (current.Count > 0 &&
                    (p.Timestamp <= current[^1].Timestamp || (p.Timestamp - current[^1].Timestamp).TotalSeconds > gap)))
                {
                    if (current.Count > 0) result.Add(current);
                    current = new();
                }
                if (IsUsable(p)) current.Add(p);
            }
            if (current.Count > 0) result.Add(current);
            return result;
        }

        // Evidence of a residual after an actual non-zero pulse reversal, not a backlash measurement.
        internal static (int Count, double MeanResidual) DecReversals(IEnumerable<GuidingPoint> points, double minimumResidual)
        {
            int count = 0; double sum = 0;
            foreach (var segment in Segments(points))
            {
                int lastDirection = 0;
                for (int i = 0; i < segment.Count; i++)
                {
                    double pulse = segment[i].DecCorrection;
                    if (!double.IsFinite(pulse) || pulse == 0) continue;
                    int direction = Math.Sign(pulse);
                    if (lastDirection != 0 && direction != lastDirection && Math.Abs(pulse) > 50 && i + 1 < segment.Count)
                    {
                        double residual = Math.Abs(segment[i + 1].DecError);
                        if (residual > minimumResidual) { count++; sum += residual; }
                    }
                    lastDirection = direction;
                }
            }
            return (count, count == 0 ? 0 : sum / count);
        }

        internal static bool BacklashCompensationEnabled(GuidingSessionInfo? info) =>
            info != null && new[] { "enabled", "true", "1" }.Contains(info.BacklashCompEnabled.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}
