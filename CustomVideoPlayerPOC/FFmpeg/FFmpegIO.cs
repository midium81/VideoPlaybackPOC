using CustomVideoPlayerPOC.Cache;
using FFmpeg.AutoGen;
using System.Runtime.InteropServices;

namespace CustomVideoPlayerPOC.FFmpeg
{
    public unsafe class FFmpegIO : IDisposable
    {
        // FFmpeg.AutoGen 6+ exposes the AVIO callbacks as function-pointer wrapper structs
        // instead of delegates, so we declare our own cdecl delegates and marshal them.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ReadPacketDelegate(void* opaque, byte* buf, int buf_size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long SeekDelegate(void* opaque, long offset, int whence);

        // The libc SEEK_* constants are no longer re-exported by the bindings.
        private const int SEEK_SET = 0;
        private const int SEEK_CUR = 1;
        private const int SEEK_END = 2;

        private readonly GrowingFileCache _cache;
        private readonly int _bufferSize = 32768;
        private readonly byte* _avioBuffer;
        private AVIOContext* _avioCtx;
        private readonly GCHandle _thisHandle;
        private readonly ReadPacketDelegate _readCallback;
        private readonly SeekDelegate _seekCallback;
        private long _currentPos = 0;
        private readonly long _fileSize;

        // Reused by the read callback so we do not allocate 32 KB per demux read.
        private readonly byte[] _scratch;

        // Lets Dispose() release a read that is parked waiting for bytes to be downloaded.
        private readonly CancellationTokenSource _cts = new();

        // Swapped out whenever a seek starts, so a read parked waiting for bytes at the *old*
        // position is released immediately instead of blocking the decode thread (and the
        // _stateLock it holds) until its 5s timeout elapses - which made a fresh seek issued while
        // a previous one was still waiting on data appear to freeze the player.
        private CancellationTokenSource _readGateCts = new();

        // While true, ReadPacket gives up (returns EOF) after a single short wait instead of
        // retrying forever. avformat_seek_file can issue several probe reads while it searches for
        // the target packet (e.g. binary/interpolation search on formats without a full index); if
        // even one of those lands outside the bytes we prioritized around the seek target, patient
        // indefinite retrying would block the seek - and the _stateLock it holds - forever, with
        // nothing to interrupt it since no further seek is guaranteed to come along. Regular
        // playback reads (outside a seek) keep retrying indefinitely, since those are fine to stall
        // on while the downloader catches up.
        private volatile bool _seekModeActive;

        public AVIOContext* AvioContext => _avioCtx;

        public FFmpegIO(GrowingFileCache cache, long fileSize)
        {
            _cache = cache;
            _fileSize = fileSize;
            _scratch = new byte[_bufferSize];
            _avioBuffer = (byte*)ffmpeg.av_malloc((ulong)_bufferSize);
            _thisHandle = GCHandle.Alloc(this);

            // Fields keep the delegates alive for as long as FFmpeg holds the pointers.
            _readCallback = ReadPacket;
            _seekCallback = Seek;

            _avioCtx = ffmpeg.avio_alloc_context(
                _avioBuffer,
                _bufferSize,
                0,
                (void*)GCHandle.ToIntPtr(_thisHandle),
                new avio_alloc_context_read_packet_func { Pointer = Marshal.GetFunctionPointerForDelegate(_readCallback) },
                default,
                new avio_alloc_context_seek_func { Pointer = Marshal.GetFunctionPointerForDelegate(_seekCallback) });
        }

        /// <summary>
        /// Releases a read that is currently parked waiting for bytes to be downloaded. Call this
        /// before starting a new seek so a stale wait cannot keep the demux lock held forever.
        /// </summary>
        public void InterruptPendingRead()
        {
            var old = Interlocked.Exchange(ref _readGateCts, new CancellationTokenSource());
            old.Cancel();
            old.Dispose();
        }

        /// <summary>
        /// Enables or disables seek mode. While active every read that times out returns EOF
        /// immediately instead of retrying indefinitely, so <c>avformat_seek_file</c>'s internal
        /// probing reads (keyframe/index search) cannot block <c>_stateLock</c> forever when they
        /// land outside the bytes that have already been downloaded.
        /// Regular playback reads (outside a seek) still retry patiently until data arrives.
        /// </summary>
        public void SetSeekMode(bool active) => _seekModeActive = active;

