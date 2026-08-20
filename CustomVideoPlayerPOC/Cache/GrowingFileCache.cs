using CustomVideoPlayerPOC.Core;
using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace CustomVideoPlayerPOC.Cache
{
	/// <summary>
	/// Reads from a file that is still being filled in by the downloader.
	///
	/// While the download is in flight every read first waits for the requested range to become
	/// available. Once <see cref="MarkComplete"/> is called the whole file is on disk, so the
	/// availability bookkeeping is bypassed entirely and reads go straight to the local file.
	/// </summary>
	public class GrowingFileCache : IDisposable
	{
		private readonly string _path;
		private readonly RangeSet _ranges;
		private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _waiters = new();
		private readonly object _handleLock = new();

		private SafeFileHandle? _handle;
		private volatile bool _isComplete;
		private bool _disposed;

		public GrowingFileCache(string path, RangeSet ranges)
		{
			_path = path;
			_ranges = ranges;
		}

		/// <summary>True once the cache serves reads purely from the completed local file.</summary>
		public bool IsComplete => _isComplete;

		/// <summary>
		/// Switches the cache into local-only mode: no more range checks, no more waiting.
		/// Safe to call more than once. Any pending waiters are released immediately.
		/// </summary>
		public void MarkComplete()
		{
			if (_isComplete) return;
			_isComplete = true;

			foreach (var key in _waiters.Keys)
			{
				if (_waiters.TryRemove(key, out var tcs))
					tcs.TrySetResult(true);
			}
		}

		public async Task<int> ReadAsync(byte[] buffer, long offset, int count, TimeSpan timeout, CancellationToken ct = default)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);

			if (offset < 0 || count <= 0) return 0;

			// Fully downloaded: skip availability tracking and read the local file directly.
			if (!_isComplete)
			{
				await WaitForRangeAsync(offset, count, timeout, ct).ConfigureAwait(false);
			}

			// RandomAccess is thread-safe and needs no seek, so concurrent reads need no lock.
			return await RandomAccess.ReadAsync(GetHandle(), buffer.AsMemory(0, count), offset, ct)
									 .ConfigureAwait(false);
		}

		private async Task WaitForRangeAsync(long offset, int count, TimeSpan timeout, CancellationToken ct)
		{
			var sw = Stopwatch.StartNew();
			while (!_ranges.IsRangeAvailable(offset, count))
			{
				// MarkComplete may land while we are waiting.
				if (_isComplete) return;

				if (sw.Elapsed > timeout) throw new TimeoutException("Timeout waiting for data");
				ct.ThrowIfCancellationRequested();

				var key = offset / 4096;
				var tcs = _waiters.GetOrAdd(key, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
				using var reg = ct.Register(() => tcs.TrySetCanceled());
				var delay = Task.Delay(200, ct);
				var completed = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
				if (completed == delay) continue;
			}
		}

		private SafeFileHandle GetHandle()
		{
			var handle = _handle;
			if (handle is { IsInvalid: false, IsClosed: false }) return handle;

			lock (_handleLock)
			{
				if (_handle is { IsInvalid: false, IsClosed: false }) return _handle;

				_handle = File.OpenHandle(
					_path,
					FileMode.Open,
					FileAccess.Read,
					FileShare.ReadWrite,
					FileOptions.Asynchronous | FileOptions.RandomAccess);

				return _handle;
			}
		}

		public void NotifyRangeAvailable(ByteRange r)
		{
			var startKey = r.Start / 4096;
			var endKey = r.End / 4096;
			for (long k = startKey; k <= endKey; k++)
			{
				if (_waiters.TryRemove(k, out var tcs))
				{
					tcs.TrySetResult(true);
				}
			}
		}

		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;

			// Release anything still blocked on a range that will never arrive.
			foreach (var key in _waiters.Keys)
			{
				if (_waiters.TryRemove(key, out var tcs))
					tcs.TrySetResult(false);
			}

			lock (_handleLock)
			{
				_handle?.Dispose();
				_handle = null;
			}

			GC.SuppressFinalize(this);
		}
	}
}
