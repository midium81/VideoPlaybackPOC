using FFmpeg.AutoGen;
using System.IO;
using System.Runtime.InteropServices;

namespace CustomVideoPlayerPOC.FFmpeg
{
    public static unsafe class FFmpegHelper
    {
        private static readonly object _initLock = new();
        private static bool _initialized;

        public static AVHWDeviceType PreferredHwDevice = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

        /// <summary>
        /// Points FFmpeg.AutoGen at the folder containing the native FFmpeg shared libraries and
        /// wires up the dynamically-loaded bindings (required since FFmpeg.AutoGen 6.x).
        /// </summary>
        public static void RegisterFFmpegBinaries(string folder)
        {
            var current = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!current.Contains(folder, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + current);
            }

            ffmpeg.RootPath = folder;

            // FFmpeg.AutoGen 6+ no longer resolves native entry points implicitly.
            DynamicallyLoadedBindings.Initialize();
        }

        public static void Init()
        {
            lock (_initLock)
            {
                if (_initialized) return;

                // av_register_all() / avcodec_register_all() were removed in FFmpeg 4.0 and are
                // gone from the FFmpeg 7.x bindings - codec/format registration is automatic.
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_INFO);
                ffmpeg.avformat_network_init();

                // Choose preferred HW device if available: D3D11VA -> DXVA2 -> QSV.
                if (IsHwDeviceSupported("d3d11va")) PreferredHwDevice = AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;
                else if (IsHwDeviceSupported("dxva2")) PreferredHwDevice = AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2;
                else if (IsHwDeviceSupported("qsv")) PreferredHwDevice = AVHWDeviceType.AV_HWDEVICE_TYPE_QSV;
                else PreferredHwDevice = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

                _initialized = true;
            }
        }

        private static bool IsHwDeviceSupported(string name)
        {
            var t = ffmpeg.av_hwdevice_find_type_by_name(name);
            return t != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        }

        public static string AvError(int err)
        {
            const int bufferSize = 1024;
            var buf = stackalloc byte[bufferSize];

            // av_strerror takes a size_t (ulong) buffer size in the new bindings.
            ffmpeg.av_strerror(err, buf, bufferSize);
            return Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"FFmpeg error {err}";
        }
    }
}