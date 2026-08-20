namespace CustomVideoPlayerPOC.Core
{
    public readonly struct ByteRange
    {
        public long Start { get; }
        public long End { get; } // inclusive
        public ByteRange(long start, long end) { Start = start; End = end; }
        public long Length => End - Start + 1;
        public bool Contains(long offset) => offset >= Start && offset <= End;
    }
}
