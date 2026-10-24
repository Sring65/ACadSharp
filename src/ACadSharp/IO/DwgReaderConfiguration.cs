using System;
using ACadSharp.IO.DWG;

namespace ACadSharp.IO;

/// <summary>
/// Configuration for reading DWG files.
/// </summary>
public class DwgReaderConfiguration : CadReaderConfiguration
{
	/// <summary>
	/// Use the Standard Cycling Redundancy Check to verify the integrity of the file, default value is set to false.
	/// </summary>
	/// <remarks>
	/// DWG file format uses a modification of a standard Cyclic Redundancy Check as an error detecting mechanism, 
	/// if this flag is enabled the reader will perform this verification to detect any possible error, but it will greatly increase the reading time.
	/// </remarks>
	[Obsolete("This property is obsolete and will be removed in future versions.")]
	public bool CrcCheck { get; set; } = false;

	/// <summary>
	/// If set to false the reader will skip the summary info section.
	/// </summary>
	/// <value>
	/// default: true
	/// </value>
	public bool ReadSummaryInfo { get; set; } = true;

	/// <summary>
	/// Where the decompressed section image (the decompressed AcDbObjects stream) is stored.
	/// </summary>
	/// <remarks>
	/// <para><c>false</c> (default) = keep the decompressed image in memory (HugeMemoryStream); seeking on the in-memory stream is extremely fast, so performance is favored.</para>
	/// <para><c>true</c> = write the decompressed image to a temporary file (FileStream), minimizing memory usage and avoiding out-of-memory on large files at the cost of disk I/O for decompression writes and handle-based seeks, which slightly lowers performance. Applies only to the largest section; smaller sections stay in memory.</para>
	/// <para>After reading completes, the temporary file is removed automatically with the reader (FileOptions.DeleteOnClose).</para>
	/// </remarks>
	/// <value>
	/// default: false
	/// </value>
	public bool UseDiskSectionCache { get; set; } = false;

	/// <summary>
	/// Retain the object-section patch source (<see cref="DwgObjectPatchSource"/>) at read time for incremental-save reuse.
	/// </summary>
	/// <remarks>
	/// <para>When enabled, the reader copies the decompressed object-section (AcDbObjects) image into a temporary file held by the caller and records each object's byte range and page metadata, so the writer can skip recompressing unchanged pages.</para>
	/// <para>Valid only for R2004 (AC1018) and later; lower versions are ignored automatically.</para>
	/// <para>Cost: one extra sequential copy the same size as the object section (on the order of 10MB/10ms on NVMe).</para>
	/// </remarks>
	/// <value>
	/// default: false
	/// </value>
	public bool RetainPatchSource { get; set; } = false;

	/// <summary>
	/// Parallelize object-section (AcDbObjects) decoding.
	/// </summary>
	/// <remarks>
	/// <para>Object decoding is the main cost of reading (~94% of read time; 2.8 million objects take about 8.8 minutes serially).
	/// Each object is located absolutely by "handle to offset", decoded on an independent cloned view stream, and decoding only records
	/// reference handle values (reference resolution happens in the BuildDocument phase) - object decoding is self-contained and order-independent.</para>
	/// <para><c>true</c> = multi-worker parallel decoding (shared read-only decompressed image + atomic claim-set dedup + shared reference queue
	/// to discover new objects); <see cref="AddTemplate"/> is run serially by the main thread after all workers finish,
	/// eliminating builder-dictionary contention between workers. Enabled only for R2004 (AC1018) and later
	/// (that version's entities have no prev/next chain, so single-entity decoding is self-contained); lower versions fall back to the sequential path.</para>
	/// </remarks>
	/// <value>
	/// default: false
	/// </value>
	public bool ParallelDecode { get; set; } = false;

	/// <summary>
	/// [PERF] 对象节（AcDbObjects）惰性页解压读模式：打开文件只读头部/页目录（亚秒级完成），
	/// 不预解压整节；按 handle 读取对象时，才按需解压对象所在页；并按读取顺序异步多线程预解压后续页。
	/// </summary>
	/// <remarks>
	/// <para><c>false</c>（默认）= 原 eager 行为：读对象前先整节解压（BGDY 约 816MB、数秒）。</para>
	/// <para><c>true</c> = 惰性页解压。仅对 R2004（AC1018）且节确实被压缩、文件为磁盘文件时生效，否则自动回退 eager。</para>
	/// <para>收益：打开时间从"整节解压+头扫描"降为亚秒级；内存中不再常驻整节映像，
	/// 页缓存占用由 <see cref="LazyPageCacheMode"/>/预算控制（默认 LRU 512MB）；页解压与对象解码重叠（预取）。</para>
	/// <para>代价：页被逐出后再读需重新解压（每页约 0.3–0.5ms，命中率低时总时间可能高于 eager）。</para>
	/// <para>与 <see cref="ParallelDecode"/>、<see cref="UseDiskSectionCache"/> 互斥：同时开启时本开关自动失效并回退 eager（Console.Error 提示）。</para>
	/// <para>与 <see cref="RetainPatchSource"/> 兼容：增量保存所需的页头元数据通过"仅头部扫描"获得（不解压页体，一次性约 1–1.5s）；
	/// 结束时构建 patch source 会对惰性流顺序读一遍（即全节按需解压一遍）。</para>
	/// </remarks>
	/// <value>
	/// default: false
	/// </value>
	public bool UseLazyObjectSection { get; set; } = false;

