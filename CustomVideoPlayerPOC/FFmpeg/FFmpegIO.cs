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

            var ct = self._cts.Token;
            var timeout = TimeSpan.FromSeconds(5);

            // The file is still being downloaded, so a missing range is not an error: park here
            // until the downloader delivers it. Returning EAGAIN/EOF instead would be recorded
            // permanently on the AVIOContext and no further read callback would ever be issued.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var read = self._cache.ReadAsync(self._scratch, self._currentPos, wanted, timeout, ct)
                                          .GetAwaiter().GetResult();
                    if (read <= 0) return ffmpeg.AVERROR_EOF;

                    Marshal.Copy(self._scratch, 0, (IntPtr)buf, read);
                    self._currentPos += read;
                    return read;
                }
                catch (TimeoutException)
                {
                    // Data has not arrived yet - keep waiting rather than failing the stream.
                }
                catch (OperationCanceledException)
                {
                    return ffmpeg.AVERROR_EOF;
                }
                catch
                {
                    return ffmpeg.AVERROR_EOF;
                }
            }

            return ffmpeg.AVERROR_EOF;
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

            if (_avioCtx != null)
            {
                ffmpeg.av_free(_avioCtx->buffer);

                var ctx = _avioCtx;
                ffmpeg.avio_context_free(&ctx);
                _avioCtx = null;
            }
            if (_thisHandle.IsAllocated) _thisHandle.Free();
            _cts.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}