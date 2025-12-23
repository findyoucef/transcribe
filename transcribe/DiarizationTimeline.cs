using System;
using System.Collections.Generic;
using System.Linq;

namespace Transcribe
{
    public sealed record DiarizationSegment(TimeSpan Start, TimeSpan End, string Speaker);

    public sealed class DiarizationTimeline
    {
        private readonly List<DiarizationSegment> segments = new();
        private readonly object sync = new();

        public void Add(DiarizationSegment segment)
        {
            lock (sync)
            {
                segments.Add(segment);
            }
        }

        public string? GetSpeakerLabel(TimeSpan start, TimeSpan end)
        {
            lock (sync)
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                DiarizationSegment? bestOverlap = null;
                double bestOverlapMs = 0;
                DiarizationSegment? bestSingle = null;
                double bestSingleMs = 0;

                foreach (var segment in segments)
                {
                    if (segment.End <= start || segment.Start >= end)
                    {
                        continue;
                    }

                    var overlapStart = segment.Start > start ? segment.Start : start;
                    var overlapEnd = segment.End < end ? segment.End : end;
                    if (overlapEnd <= overlapStart)
                    {
                        continue;
                    }

                    var overlap = (overlapEnd - overlapStart).TotalMilliseconds;
                    if (IsOverlapLabel(segment.Speaker))
                    {
                        if (overlap > bestOverlapMs)
                        {
                            bestOverlapMs = overlap;
                            bestOverlap = segment;
                        }
                    }
                    else if (overlap > bestSingleMs)
                    {
                        bestSingleMs = overlap;
                        bestSingle = segment;
                    }
                }

                if (bestOverlap != null)
                {
                    var spanMs = Math.Max(0, (end - start).TotalMilliseconds);
                    var minOverlapMs = Math.Max(300, spanMs * 0.4);
                    if (bestOverlapMs >= minOverlapMs)
                    {
                        return bestOverlap.Speaker;
                    }
                }

                if (bestSingle != null)
                {
                    return bestSingle.Speaker;
                }

                var last = segments.OrderByDescending(s => s.End).FirstOrDefault();
                if (last != null && start - last.End <= TimeSpan.FromSeconds(1))
                {
                    return last.Speaker;
                }

                return null;
            }
        }

        public IReadOnlyList<DiarizationSegment> Snapshot()
        {
            lock (sync)
            {
                return segments.ToList();
            }
        }

        private static bool IsOverlapLabel(string label)
        {
            return label.IndexOf('+', StringComparison.Ordinal) >= 0;
        }
    }
}
