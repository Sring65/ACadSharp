using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ACadSharp.IO.DWG
{
	/// <summary>
	/// [PERF] 对象节"先打开、后解压"惰性页解压模式的页缓存模式。
	/// </summary>
	public enum DwgLazyPageCacheMode
	{
		/// <summary>
		/// 所有已解压的页全部常驻内存（不逐出）。内存占用＝整节预解压（BGDY 约 816MB），
		/// 但打开时间极短且首读页不再等待。适合同一文件反复全量读取的场景。
		/// </summary>
		All = 0,

		/// <summary>
		/// 按字节预算做 LRU 逐出（预算由 <see cref="DwgReaderConfiguration.LazyPageCacheBytes"/> 指定，默认 512MB）。
		/// 内存优化的主开关：读取顺序局部性强时命中率高，内存稳定在预算附近。
		/// </summary>
		Lru = 1,

		/// <summary>
		/// 按页数滑动窗口逐出（保留最近 N 页，N 由 <see cref="DwgReaderConfiguration.LazyEvictWindowPages"/> 指定，默认 64 页≈1.9MB）。
		/// 占用恒定且极小，与文件大小无关；页被逐出后若再读到需重新解压。
		/// </summary>
		EvictAfterUse = 2,

		/// <summary>
		/// 自适应字节预算：从 min(LazyPageCacheBytes, 1GB) 起步；
		/// 窗口内主线程命中率 &gt;97% 时预算减半（下限 64MB），&lt;90% 时加倍（上限 4GB）。
		/// 自动在速度与内存之间折中，无需人工调参。
		/// </summary>
		Auto = 3,
	}

	/// <summary>
	/// [PERF] R2004（AC1018）对象节（AcDbObjects）的页级惰性解压流。
	/// 构造时只从节映射读取页目录（零额外磁盘 I/O），不解压任何页；
	/// 调用方（DwgObjectReader）按 offset 定位读取，首次触达某页才解压（同步内联解压，或等待后台预取任务），每页至多解压一次。
	/// 读操作由主线程单线程顺序进行（_crcReader 缓冲读）；不支持写。
	/// 页目录必须在任何 decryptDataSection 头扫描之前快照（头扫描会用 checksum+startOffset 覆盖 LocalSections[].Offset）。
	/// </summary>
	internal sealed class DwgLazySectionStream : Stream
	{
		private const int StateNone = 0;
		private const int StateLoading = 1;
		private const int StateCached = 2;

		private readonly string _filePath;
		private readonly long[] _pageStart; //每页在解压映像中的起始偏移（连续平铺）
		private readonly int[] _pageLen; //每页解压后字节数
		private readonly long[] _filePos; //页体在文件中的位置（32B XOR 头之后）
		private readonly bool[] _empty; //空页（内容全 0，无需解压）
		internal readonly int _pageCount;
		internal readonly long _length;

		private readonly object _gate = new object();
		private readonly int[] _state;
		private readonly byte[][] _data;
		private readonly TaskCompletionSource<byte[]>[] _pending;

		//O(1) 双向链表 LRU（页号数组实现，无装箱）；仅"已缓存"页在链表中（解压中的页永不被逐出）
		private readonly int[] _lruPrev;
		private readonly int[] _lruNext;
		private int _lruHead = -1;
		private int _lruTail = -1;
		private int _lruCount;
		internal long _cachedBytes;
		internal long _peakCachedBytes;
		internal int _inFlight;
		private volatile bool _disposed;

		private readonly FileStream _mainFile;
		private readonly ThreadLocal<FileStream> _workerFile;

		internal readonly DwgLazyPageCacheMode _mode;
		internal long _byteBudget;
		private readonly int _evictWindowPages;
		private readonly int _maxInFlight;

		//诊断计数
		internal long _ensureCount;
		internal long _hitCount;
		internal long _missCount;
		internal long _prefetchWaitCount;
		internal long _inlineFallbackCount;
		internal long _decompressTicks;
		private long _autoWindowCalls;
		private long _autoWindowHits;
		private long _autoWindowMisses;

		//[PERF] 快路径诊断计数（仅主读取线程写；诊断读取用 Interlocked.Read）
		internal long _fastHitCount;
		internal long _generalCallCount;
		internal long _generalBackwardCount;
		internal long _generalCrossEndCount;
		internal long _prefetchClaimCount;
		internal long _prefetchDoneCount;
		internal long _prefetchResetCount;

		private long _position;
		private readonly byte[] _oneByte = new byte[1];

		//[PERF] 顺序读快路径：当前命中页窗口（仅主读取线程访问；页数据数组解压后不可变，
		//即使该页随后被 LRU 逐出，数组引用仍有效，故无需加锁）。
		private int _curPage = -1;
		private long _curPageStart;
		private long _curPageEnd;
		private byte[] _curData;

		public DwgLazySectionStream(string rawFilePath, DwgSectionDescriptor descriptor, DwgReaderConfiguration config)
		{
			_filePath = rawFilePath;

			//立即快照页目录：decryptDataSection 会用 (checksum + startOffset) 覆盖 Offset，
			//而这里的 Offset 是"解压映像内偏移"（页目录定位必需），必须在覆盖前固化。
			DwgLocalSectionMap[] sections = descriptor.LocalSections.ToArray();
			Array.Sort(sections, (a, b) => a.Offset.CompareTo(b.Offset));

			_pageCount = sections.Length;
			_pageStart = new long[_pageCount];
			_pageLen = new int[_pageCount];
			_filePos = new long[_pageCount];
			_empty = new bool[_pageCount];
			_state = new int[_pageCount];
			_data = new byte[_pageCount][];
			_pending = new TaskCompletionSource<byte[]>[_pageCount];
			_lruPrev = new int[_pageCount];
			_lruNext = new int[_pageCount];

			long len = 0;
			for (int i = 0; i < _pageCount; i++)
			{
				DwgLocalSectionMap s = sections[i];
				_pageStart[i] = (long)s.Offset;
				_pageLen[i] = (int)s.DecompressedSize;
				_empty[i] = s.IsEmpty;
				_filePos[i] = s.IsEmpty ? 0 : s.Seeker + 32;
				_lruPrev[i] = -1;
				_lruNext[i] = -1;
				len += _pageLen[i];
			}
			_length = len;

			_mode = (config != null) ? config.LazyPageCacheMode : DwgLazyPageCacheMode.Lru;
			long defaultBudget = (_mode == DwgLazyPageCacheMode.Auto) ? (1L << 30) : (512L << 20);
			long cfgBudget = (config != null) ? config.LazyPageCacheBytes : 0;
			_byteBudget = (cfgBudget > 0) ? cfgBudget : defaultBudget;
			if (_mode == DwgLazyPageCacheMode.Auto && _byteBudget > (1L << 30))
				_byteBudget = 1L << 30; //Auto 模式起步不超过 1GB
			int cfgWindow = (config != null) ? config.LazyEvictWindowPages : 64;
			_evictWindowPages = Math.Max(1, cfgWindow);
			int cfgWorkers = (config != null) ? config.LazyPrefetchWorkers : 4;
			_maxInFlight = Math.Max(1, cfgWorkers);

			_mainFile = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
			_workerFile = new ThreadLocal<FileStream>(
				() => new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read),
				false);

			DwgLazyReadDiagnostics.Register(this);
		}

		/// <summary>
		/// 按页定位读取（不改动流游标）。返回值可能小于 count（到达节末尾）；跨页自动拼接，空页补零。
		/// </summary>
		public int ReadAt(ref long position, byte[] buffer, int offset, int count)
		{
			if (count == 0)
				return 0;
			if (position < 0)
				position = 0;
			if (position >= _length)
				return 0;

			long end = position + count;
			if (end > _length)
				end = _length;

			//[PERF] 顺序读快路径：整个读段落在"当前已加载页"窗口内时直接拷贝，
			//省去二分定位/加锁/LRU 触碰（对象流按字段小步读取，这是惰性模式的主要开销源）。
			//计数仅主读取线程写（后台 worker 不走 ReadAt）→ 普通自增比 Interlocked 便宜约一个数量级。
			if (_curPage >= 0 && position >= _curPageStart && end <= _curPageEnd && _curData != null)
			{
				_fastHitCount++;
				long src = position - _curPageStart;
				int fastTake = (int)(end - position);
				if (fastTake == 1)
					buffer[offset] = _curData[(int)src];
				else
					Buffer.BlockCopy(_curData, (int)src, buffer, offset, fastTake);
				position = end;
				return fastTake;
			}

			_generalCallCount++;
			if (_curPage < 0 || position < _curPageStart)
				_generalBackwardCount++;
			else if (end > _curPageEnd)
				_generalCrossEndCount++;

			int total = (int)(end - position);
			int written = 0;
			int page = PageForOffset(position);
			while (written < total)
			{
				long pStart = _pageStart[page];
				long pEnd = pStart + _pageLen[page];
				long from = (position < pStart) ? pStart : position;
				long to = (end < pEnd) ? end : pEnd;
				int take = (int)(to - from);
				if (take <= 0)
				{
					page++;
					continue;
				}

				if (_empty[page])
				{
					Array.Clear(buffer, offset + written, take);
				}
				else
				{
					byte[] data = EnsurePage(page);
					Buffer.BlockCopy(data, (int)(from - pStart), buffer, offset + written, take);
				}

				position = to;
				written += take;
				page++;
			}

			//[PERF] 更新快路径窗口：若新游标所在页已加载，后续顺序读直接命中快路径
			if (position < _length)
			{
				int np = PageForOffset(position);
				if (_state[np] == StateCached && !_empty[np] && _data[np] != null)
				{
					_curPage = np;
					_curPageStart = _pageStart[np];
					_curPageEnd = _pageStart[np] + _pageLen[np];
					_curData = _data[np];
				}
				else
				{
					_curPage = -1;
					_curData = null;
				}
			}
			return written;
		}

		/// <summary>
		/// 预取认领（调用方需保证 page 有效）：页未解压且后台任务数未达上限时置为 Loading 并返回 TCS，否则返回 null。
		/// </summary>
		private TaskCompletionSource<byte[]> TryClaimForPrefetch(int page)
		{
			TaskCompletionSource<byte[]> tcs = null;
			lock (_gate)
			{
				if (_state[page] == StateNone && _inFlight < _maxInFlight)
				{
					tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
					_state[page] = StateLoading;
					_pending[page] = tcs;
					_inFlight++;
					Interlocked.Increment(ref _prefetchClaimCount);
				}
			}
			return tcs;
		}

		/// <summary>
		/// [PERF] 异步预取：对每个 offset，若其所在页尚未解压且后台任务数未达上限，则启动后台解压；
		/// 已缓存/解压中/超上限则直接跳过（主线程稍后读到时会同步解压兜底，数据不会丢失；
		/// DwgObjectReader 按读取顺序周期性重新扫描 handle 队列，被跳过的页会被自然重试）。
		/// </summary>
		public void EnqueuePrefetch(long[] offsets, int count)
		{
			if (_disposed || offsets == null || count <= 0)
				return;

			for (int i = 0; i < count; i++)
			{
				long off = offsets[i];
				if (off < 0 || off >= _length)
					continue;

				int page = PageForOffset(off);
				TaskCompletionSource<byte[]> tcs = TryClaimForPrefetch(page);
				if (tcs != null)
					LaunchPrefetch(page, tcs);
			}
		}

		/// <summary>
		/// [PERF] 预测预取（按读取顺序）：从读取游标当前 offset 起，把紧随其后的 pagesAhead 页中
		/// 所有"尚未解压"的页交给后台异步解压。DwgObjectReader 每读 64 个对象调用一次，
		/// 使主线程读到时下窗口页大多已就绪；实际并发受 LazyPrefetchWorkers 限制，
		/// 超限的页本轮跳过，下轮窗口自然重试。
		/// </summary>
		public void EnqueuePrefetchWindow(long start, int pagesAhead)
		{
			if (_disposed || pagesAhead <= 0 || start < 0 || start >= _length)
				return;

			int first = PageForOffset(start);
			int last = Math.Min(_pageCount - 1, first + pagesAhead - 1);
			for (int page = first; page <= last; page++)
			{
				TaskCompletionSource<byte[]> tcs = TryClaimForPrefetch(page);
				if (tcs != null)
					LaunchPrefetch(page, tcs);
			}
		}

		/// <summary>
		/// 创建独立的、可独立定位的读视图（DwgObjectReader 每对象读者使用；
		/// 视图为 MemoryStream 子类，DwgStreamReaderBase 不会为其构建 64KB 缓冲）。
		/// </summary>
		public DwgLazySectionView CreateView()
		{
			return new DwgLazySectionView(this);
		}

		private void LaunchPrefetch(int page, TaskCompletionSource<byte[]> tcs)
		{
			Task.Run(() =>
			{
				try
				{
					Stopwatch sw = Stopwatch.StartNew();
					FileStream f = _workerFile.Value;
					ValueTuple<byte[], int> res = DwgLZ77AC18Decompressor.DecompressPageToBuffer(f, _filePos[page]);
					long ticks = sw.ElapsedTicks;
					byte[] data = res.Item1;
					int written = res.Item2;
					if (written != data.Length)
						Array.Resize(ref data, written);
					StorePageWorker(page, data, written, tcs, ticks);
				}
				catch (Exception ex)
				{
					ResetPageWorker(page, tcs, ex);
				}
			});
		}

		/// <summary>
		/// 确保某页已解压并返回其数据。主线程调用：命中→直接返回；
		/// 后台预取中→等待其完成（失败则回退内联）；未启动→主线程同步解压。
		/// </summary>
		private byte[] EnsurePage(int page)
		{
			Interlocked.Increment(ref _ensureCount);

			while (true)
			{
				TaskCompletionSource<byte[]> tcs;
				bool claimedInline = false;
				lock (_gate)
				{
					if (_state[page] == StateCached)
					{
						Interlocked.Increment(ref _hitCount);
						AutoWindowRecord(true);
						LruTouch(page);
						return _data[page];
					}
					if (_state[page] == StateLoading)
					{
						tcs = _pending[page];
					}
					else
					{
						//主线程内联解压（此时页必然未启动，无竞争）
						tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
						_state[page] = StateLoading;
						_pending[page] = tcs;
						claimedInline = true;
					}
				}

				if (claimedInline)
				{
					try
					{
						Stopwatch sw = Stopwatch.StartNew();
						ValueTuple<byte[], int> res = DwgLZ77AC18Decompressor.DecompressPageToBuffer(_mainFile, _filePos[page]);
						long ticks = sw.ElapsedTicks;
						byte[] data = res.Item1;
						int written = res.Item2;
						if (written != data.Length)
							Array.Resize(ref data, written);
						StorePageMain(page, data, written, tcs);
						Interlocked.Add(ref _decompressTicks, ticks);
						Interlocked.Increment(ref _missCount);
						lock (_gate)
						{
							AutoWindowRecord(false);
						}
						return data;
					}
					catch (Exception ex)
					{
						//内联失败：恢复未读状态并抛出（与原 eager 路径语义一致，响亮失败）。
						//TCS 取消而非设异常：此刻无等待者（只有主线程在跑内联），避免未观察任务异常噪声。
						lock (_gate)
						{
							if (_state[page] == StateLoading && _pending[page] == tcs)
							{
								_state[page] = StateNone;
								_pending[page] = null;
							}
						}
						tcs.TrySetCanceled();
						throw ex;
					}
				}

				//后台预取正在解压：等待结果；失败则下一轮循环回退为内联解压
				try
				{
					byte[] data = tcs.Task.GetAwaiter().GetResult();
					Interlocked.Increment(ref _prefetchWaitCount);
					lock (_gate)
					{
						if (_state[page] == StateCached)
							LruTouch(page);
						AutoWindowRecord(true); //预取及时=主线程未付出解压成本，计为命中
					}
					return data;
				}
				catch (Exception)
				{
					//预取失败/被取消：回退内联
					Interlocked.Increment(ref _inlineFallbackCount);
				}
			}
		}

		/// <summary>主线程内联解压完成后的存储（主线程即 owner）。</summary>
		private void StorePageMain(int page, byte[] data, int written, TaskCompletionSource<byte[]> tcs)
		{
			lock (_gate)
			{
				_state[page] = StateCached;
				_data[page] = data;
				_pending[page] = null;
				_cachedBytes += written;
				if (_cachedBytes > _peakCachedBytes)
					_peakCachedBytes = _cachedBytes;
				LruAddHead(page);
				EvictIfNeeded();
			}
			tcs.TrySetResult(data);
		}

		/// <summary>后台预取完成后的存储（需 owner 校验：期间该页可能被主线程内联抢先完成）。</summary>
		private void StorePageWorker(int page, byte[] data, int written, TaskCompletionSource<byte[]> tcs, long ticks)
		{
			bool owner;
			lock (_gate)
			{
				owner = (_state[page] == StateLoading && _pending[page] == tcs);
				if (owner)
				{
					_state[page] = StateCached;
					_data[page] = data;
					_pending[page] = null;
					_cachedBytes += written;
					if (_cachedBytes > _peakCachedBytes)
						_peakCachedBytes = _cachedBytes;
					LruAddHead(page);
					EvictIfNeeded();
					Interlocked.Add(ref _decompressTicks, ticks);
					Interlocked.Increment(ref _missCount);
					Interlocked.Increment(ref _prefetchDoneCount);
					AutoWindowRecord(false);
				}
				_inFlight--;
			}
			if (owner)
				tcs.TrySetResult(data);
			else
				tcs.TrySetCanceled();
		}

		/// <summary>后台预取失败：恢复未读状态（若仍为 owner），主线程将回退内联。</summary>
		private void ResetPageWorker(int page, TaskCompletionSource<byte[]> tcs, Exception ex)
		{
			lock (_gate)
			{
				if (_state[page] == StateLoading && _pending[page] == tcs)
				{
					_state[page] = StateNone;
					_pending[page] = null;
				}
				_inFlight--;
				Interlocked.Increment(ref _prefetchResetCount);
			}
			tcs.TrySetException(ex);
		}

		#region 页定位
		private int PageForOffset(long offset)
		{
			//二分：最大的 i 使 _pageStart[i] <= offset（页平铺连续，offset 必然落在某页内）
			int lo = 0;
			int hi = _pageCount - 1;
			while (lo < hi)
			{
				int mid = (lo + hi + 1) >> 1;
				if (_pageStart[mid] <= offset)
					lo = mid;
				else
					hi = mid - 1;
			}
			return lo;
		}

		#endregion

		#region LRU 与逐出（均须在 _gate 下调用）

		private void LruAddHead(int page)
		{
			if (_lruHead < 0)
			{
				_lruHead = page;
				_lruTail = page;
				_lruPrev[page] = -1;
				_lruNext[page] = -1;
				_lruCount = 1;
				return;
			}
			int oldHead = _lruHead;
			_lruHead = page;
			_lruPrev[page] = -1;
			_lruNext[page] = oldHead;
			_lruPrev[oldHead] = page;
			_lruCount++;
		}

		private void LruRemove(int page)
		{
			int prev = _lruPrev[page];
			int next = _lruNext[page];
			if (prev >= 0)
				_lruNext[prev] = next;
			else
				_lruHead = next;
			if (next >= 0)
				_lruPrev[next] = prev;
			else
				_lruTail = prev;
			_lruPrev[page] = -1;
			_lruNext[page] = -1;
			_lruCount--;
		}

		private void LruTouch(int page)
		{
			if (_lruHead == page)
				return;
			LruRemove(page);
			LruAddHead(page);
		}

		private void EvictIfNeeded()
		{
			switch (_mode)
			{
				case DwgLazyPageCacheMode.All:
					return;
				case DwgLazyPageCacheMode.EvictAfterUse:
					while (_lruCount > _evictWindowPages)
						EvictTail();
					return;
				default: //Lru / Auto：字节预算
					while (_cachedBytes > _byteBudget && _lruCount > 0)
						EvictTail();
					return;
			}
		}

		private void EvictTail()
		{
			int victim = _lruTail;
			if (victim < 0)
				return;
			LruRemove(victim);
			_cachedBytes -= _pageLen[victim];
			_data[victim] = null;
			_state[victim] = StateNone;
		}

		#endregion

		#region Auto 自适应（均须在 _gate 下调用）

		private void AutoWindowRecord(bool hit)
		{
			if (_mode != DwgLazyPageCacheMode.Auto)
				return;
			if (hit)
				Interlocked.Increment(ref _autoWindowHits);
			else
				Interlocked.Increment(ref _autoWindowMisses);
			if (Interlocked.Increment(ref _autoWindowCalls) >= 8192)
				AutoAdaptLocked();
		}

		private void AutoAdaptLocked()
		{
			long calls = Interlocked.Exchange(ref _autoWindowCalls, 0);
			long hits = Interlocked.Exchange(ref _autoWindowHits, 0);
			long misses = Interlocked.Exchange(ref _autoWindowMisses, 0);
			long total = hits + misses;
			if (total < 256)
				return; //窗口样本太少，不调整

			double rate = (double)hits / (double)total;
			if (rate > 0.97)
			{
				//命中率极高：缓存富裕，减半预算（下限 64MB）
				long nb = Math.Max(64L << 20, _byteBudget / 2);
				if (nb < _byteBudget)
				{
					_byteBudget = nb;
					EvictIfNeeded();
				}
			}
			else if (rate < 0.90)
			{
				//命中率偏低：加倍预算（上限 4GB）
				long nb = Math.Min(4L << 30, _byteBudget * 2);
				if (nb > _byteBudget)
					_byteBudget = nb;
			}
		}

		#endregion

		#region Stream 重写

		public override bool CanRead
		{
			get { return true; }
		}

		public override bool CanSeek
		{
			get { return true; }
		}

		public override bool CanWrite
		{
			get { return false; }
		}

		public override long Length
		{
			get { return _length; }
		}

		public override long Position
		{
			get { return _position; }
			set { Seek(value, SeekOrigin.Begin); }
		}

		// [PERF] 避免 Stream.ReadByte 默认实现的每次 new byte[1] 分配（位读路径逐字节调用）。
		public override int ReadByte()
		{
			long pos = _position;
			int n = ReadAt(ref pos, _oneByte, 0, 1);
			if (n > 0)
				_position = pos;
			return (n > 0) ? _oneByte[0] : -1;
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			return ReadAt(ref _position, buffer, offset, count);
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			long basePos;
			switch (origin)
			{
				case SeekOrigin.Begin:
					basePos = 0;
					break;
				case SeekOrigin.Current:
					basePos = _position;
					break;
				case SeekOrigin.End:
					basePos = _length;
					break;
				default:
					throw new ArgumentException("Invalid SeekOrigin", "origin");
			}
			_position = basePos + offset;
			return _position;
		}

		public override void Flush()
		{
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && !_disposed)
			{
				_disposed = true;

				//等待后台预取任务收尾（上限 2s，避免读中断时无限等待）
				Stopwatch sw = Stopwatch.StartNew();
				while (true)
				{
					int inFlight;
					lock (_gate)
						inFlight = _inFlight;
					if (inFlight == 0 || sw.ElapsedMilliseconds > 2000)
						break;
					Thread.Sleep(5);
				}

				try
				{
					_mainFile.Dispose();
				}
				catch
				{
				}
				_workerFile.Dispose();
			}
			base.Dispose(disposing);
		}

		#endregion
	}

	/// <summary>
	/// [PERF] <see cref="DwgLazySectionStream"/> 之上的每对象读视图。
	/// 继承 MemoryStream 是为了让 DwgStreamReaderBase 判定"不需要自建 64KB 缓冲"
	/// （其 _buf 仅对非 MemoryStream 流分配）；所有读/定位透明转发到 owner 流
	/// （owner 按页号二分定位页，按需解压/读取页数据）。
	/// </summary>
	internal sealed class DwgLazySectionView : MemoryStream
	{
		private DwgLazySectionStream _owner;
		private long _position;
		private readonly byte[] _oneByte = new byte[1];

		public DwgLazySectionView(DwgLazySectionStream owner)
			: base(new byte[1])
		{
			_owner = owner;
		}

		/// <summary>重置到 owner（DwgObjectReader 视图池化复用）。</summary>
		public void Reset(DwgLazySectionStream owner)
		{
			_owner = owner;
			_position = 0;
		}

		public override bool CanRead
		{
			get { return _owner != null; }
		}

		public override bool CanSeek
		{
			get { return true; }
		}

		public override bool CanWrite
		{
			get { return false; }
		}

		public override long Length
		{
			get { return (_owner != null) ? _owner.Length : 0; }
		}

		public override long Position
		{
			get { return _position; }
			set { Seek(value, SeekOrigin.Begin); }
		}

		public override int ReadByte()
		{
			DwgLazySectionStream owner = _owner;
			if (owner == null)
				return -1;
			long pos = _position;
			int n = owner.ReadAt(ref pos, _oneByte, 0, 1);
			if (n > 0)
				_position = pos;
			return (n > 0) ? _oneByte[0] : -1;
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			DwgLazySectionStream owner = _owner;
			if (owner == null || count <= 0)
				return 0;
			long pos = _position;
			int n = owner.ReadAt(ref pos, buffer, offset, count);
			if (n > 0)
				_position = pos;
			return n;
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			long basePos;
			switch (origin)
			{
				case SeekOrigin.Begin:
					basePos = 0;
					break;
				case SeekOrigin.Current:
					basePos = _position;
					break;
				case SeekOrigin.End:
					basePos = Length;
					break;
				default:
					throw new ArgumentException("Invalid SeekOrigin", "origin");
			}
			_position = basePos + offset;
			return _position;
		}

		public override void Flush()
		{
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}
	}

	/// <summary>
	/// [PERF] 惰性页解压读模式诊断（internal；DwgReadPerf 通过反射读取）。
	/// 与 DwgReadDiagnostics 同样的访问方式——强签名程序集无法对未签名程序集暴露 internal。
	/// </summary>
	internal static class DwgLazyReadDiagnostics
	{
		private static DwgLazySectionStream _instance;

		public static void Register(DwgLazySectionStream stream)
		{
			_instance = stream;
		}

		public static void Reset()
		{
			_instance = null;
		}

		public static bool Active
		{
			get { return _instance != null; }
		}

		public static int PageCount
		{
			get { return (_instance != null) ? _instance._pageCount : 0; }
		}

		public static long DecompressedLength
		{
			get { return (_instance != null) ? _instance._length : 0; }
		}

		public static long EnsureCount
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._ensureCount) : 0; }
		}

		public static long HitCount
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._hitCount) : 0; }
		}

		public static long MissCount
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._missCount) : 0; }
		}

		public static long PrefetchWaitCount
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._prefetchWaitCount) : 0; }
		}

		public static long InlineFallbackCount
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._inlineFallbackCount) : 0; }
		}

		/// <summary>累计页解压耗时（ms，含主线程内联与后台预取）。</summary>
		public static long DecompressMs
		{
			get
			{
				if (_instance == null)
					return 0;
				long ticks = Interlocked.Read(ref _instance._decompressTicks);
				return ticks / (Stopwatch.Frequency / 1000);
			}
		}

		public static long CachedBytes
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._cachedBytes) : 0; }
		}

		public static long PeakCachedBytes
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._peakCachedBytes) : 0; }
		}

		public static int InFlight
		{
			get { return (_instance != null) ? _instance._inFlight : 0; }
		}

		/// <summary>当前字节预算（Auto 模式下会自适应变化）。</summary>
		public static long ByteBudget
		{
			get { return (_instance != null) ? _instance._byteBudget : 0; }
		}

		public static string Mode
		{
			get { return (_instance != null) ? _instance._mode.ToString() : "None"; }
		}

		/// <summary>[PERF] 快路径命中次数（整个读段落在当前已加载页窗口内）。</summary>
		public static long FastHitCount
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._fastHitCount) : 0; }
		}

		/// <summary>[PERF] 走通用路径的 ReadAt 次数（含二分定位/加锁/LRU 触碰）。</summary>
		public static long GeneralCallCount
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._generalCallCount) : 0; }
		}

		/// <summary>[PERF] 通用路径中"游标落后于窗口"（回退/窗口失效）的次数。</summary>
		public static long GeneralBackwardCount
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._generalBackwardCount) : 0; }
		}

		/// <summary>[PERF] 通用路径中"读段越过窗口页末尾"（跨页）的次数。</summary>
		public static long GeneralCrossEndCount
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._generalCrossEndCount) : 0; }
		}

		/// <summary>[PERF] 后台预取成功认领（启动解压）的页数。</summary>
		public static long PrefetchClaimCount
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._prefetchClaimCount) : 0; }
		}

		/// <summary>[PERF] 后台预取成功完成（数据入缓存）的页数。</summary>
		public static long PrefetchDoneCount
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._prefetchDoneCount) : 0; }
		}

		/// <summary>[PERF] 后台预取失败回退（页恢复未读状态）的次数。</summary>
		public static long PrefetchResetCount
		{
			get { return (_instance != null) ? Interlocked.Read(ref _instance._prefetchResetCount) : 0; }
		}
	}
}
