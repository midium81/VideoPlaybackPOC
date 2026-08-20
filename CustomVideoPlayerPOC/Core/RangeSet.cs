using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CustomVideoPlayerPOC.Core
{
    public class RangeSet
    {
        private readonly List<ByteRange> _ranges = new();
        private readonly object _lock = new();

        public void Add(ByteRange r)
        {
            lock (_lock)
            {
                _ranges.Add(r);
                _ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
                var merged = new List<ByteRange>();
                foreach (var cur in _ranges)
                {
                    if (merged.Count == 0) { merged.Add(cur); continue; }
                    var last = merged[^1];
                    if (cur.Start <= last.End + 1)
                    {
                        merged[^1] = new ByteRange(last.Start, Math.Max(last.End, cur.End));
                    }
                    else merged.Add(cur);
                }
                _ranges.Clear();
                _ranges.AddRange(merged);
            }
        }

        public bool IsRangeAvailable(long start, long length)
        {
            lock (_lock)
            {
                var end = start + length - 1;
                return _ranges.Any(r => r.Start <= start && r.End >= end);
            }
        }

        public IEnumerable<ByteRange> GetMissingRanges(long start, long length)
        {
            lock (_lock)
            {
                var end = start + length - 1;
                var missing = new List<ByteRange>();
                long cur = start;
                foreach (var r in _ranges)
                {
                    if (r.End < cur) continue;
                    if (r.Start > end) break;
                    if (r.Start > cur)
                    {
                        missing.Add(new ByteRange(cur, Math.Min(end, r.Start - 1)));
                    }
                    cur = Math.Max(cur, r.End + 1);
                    if (cur > end) break;
                }
                if (cur <= end) missing.Add(new ByteRange(cur, end));
                return missing;
            }
        }

        public IEnumerable<ByteRange> AllRanges
        {
            get { lock (_lock) return _ranges.ToArray(); }
        }

        /// <summary>Total number of distinct bytes held (ranges are kept merged, so no double counting).</summary>
        public long DownloadedBytes
        {
            get
            {
                lock (_lock)
                {
                    long total = 0;
                    foreach (var r in _ranges) total += r.Length;
                    return total;
                }
            }
        }

        /// <summary>True when [0, totalLength) is covered by a single contiguous range.</summary>
        public bool IsComplete(long totalLength)
            => totalLength > 0 && IsRangeAvailable(0, totalLength);

        /// <summary>First gap in [0, totalLength), or null when nothing is missing.</summary>
        public ByteRange? FirstMissing(long totalLength)
        {
            if (totalLength <= 0) return null;
            foreach (var r in GetMissingRanges(0, totalLength)) return r;
            return null;
        }
    }
}
