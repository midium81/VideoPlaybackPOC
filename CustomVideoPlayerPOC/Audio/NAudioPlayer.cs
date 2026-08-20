using FFmpeg.AutoGen;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CustomVideoPlayerPOC.Audio
{
    public unsafe class NAudioPlayer : IDisposable
    {
        private readonly WaveOutEvent _waveOut;
        private BufferedWaveProvider? _bufferedProvider;
        private readonly object _lock = new();
        private SwrContext* _swrCtx = null;
        private int _outSampleRate = 44100;
        private int _outChannels = 2;
        private readonly AVSampleFormat _outFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;

        public NAudioPlayer()
        {
            _waveOut = new WaveOutEvent();
        }

        public void Init(int sampleRate, int channels)
        {
            lock (_lock)
            {
                _outSampleRate = sampleRate;
                _outChannels = channels;
                var waveFormat = WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, _outSampleRate, _outChannels, _outSampleRate * _outChannels * 2, _outChannels * 2, 16);
                _bufferedProvider = new BufferedWaveProvider(waveFormat) { DiscardOnBufferOverflow = true };
                _waveOut.Init(_bufferedProvider);
                _waveOut.Play();
            }
        }

        public void SubmitAudioFrame(AVFrame* frame)
        {
            lock (_lock)
            {
                // AVFrame.channels was removed in FFmpeg 5.1; use AVChannelLayout.nb_channels.
                int srcChannels = frame->ch_layout.nb_channels;

                if (_bufferedProvider == null)
                {
                    Init(frame->sample_rate, srcChannels);
                }

                // If frame is already S16 interleaved, copy directly
                if (frame->format == (int)AVSampleFormat.AV_SAMPLE_FMT_S16)
                {
                    var dataPtr = (IntPtr)frame->data[0];
                    var size = ffmpeg.av_samples_get_buffer_size(null, srcChannels, frame->nb_samples, (AVSampleFormat)frame->format, 1);
                    if (size <= 0) return;
                    var buffer = new byte[size];
                    Marshal.Copy(dataPtr, buffer, 0, size);
                    _bufferedProvider!.AddSamples(buffer, 0, size);
                    return;
                }

                // Otherwise resample to S16 interleaved.
                // swr_alloc_set_opts was replaced by swr_alloc_set_opts2 (AVChannelLayout based).
                if (_swrCtx == null)
                {
                    AVChannelLayout outLayout;
                    ffmpeg.av_channel_layout_default(&outLayout, _outChannels);

                    AVChannelLayout inLayout = frame->ch_layout;
                    if (inLayout.nb_channels <= 0)
                        ffmpeg.av_channel_layout_default(&inLayout, srcChannels);

                    SwrContext* swr = null;
                    int err = ffmpeg.swr_alloc_set_opts2(
                        &swr,
                        &outLayout,
                        _outFormat,
                        _outSampleRate,
                        &inLayout,
                        (AVSampleFormat)frame->format,
                        frame->sample_rate,
                        0,
                        null);

                    if (err < 0 || swr == null) return;

                    if (ffmpeg.swr_init(swr) < 0)
                    {
                        ffmpeg.swr_free(&swr);
                        return;
                    }

                    _swrCtx = swr;
                }

                int dstNbSamples = (int)ffmpeg.av_rescale_rnd(ffmpeg.swr_get_delay(_swrCtx, frame->sample_rate) + frame->nb_samples, _outSampleRate, frame->sample_rate, AVRounding.AV_ROUND_UP);
                byte** dstData = null;
                if (ffmpeg.av_samples_alloc_array_and_samples(&dstData, null, _outChannels, dstNbSamples, _outFormat, 0) < 0)
                    return;

                try
                {
                    var converted = ffmpeg.swr_convert(_swrCtx, dstData, dstNbSamples, (byte**)&frame->data, frame->nb_samples);
                    if (converted <= 0) return;

                    var outSize = ffmpeg.av_samples_get_buffer_size(null, _outChannels, converted, _outFormat, 1);
                    if (outSize <= 0) return;

                    var buffer = new byte[outSize];
                    Marshal.Copy((IntPtr)dstData[0], buffer, 0, outSize);
                    _bufferedProvider!.AddSamples(buffer, 0, outSize);
                }
                finally
                {
                    if (dstData != null)
                    {
                        ffmpeg.av_freep(&dstData[0]);
                        ffmpeg.av_freep(&dstData);
                    }
                }
            }
        }

        public void Dispose()
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            if (_swrCtx != null)
            {
                var swr = _swrCtx;
                ffmpeg.swr_free(&swr);
                _swrCtx = null;
            }
            GC.SuppressFinalize(this);
        }
    }
}