        private static int ReadPacket(void* opaque, byte* buf, int buf_size)
        {
            var handle = GCHandle.FromIntPtr((IntPtr)opaque);
            if (handle.Target is not FFmpegIO self) return ffmpeg.AVERROR_EOF;

            // Past the end of the media: this is the only genuine EOF.
            if (self._currentPos >= self._fileSize) return ffmpeg.AVERROR_EOF;

            // Never ask for bytes beyond the end of the file - that range can never become
            // available and the wait below would spin until the token is cancelled.
            var wanted = (int)Math.Min(Math.Min(buf_size, self._bufferSize), self._fileSize - self._currentPos);
            if (wanted <= 0) return ffmpeg.AVERROR_EOF;

            // Seek-mode uses a much shorter per-attempt timeout. If avformat_seek_file's probe
            // read doesn't find the bytes quickly we return EOF so the search gives up fast rather
            // than blocking the demux lock for potentially minutes. Normal playback reads keep the
            // full 5s window since the downloader's sequential prefetch will eventually reach them.
            var seekMode = self._seekModeActive;
            var timeout = seekMode ? TimeSpan.FromMilliseconds(750) : TimeSpan.FromSeconds(5);

            // The file is still being downloaded, so a missing range is not an error: park here
            // until the downloader delivers it. Returning EAGAIN/EOF instead would be recorded
            // permanently on the AVIOContext and no further read callback would ever be issued.
            while (true)
            {
                // Re-read the gate each iteration: InterruptPendingRead() swaps in a fresh source
                // when a seek starts, so a stale wait for the previous position is cancelled even
                // though this loop never sees the *disposal* token get signalled.
                var gate = Volatile.Read(ref self._readGateCts);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(self._cts.Token, gate.Token);

                try
                {
                    var read = self._cache.ReadAsync(self._scratch, self._currentPos, wanted, timeout, linked.Token)
                                          .GetAwaiter().GetResult();
                    if (read <= 0) return ffmpeg.AVERROR_EOF;

                    Marshal.Copy(self._scratch, 0, (IntPtr)buf, read);
                    self._currentPos += read;  
                    return read;
                }
                catch (TimeoutException)
                {
                    // Seek mode: data for this probe position is not available yet - fail fast so
                    // avformat_seek_file can either try a different strategy or return an error,
                    // which lets SeekTimeCore exit the lock quickly and unblocks the player.
                    if (self._seekModeActive) return ffmpeg.AVERROR_EOF;

                    // Normal playback: keep retrying; the downloader will arrive here eventually.
                }
                catch (OperationCanceledException)
                {
                    // Disposal: give up for good. A seek interrupt: return EOF so the decode
                    // thread's av_read_frame call returns and releases the state lock; the seek
                    // waiting on that lock will reposition _currentPos before the next read.
                    return ffmpeg.AVERROR_EOF;
                }
                catch
                {
                    return ffmpeg.AVERROR_EOF;
                }

                if (self._cts.IsCancellationRequested) return ffmpeg.AVERROR_EOF;
            }
        }

        private static long Seek(void* opaque, long offset, int whence)
        {
            var handle = GCHandle.FromIntPtr((IntPtr)opaque);
            if (handle.Target is not FFmpegIO self) return -1;

            // FFmpeg may OR in AVSEEK_FORCE; it is only a hint and must be masked off.
            whence &= ~ffmpeg.AVSEEK_FORCE;

            switch (whence)
            {
                case SEEK_SET:
                    self._currentPos = offset;
                    return self._currentPos;
                case SEEK_CUR:
                    self._currentPos += offset;
                    return self._currentPos;
                case SEEK_END:
                    self._currentPos = self._fileSize + offset;
                    return self._currentPos;
                case ffmpeg.AVSEEK_SIZE:
                    return self._fileSize;
                default:
                    return -1;
            }
        }

        public void Dispose()
        {
            // Release any read parked waiting for bytes that will never arrive.
            _cts.Cancel();
            _readGateCts.Cancel();

            if (_avioCtx != null)
            {
                ffmpeg.av_free(_avioCtx->buffer);

                var ctx = _avioCtx;
                ffmpeg.avio_context_free(&ctx);
                _avioCtx = null;
            }
            if (_thisHandle.IsAllocated) _thisHandle.Free();
            _cts.Dispose();
            _readGateCts.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}