	/// <summary>
	/// [PERF] 惰性页解压模式的页缓存策略（仅 <see cref="UseLazyObjectSection"/> 为 true 时有效）。
	/// </summary>
	/// <value>
	/// default: Lru
	/// </value>
	public DwgLazyPageCacheMode LazyPageCacheMode { get; set; } = DwgLazyPageCacheMode.Lru;

	/// <summary>
	/// [PERF] 惰性页缓存字节预算（<c>LazyPageCacheMode</c> 为 Lru/Auto 时生效）。
	/// </summary>
	/// <remarks>
	/// <para>0 = 使用默认值：Lru 模式 512MB；Auto 模式从 min(本值, 1GB) 起步并自适应（64MB–4GB）。</para>
	/// <para>内存收益：预算越大命中率越高、总时间越短；内存占用≈预算（加上当前正在解码的对象图）。</para>
	/// </remarks>
	/// <value>
	/// default: 0（Lru: 512MB / Auto: 1GB 起步）
	/// </value>
	public int LazyPageCacheBytes { get; set; } = 0;

	/// <summary>
	/// [PERF] <c>LazyPageCacheMode.EvictAfterUse</c> 的滑动窗口页数：仅保留最近读到的 N 页。
	/// </summary>
	/// <remarks>
	/// 每页约 29KB（BGDY），64 页≈1.9MB。窗口越大命中率越高、内存略增。
	/// </remarks>
	/// <value>
	/// default: 64
	/// </value>
	public int LazyEvictWindowPages { get; set; } = 64;

	/// <summary>
	/// [PERF] 惰性模式预取的"前瞻页数"：从当前读取游标起，向后多少页（pages ahead）纳入预取窗口。
	/// </summary>
	/// <remarks>
	/// <para>DwgObjectReader 每解码 64 个对象把"读取游标之后 N 页"窗口内尚未解压的页提交给惰性流异步解压
	/// （N = 本值，默认 512 页 ≈ 15MB 解压映像）；实际并发任务数受 <see cref="LazyPrefetchWorkers"/> 上限约束，
	/// 窗口内超并发上限的页本轮跳过、下轮窗口自然重试。</para>
	/// <para>性能收益：预取越深，主线程"等待页解压"的概率越低；过深无额外收益（页在读取前已解压完毕），且浪费少量后台解压算力。</para>
	/// </remarks>
	/// <value>
	/// default: 512
	/// </value>
	public int LazyPrefetchDepth { get; set; } = 512;

	/// <summary>
	/// [PERF] 惰性模式后台预取的并发任务数（同时进行的页解压数）。
	/// </summary>
	/// <remarks>
	/// 每页解压约 0.3–0.5ms（BGDY 约 29KB 页），4 任务即可喂饱单线程解码；核多时可调大，但页解压是 CPU+磁盘混合，超过核数收益递减。
	/// </remarks>
	/// <value>
	/// default: 4
	/// </value>
	public int LazyPrefetchWorkers { get; set; } = 4;

	/// <summary>
	/// [PERF] 文档构建完成后丢弃对象模板字典（templatesMap / cadObjectsTemplates / dictionaryTemplates /
	/// tableEntryTemplates / tableTemplates / tableTemplates 等），释放构建期暂存的对象副本。
	/// </summary>
	/// <remarks>
	/// <para>内存收益：最终文档与模板共享同一批 CadObject 实例（模板 Build 时直接挂入文档），
	/// 因此可安全释放的是构建期的 6 个 handle→模板 索引字典 + 模板包装对象（含 EData 索引/Reactors 集合等冗余元数据）
	/// + BlockRecordTemplates 列表；BGDY 2.8M 对象实测约释放 1–3GB（以 DwgReadPerf 实测为准）。</para>
	/// <para>时机：Read() 内 BuildDocument/BuildDataStorage/ApplyR2000Gradients 全部完成后、MarkBuildEnd 之前。</para>
	/// <para>代价：无运行时功能损失——模板字典只在构建阶段被消费；文档图本身不受影响，
	/// 增量保存（RetainPatchSource）走的是 patch source 的 handle→页映射，不依赖这些字典。</para>
	/// </remarks>
	/// <value>
	/// default: false
	/// </value>
	public bool DiscardTemplatesAfterBuild { get; set; } = false;
}
