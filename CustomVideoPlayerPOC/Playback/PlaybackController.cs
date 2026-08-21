using CustomVideoPlayerPOC.Audio;
using CustomVideoPlayerPOC.Cache;
using CustomVideoPlayerPOC.Downloader;
using CustomVideoPlayerPOC.FFmpeg;
using FFmpeg.AutoGen;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CustomVideoPlayerPOC.Playback
{
    /// <summary>
    /// Demux/decode/render loop built on FFmpeg.AutoGen 9.x.
    /// Requires native FFmpeg shared libraries matching ffmpeg.LibraryVersionMap
    /// (avutil-61, avcodec-63, avformat-63, swscale-10, swresample-7).
    /// </summary>
    public class PlaybackController : IDisposable
    {
        // FFmpeg.AutoGen 6+ exposes AVCodecContext.get_format as a function-pointer wrapper
        // struct rather than a delegate, so we declare our own cdecl delegate and marshal it.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private unsafe delegate AVPixelFormat GetFormatDelegate(AVCodecContext* ctx, AVPixelFormat* pixFmts);

        // swscale flags are no longer exposed as constants by the bindings.
        private const int SWS_BILINEAR = 2;

        private readonly FFmpegIO _ffio;
        private readonly GrowingFileCache _cache;

        // Null when playing a fully retained local file - there is nothing left to download.
        private readonly RangeDownloader? _downloader;

        private unsafe AVFormatContext* _fmtCtx;
        private unsafe AVCodecContext* _videoDecCtx;
        private unsafe AVCodecContext* _audioDecCtx;
        private unsafe AVBufferRef* _hwDeviceCtx;
        private unsafe SwsContext* _swsCtx;

        // Cached scaler input description so we only rebuild the SwsContext when it changes.
        private int _swsSrcWidth;
        private int _swsSrcHeight;
        private AVPixelFormat _swsSrcFormat = AVPixelFormat.AV_PIX_FMT_NONE;

        private GetFormatDelegate? _getFormatCallback;

        private int _videoStreamIndex = -1;
        private int _audioStreamIndex = -1;

        private Task? _decodeTask;
        private CancellationTokenSource _cts = new();

        private readonly object _stateLock = new();
        private volatile bool _isPlaying;

        // double cannot be volatile; use Volatile.Read/Write for cross-thread visibility.
        private double _playbackRate = 1.0;

        private readonly WriteableBitmap _targetBitmap;
        private readonly int _targetWidth;
        private readonly int _targetHeight;

        private readonly NAudioPlayer _audioPlayer = new();

        private volatile bool _singleStepRequested;

        // AVStream.cur_dts / AVStream.pts are no longer public API; track position ourselves.
        private long _lastVideoPts = ffmpeg.AV_NOPTS_VALUE;
        private AVRational _videoTimeBase;

        // Published by the decode thread once the container is open; read lock-free by the UI so a
        // decode thread parked on _stateLock cannot freeze the position timer.
        private long _durationTicks;

        // Where a seek asked to go, reported as the position until the first frame is decoded.
        private long _pendingSeekTicks;

        // Nominal time between frames, taken from the stream instead of a hardcoded 30 fps.
        private double _frameIntervalMs = 1000.0 / 30.0;

        // Frame presentation bookkeeping: skip presenting while the UI still owes us the last frame.
        private int _framesSinceRender;
        private int _renderPending;

        // ----- Reverse playback -----
        // Decoders only run forwards, so playing backwards means repeatedly decoding a window that
        // ends at the current position and presenting the frames of that window in reverse order.
        // The window is bounded so the buffered BGR24 frames cannot grow without limit.
        private const int MaxReverseFrames = 24;

        private readonly List<byte[]> _reverseFrames = new(MaxReverseFrames);
        private readonly List<long> _reversePts = new(MaxReverseFrames);
        private int _reverseCursor = -1;
        private long _reverseAnchorPts = ffmpeg.AV_NOPTS_VALUE;

        // Direction the decode loop last ran in, so a direction change can reset the pipeline.
        private bool _lastLoopWasReverse;

        // Raised by a seek on another thread; honoured by the decode thread, which owns the lists.
        private volatile bool _reverseInvalidated;

        public PlaybackController(
            FFmpegIO ffio,
            GrowingFileCache cache,
            RangeDownloader? downloader,
            WriteableBitmap targetBitmap)
        {
            _ffio = ffio;
            _cache = cache;
            _downloader = downloader;

            _targetBitmap = targetBitmap;
            _targetWidth = targetBitmap.PixelWidth;
            _targetHeight = targetBitmap.PixelHeight;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _decodeTask = Task.Run(() => DecodeLoop(token), token);
        }

        public void Play() => _isPlaying = true;

        public void Pause() => _isPlaying = false;

        /// <summary>
        /// Playback speed. A negative value plays backwards, e.g. -1 for reverse at normal speed
        /// and -4 for reverse at 4x.
        /// </summary>
        public void SetRate(double rate) => Volatile.Write(ref _playbackRate, rate);

        /// <summary>True while the controller is playing backwards.</summary>
        public bool IsReverse => Volatile.Read(ref _playbackRate) < 0;

        /// <summary>Starts (or continues) playing backwards, keeping the current speed magnitude.</summary>
        public void PlayBackward()
        {
            var speed = Math.Abs(Volatile.Read(ref _playbackRate));
            if (speed <= 0) speed = 1.0;

            SetRate(-speed);
            _isPlaying = true;
        }

        /// <summary>Starts (or continues) playing forwards, keeping the current speed magnitude.</summary>
        public void PlayForward()
        {
            var speed = Math.Abs(Volatile.Read(ref _playbackRate));
            if (speed <= 0) speed = 1.0;

            SetRate(speed);
            _isPlaying = true;
        }

        /// <summary>Changes the speed magnitude without changing the current direction.</summary>
        public void SetSpeed(double speed)
        {
            speed = Math.Abs(speed);
            SetRate(IsReverse ? -speed : speed);
        }

        public Task SeekTimeAsync(TimeSpan ts)
        {
            if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;

            // The decode thread can hold _stateLock for a long time while it waits for bytes to be
            // downloaded, so never take it on the caller's (UI) thread.
            return Task.Run(() => SeekTimeCore(ts));
        }

        private unsafe void SeekTimeCore(TimeSpan ts)
        {
            var tsScaled = (long)(ts.TotalSeconds * ffmpeg.AV_TIME_BASE);

            // Report the requested position straight away: the pts is unknown until the first frame
            // after the seek is decoded, and returning zero in the meantime makes the UI slider jump.
            Interlocked.Exchange(ref _pendingSeekTicks, ts.Ticks);

            // The decode thread may currently be parked inside av_read_frame while holding
            // _stateLock, waiting for bytes from a *previous* position that have not downloaded yet.
            // Without this, a new seek issued while that wait is still in progress would block on
            // the lock indefinitely instead of landing on the new spot - release it here so this
            // seek is not stuck behind a stale one.
            _ffio.InterruptPendingRead();

            lock (_stateLock)
            {
                if (_fmtCtx == null)
                    return;

                // Without AVSEEK_FLAG_BACKWARD the demuxer lands on the first keyframe at or after
                // the target, so a backward jump can end up ahead of where it started.
                var backward = ts <= GetCurrentTime();
                var flags = backward ? ffmpeg.AVSEEK_FLAG_BACKWARD : 0;

                // Seek mode makes ReadPacket give up quickly when a probe read lands on bytes that
                // have not been downloaded yet, so the seek call cannot block the lock forever if
                // the format's internal index/keyframe search touches positions outside the range we
                // prefetched. The seek may be imprecise if data is sparse, but the decoder will
                // resync on the next keyframe it does find, which is always better than a hang.
                _ffio.SetSeekMode(true);
                try
                {
                    ffmpeg.avformat_seek_file(_fmtCtx, -1, long.MinValue, tsScaled, long.MaxValue, flags);
                }
                finally
                {
                    _ffio.SetSeekMode(false);
                }

                // A previous short read may have latched an error on the custom AVIOContext.
                if (_fmtCtx->pb != null)
                {
                    _fmtCtx->pb->eof_reached = 0;
                    _fmtCtx->pb->error = 0;
                }

                if (_videoDecCtx != null)
                    ffmpeg.avcodec_flush_buffers(_videoDecCtx);

                if (_audioDecCtx != null)
                    ffmpeg.avcodec_flush_buffers(_audioDecCtx);

                Interlocked.Exchange(ref _lastVideoPts, ffmpeg.AV_NOPTS_VALUE);

                // Frames buffered for reverse playback belong to the old position. The lists are
                // owned by the decode thread, so only raise a flag here and let it do the clearing.
                _reverseInvalidated = true;
            }
        }

        private void InvalidateReverseBuffer()
        {
            _reverseFrames.Clear();
            _reversePts.Clear();
            _reverseCursor = -1;
            _reverseAnchorPts = ffmpeg.AV_NOPTS_VALUE;
            _reverseInvalidated = false;
        }

        /// <summary>
        /// Presents exactly one more frame and pauses. Stepping backwards uses the reverse frame
        /// window, so it lands on the true previous frame rather than on the previous keyframe.
        /// </summary>
        public Task StepFrameAsync(bool forward = true)
        {
            SetRate(forward ? Math.Abs(Volatile.Read(ref _playbackRate)) : -Math.Abs(Volatile.Read(ref _playbackRate)));

            _isPlaying = false;
            _singleStepRequested = true;

            return Task.CompletedTask;
        }

        private TimeSpan GetCurrentTime()
        {
            var pts = Interlocked.Read(ref _lastVideoPts);
            if (pts == ffmpeg.AV_NOPTS_VALUE)
                return TimeSpan.FromTicks(Interlocked.Read(ref _pendingSeekTicks));

            var tb = _videoTimeBase;
            if (tb.den == 0)
                return TimeSpan.Zero;

            return TimeSpan.FromSeconds(pts * ffmpeg.av_q2d(tb));
        }

        /// <summary>Current playback position, or <see cref="TimeSpan.Zero"/> before the first frame.</summary>
        public TimeSpan Position => GetCurrentTime();

        /// <summary>Total media duration, or <see cref="TimeSpan.Zero"/> while it is still unknown.</summary>
        public TimeSpan Duration
        {
            get
            {
                var ticks = Interlocked.Read(ref _durationTicks);
                return ticks > 0 ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Rough byte offset the demuxer is reading at, used to steer the download prefetch window
        /// towards what is about to be played (including after a seek).
        /// </summary>
        public long GetPlaybackByteOffset(long totalBytes)
        {
            if (totalBytes <= 0) return 0;

            var duration = Duration;
            if (duration <= TimeSpan.Zero) return 0;

            var fraction = GetCurrentTime().TotalSeconds / duration.TotalSeconds;
            return (long)Math.Clamp(fraction * totalBytes, 0, totalBytes - 1);
        }

        private unsafe void DecodeLoop(CancellationToken ct)
        {
            FFmpegHelper.Init();

            AVPacket* pkt = null;
            AVFrame* frame = null;
            byte* outBuffer = null;

            try
            {
                OpenInput();
                OpenDecoders();

                pkt = ffmpeg.av_packet_alloc();
                frame = ffmpeg.av_frame_alloc();

                var outBufferSize = _targetWidth * _targetHeight * 3;
                outBuffer = (byte*)ffmpeg.av_malloc((ulong)outBufferSize);

                var frameClock = System.Diagnostics.Stopwatch.StartNew();

                while (!ct.IsCancellationRequested)
                {
                    if (!_isPlaying && !_singleStepRequested)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    var rate = Volatile.Read(ref _playbackRate);
                    if (rate == 0) rate = 1.0;

                    var reverse = rate < 0;
                    var speed = Math.Abs(rate);

                    // Switching direction leaves the demuxer positioned for the old direction.
                    if (reverse != _lastLoopWasReverse)
                    {
                        _lastLoopWasReverse = reverse;

                        if (!reverse)
                        {
                            // Resume forward decoding from wherever reverse playback stopped.
                            SeekTimeCore(GetCurrentTime());
                        }
                        else
                        {
                            InvalidateReverseBuffer();
                        }
                    }

                    if (reverse)
                    {
                        if (_reverseInvalidated)
                            InvalidateReverseBuffer();

                        if (!StepReverse(frame, pkt, outBuffer, outBufferSize, speed, ct))
                        {
                            // Start of the media reached: stop rather than spin.
                            _isPlaying = false;
                            _singleStepRequested = false;
                            continue;
                        }

                        if (_singleStepRequested)
                        {
                            _singleStepRequested = false;
                            _isPlaying = false;
                        }

                        PaceFrame(frameClock, speed);
                        continue;
                    }

                    int ret;
                    lock (_stateLock)
                    {
                        ret = ffmpeg.av_read_frame(_fmtCtx, pkt);
                    }

                    if (ret < 0)
                    {
                        ffmpeg.av_packet_unref(pkt);

                        if (ret == ffmpeg.AVERROR_EOF && (_cache.IsComplete || ct.IsCancellationRequested))
                        {
                            // Real end of a fully downloaded file: stop instead of spinning.
                            _isPlaying = false;
                            break;
                        }

                        // Short read on a still-growing file. FFmpeg latches eof_reached/error on
                        // the AVIOContext and would never call the read callback again, so clear
                        // them before backing off and retrying.
                        lock (_stateLock)
                        {
                            if (_fmtCtx != null && _fmtCtx->pb != null)
                            {
                                _fmtCtx->pb->eof_reached = 0;
                                _fmtCtx->pb->error = 0;
                            }
                        }

                        Thread.Sleep(50);
                        continue;
                    }

                    if (pkt->stream_index == _videoStreamIndex)
                    {
                        ProcessVideoPacket(pkt, frame, outBuffer, outBufferSize, ct);
                    }
                    else if (pkt->stream_index == _audioStreamIndex && _audioDecCtx != null && speed <= 1.0)
                    {
                        if (ffmpeg.avcodec_send_packet(_audioDecCtx, pkt) == 0)
                        {
                            while (ffmpeg.avcodec_receive_frame(_audioDecCtx, frame) == 0)
                            {
                                _audioPlayer.SubmitAudioFrame(frame);
                                ffmpeg.av_frame_unref(frame);
                            }
                        }
                    }

                    ffmpeg.av_packet_unref(pkt);

                    // Pace playback on the stream's own frame interval. Dividing by the speed is
                    // what makes 2x/4x actually faster.
                    PaceFrame(frameClock, speed);
                }
            }
            finally
            {
                if (outBuffer != null) ffmpeg.av_free(outBuffer);
                if (frame != null) ffmpeg.av_frame_free(&frame);
                if (pkt != null) ffmpeg.av_packet_free(&pkt);

                CleanupNative();
            }
        }

        private unsafe void OpenInput()
        {
            var fmtCtx = ffmpeg.avformat_alloc_context();
            fmtCtx->pb = _ffio.AvioContext;

            // Tell FFmpeg the I/O is custom so it does not try to reopen by filename.
            fmtCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            int err = ffmpeg.avformat_open_input(&fmtCtx, null, null, null);
            if (err < 0)
            {
                // avformat_open_input already freed the context on failure only when it took
                // ownership; with custom IO it does not, so release it here.
                if (fmtCtx != null) ffmpeg.avformat_free_context(fmtCtx);
                throw new ApplicationException("Could not open input: " + FFmpegHelper.AvError(err));
            }

            _fmtCtx = fmtCtx;

            err = ffmpeg.avformat_find_stream_info(_fmtCtx, null);
            if (err < 0)
                throw new ApplicationException("Could not find stream info: " + FFmpegHelper.AvError(err));

            for (int i = 0; i < _fmtCtx->nb_streams; i++)
            {
                var st = _fmtCtx->streams[i];

                if (st->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStreamIndex < 0)
                    _videoStreamIndex = i;

                if (st->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && _audioStreamIndex < 0)
                    _audioStreamIndex = i;
            }

            if (_videoStreamIndex < 0)
                throw new ApplicationException("No video stream found");

            _videoTimeBase = _fmtCtx->streams[_videoStreamIndex]->time_base;

            var fps = ffmpeg.av_guess_frame_rate(_fmtCtx, _fmtCtx->streams[_videoStreamIndex], null);
            if (fps.num > 0 && fps.den > 0)
                _frameIntervalMs = 1000.0 * fps.den / fps.num;

            if (_fmtCtx->duration > 0)
                Interlocked.Exchange(ref _durationTicks, TimeSpan.FromSeconds((double)_fmtCtx->duration / ffmpeg.AV_TIME_BASE).Ticks);
        }

        private unsafe void OpenDecoders()
        {
            // ----- Hardware device (optional) -----
            var hwType = FFmpegHelper.PreferredHwDevice;
            if (hwType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                AVBufferRef* hwDeviceCtx = null;
                if (ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, hwType, null, null, 0) >= 0)
                    _hwDeviceCtx = hwDeviceCtx;
            }

            // ----- Video decoder -----
            var vpar = _fmtCtx->streams[_videoStreamIndex]->codecpar;
            var vcodec = ffmpeg.avcodec_find_decoder(vpar->codec_id);
            if (vcodec == null)
                throw new ApplicationException("No decoder found for the video stream");

            _videoDecCtx = ffmpeg.avcodec_alloc_context3(vcodec);
            ffmpeg.avcodec_parameters_to_context(_videoDecCtx, vpar);

            if (_hwDeviceCtx != null)
            {
                _videoDecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);

                // Keep the delegate rooted for the lifetime of the codec context.
                _getFormatCallback = SelectPixelFormat;
                _videoDecCtx->get_format = new AVCodecContext_get_format_func
                {
                    Pointer = Marshal.GetFunctionPointerForDelegate(_getFormatCallback)
                };
            }

            int openErr = ffmpeg.avcodec_open2(_videoDecCtx, vcodec, null);
            if (openErr < 0)
                throw new ApplicationException("Could not open video codec: " + FFmpegHelper.AvError(openErr));

            // ----- Audio decoder -----
            if (_audioStreamIndex >= 0)
            {
                var apar = _fmtCtx->streams[_audioStreamIndex]->codecpar;
                var acodec = ffmpeg.avcodec_find_decoder(apar->codec_id);

                if (acodec != null)
                {
                    _audioDecCtx = ffmpeg.avcodec_alloc_context3(acodec);
                    ffmpeg.avcodec_parameters_to_context(_audioDecCtx, apar);

                    if (ffmpeg.avcodec_open2(_audioDecCtx, acodec, null) < 0)
                    {
                        var audioCtx = _audioDecCtx;
                        ffmpeg.avcodec_free_context(&audioCtx);
                        _audioDecCtx = null;
                        _audioStreamIndex = -1;
                    }
                }
            }
        }

        private static unsafe AVPixelFormat SelectPixelFormat(AVCodecContext* ctx, AVPixelFormat* pixFmts)
        {
            for (var p = pixFmts; p != null && *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == AVPixelFormat.AV_PIX_FMT_D3D11 ||
                    *p == AVPixelFormat.AV_PIX_FMT_DXVA2_VLD)
                {
                    return *p;
                }
            }

            return pixFmts != null ? *pixFmts : AVPixelFormat.AV_PIX_FMT_NONE;
        }

        private void PaceFrame(System.Diagnostics.Stopwatch clock, double speed)
        {
            if (speed <= 0) speed = 1.0;

            var due = _frameIntervalMs / speed - clock.Elapsed.TotalMilliseconds;
            if (due > 1) Thread.Sleep((int)due);
            clock.Restart();
        }

        /// <summary>
        /// Presents the next frame going backwards. Returns false once the start of the media is
        /// reached and there is nothing left to show.
        /// </summary>
        private unsafe bool StepReverse(AVFrame* frame, AVPacket* pkt, byte* outBuffer, int outBufferSize, double speed, CancellationToken ct)
        {
            if (_reverseCursor < 0 && !FillReverseWindow(frame, pkt, outBuffer, outBufferSize, ct))
                return false;

            if (_reverseCursor < 0 || _reverseCursor >= _reverseFrames.Count)
                return false;

            Interlocked.Exchange(ref _lastVideoPts, _reversePts[_reverseCursor]);
            PresentFrame(_reverseFrames[_reverseCursor]);

            // At higher speeds show every Nth buffered frame instead of decoding less accurately.
            var stride = speed >= 2.0 ? (int)Math.Round(speed) : 1;
            _reverseCursor -= _singleStepRequested ? 1 : stride;

            return true;
        }

        /// <summary>
        /// Decodes the window of frames immediately before the current position and keeps the last
        /// <see cref="MaxReverseFrames"/> of them, so they can be presented newest-first.
        /// </summary>
        private unsafe bool FillReverseWindow(AVFrame* frame, AVPacket* pkt, byte* outBuffer, int outBufferSize, CancellationToken ct)
        {
            _reverseFrames.Clear();
            _reversePts.Clear();
            _reverseCursor = -1;

            var tb = _videoTimeBase;
            if (tb.den == 0) return false;

            var anchorTime = _reverseAnchorPts == ffmpeg.AV_NOPTS_VALUE
                ? GetCurrentTime()
                : TimeSpan.FromSeconds(_reverseAnchorPts * ffmpeg.av_q2d(tb));

            if (anchorTime <= TimeSpan.Zero) return false;

            // Decode a window slightly larger than what we keep, so the seek lands on a keyframe
            // early enough for the frames we actually want to be reconstructable.
            var windowMs = _frameIntervalMs * MaxReverseFrames;
            var startTime = anchorTime - TimeSpan.FromMilliseconds(windowMs);
            if (startTime < TimeSpan.Zero) startTime = TimeSpan.Zero;

            var anchorPts = _reverseAnchorPts == ffmpeg.AV_NOPTS_VALUE
                ? (long)(anchorTime.TotalSeconds / ffmpeg.av_q2d(tb))
                : _reverseAnchorPts;

            SeekTimeCore(startTime);

            // SeekTimeCore flags the window as stale; it is being rebuilt right now.
            _reverseInvalidated = false;
            _reverseAnchorPts = anchorPts;
            Interlocked.Exchange(ref _pendingSeekTicks, anchorTime.Ticks);

            while (!ct.IsCancellationRequested)
            {
                int ret;
                lock (_stateLock)
                {
                    ret = ffmpeg.av_read_frame(_fmtCtx, pkt);
                }

                if (ret < 0)
                {
                    ffmpeg.av_packet_unref(pkt);
                    break;
                }

                if (pkt->stream_index != _videoStreamIndex)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                var sent = ffmpeg.avcodec_send_packet(_videoDecCtx, pkt);
                ffmpeg.av_packet_unref(pkt);
                if (sent != 0) continue;

                var reachedAnchor = false;

                while (ffmpeg.avcodec_receive_frame(_videoDecCtx, frame) == 0)
                {
                    AVFrame* swFrame = frame;
                    AVFrame* tmpFrame = null;

                    try
                    {
                        if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11 ||
                            frame->format == (int)AVPixelFormat.AV_PIX_FMT_DXVA2_VLD)
                        {
                            tmpFrame = ffmpeg.av_frame_alloc();
                            if (ffmpeg.av_hwframe_transfer_data(tmpFrame, frame, 0) < 0)
                            {
                                ffmpeg.av_frame_free(&tmpFrame);
                                tmpFrame = null;
                            }
                            else
                            {
                                swFrame = tmpFrame;
                            }
                        }

                        var pts = frame->pts;
                        if (pts == ffmpeg.AV_NOPTS_VALUE) continue;

                        if (pts >= anchorPts)
                        {
                            reachedAnchor = true;
                            continue;
                        }

                        var pixels = ScaleFrame(swFrame, outBuffer, outBufferSize);
                        if (pixels == null) continue;

                        _reverseFrames.Add(pixels);
                        _reversePts.Add(pts);

                        // Keep only the newest frames of the window.
                        if (_reverseFrames.Count > MaxReverseFrames)
                        {
                            _reverseFrames.RemoveAt(0);
                            _reversePts.RemoveAt(0);
                        }
                    }
                    finally
                    {
                        if (tmpFrame != null)
                            ffmpeg.av_frame_free(&tmpFrame);

                        ffmpeg.av_frame_unref(frame);
                    }
                }

                if (reachedAnchor) break;
            }

            if (_reverseFrames.Count == 0)
                return false;

            // Next window ends where this one starts.
            _reverseAnchorPts = _reversePts[0];
            _reverseCursor = _reverseFrames.Count - 1;

            return true;
        }

        private unsafe void ProcessVideoPacket(AVPacket* pkt, AVFrame* frame, byte* outBuffer, int outBufferSize, CancellationToken ct)
        {
            if (ffmpeg.avcodec_send_packet(_videoDecCtx, pkt) != 0)
                return;

            while (!ct.IsCancellationRequested && ffmpeg.avcodec_receive_frame(_videoDecCtx, frame) == 0)
            {
                AVFrame* swFrame = frame;
                AVFrame* tmpFrame = null;

                try
                {
                    bool isHwFrame =
                        frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11 ||
                        frame->format == (int)AVPixelFormat.AV_PIX_FMT_DXVA2_VLD;

                    if (isHwFrame)
                    {
                        tmpFrame = ffmpeg.av_frame_alloc();
                        if (ffmpeg.av_hwframe_transfer_data(tmpFrame, frame, 0) < 0)
                        {
                            ffmpeg.av_frame_free(&tmpFrame);
                            tmpFrame = null;
                        }
                        else
                        {
                            swFrame = tmpFrame;
                        }
                    }

                    if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                        Interlocked.Exchange(ref _lastVideoPts, frame->pts);

                    // At high rates keep decoding every frame - dropping packets breaks the
                    // reference chain - but only present every Nth one, which is what the render
                    // path can keep up with.
                    var rate = Volatile.Read(ref _playbackRate);
                    var renderEvery = rate >= 2.0 ? (int)Math.Round(rate) : 1;

                    if (_singleStepRequested || ++_framesSinceRender >= renderEvery)
                    {
                        _framesSinceRender = 0;
                        RenderFrame(swFrame, outBuffer, outBufferSize);
                    }

                    if (_singleStepRequested)
                    {
                        _singleStepRequested = false;
                        _isPlaying = false;
                    }
                }
                finally
                {
                    if (tmpFrame != null)
                        ffmpeg.av_frame_free(&tmpFrame);

                    ffmpeg.av_frame_unref(frame);
                }
            }
        }

        private unsafe void RenderFrame(AVFrame* swFrame, byte* outBuffer, int outBufferSize)
        {
            var pixels = ScaleFrame(swFrame, outBuffer, outBufferSize);
            if (pixels != null)
                PresentFrame(pixels);
        }

        /// <summary>Scales a decoded frame into a fresh BGR24 buffer sized for the target bitmap.</summary>
        private unsafe byte[]? ScaleFrame(AVFrame* swFrame, byte* outBuffer, int outBufferSize)
        {
            var srcFmt = (AVPixelFormat)swFrame->format;

            // Reuse the scaler unless the source geometry/format changed.
            if (_swsCtx == null || _swsSrcWidth != swFrame->width || _swsSrcHeight != swFrame->height || _swsSrcFormat != srcFmt)
            {
                if (_swsCtx != null)
                    ffmpeg.sws_freeContext(_swsCtx);

                _swsCtx = ffmpeg.sws_getContext(
                    swFrame->width,
                    swFrame->height,
                    srcFmt,
                    _targetWidth,
                    _targetHeight,
                    AVPixelFormat.AV_PIX_FMT_BGR24,
                    SWS_BILINEAR,
                    null,
                    null,
                    null);

                _swsSrcWidth = swFrame->width;
                _swsSrcHeight = swFrame->height;
                _swsSrcFormat = srcFmt;
            }

            if (_swsCtx == null)
                return null;

            var dstData = new byte_ptrArray4();
            var dstLines = new int_array4();

            ffmpeg.av_image_fill_arrays(
                ref dstData,
                ref dstLines,
                outBuffer,
                AVPixelFormat.AV_PIX_FMT_BGR24,
                _targetWidth,
                _targetHeight,
                1);

            // sws_scale now takes managed byte*[] / int[] arrays instead of the fixed-size structs.
            var srcPlanes = new byte*[4];
            var srcStrides = new int[4];
            var dstPlanes = new byte*[4];
            var dstStrides = new int[4];

            for (uint i = 0; i < 4; i++)
            {
                srcPlanes[i] = swFrame->data[i];
                srcStrides[i] = swFrame->linesize[i];
                dstPlanes[i] = dstData[i];
                dstStrides[i] = dstLines[i];
            }

            ffmpeg.sws_scale(_swsCtx, srcPlanes, srcStrides, 0, swFrame->height, dstPlanes, dstStrides);

            var managed = new byte[outBufferSize];
            Marshal.Copy((IntPtr)outBuffer, managed, 0, outBufferSize);

            return managed;
        }

        /// <summary>Hands a scaled BGR24 buffer to the UI thread for display.</summary>
        private void PresentFrame(byte[] managed)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            // A blocking Invoke here throttled the whole pipeline to the UI thread and starved the
            // downloader. Post the frame instead and skip it if the previous one is still pending.
            if (Interlocked.CompareExchange(ref _renderPending, 1, 0) != 0)
                return;

            dispatcher.InvokeAsync(() =>
            {
                try
                {
                    _targetBitmap.Lock();
                    try
                    {
                        _targetBitmap.WritePixels(
                            new Int32Rect(0, 0, _targetWidth, _targetHeight),
                            managed,
                            _targetWidth * 3,
                            0);
                        _targetBitmap.AddDirtyRect(new Int32Rect(0, 0, _targetWidth, _targetHeight));
                    }
                    finally
                    {
                        _targetBitmap.Unlock();
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _renderPending, 0);
                }
            });
        }

        private unsafe void CleanupNative()
        {
            if (_swsCtx != null)
            {
                ffmpeg.sws_freeContext(_swsCtx);
                _swsCtx = null;
            }

            if (_videoDecCtx != null)
            {
                var videoCtx = _videoDecCtx;
                ffmpeg.avcodec_free_context(&videoCtx);
                _videoDecCtx = null;
            }

            if (_audioDecCtx != null)
            {
                var audioCtx = _audioDecCtx;
                ffmpeg.avcodec_free_context(&audioCtx);
                _audioDecCtx = null;
            }

            if (_hwDeviceCtx != null)
            {
                var hw = _hwDeviceCtx;
                ffmpeg.av_buffer_unref(&hw);
                _hwDeviceCtx = null;
            }

            if (_fmtCtx != null)
            {
                var fmt = _fmtCtx;
                ffmpeg.avformat_close_input(&fmt);
                _fmtCtx = null;
            }

            _getFormatCallback = null;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _decodeTask?.Wait(2000);
            _cts.Dispose();

            _audioPlayer?.Dispose();
            _ffio?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
