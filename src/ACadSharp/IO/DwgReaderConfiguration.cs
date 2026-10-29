using System;
using ACadSharp.IO.DWG;

namespace ACadSharp.IO;

/// <summary>
/// Configuration for reading DWG files.
/// </summary>
public class DwgReaderConfiguration : CadReaderConfiguration
{
	/// <summary>
	/// Whether to check the checksum of the data.
	/// </summary>
	[Obsolete]
	public bool CrcCheck { get; set; } = false;

	/// <summary>
	/// Whether to read the summary information section.
	/// </summary>
	/// <remarks>
	/// When the option is enabled the reader will try to read the summary information
	/// section. This section is optional and may not be present in the file. When the
	/// section is not present, no error is raised and the summary information is left
	/// empty.
	/// </remarks>
	public bool ReadSummaryInfo { get; set; } = true;

	/// <summary>
	/// [PATCH] Store the objects-section (AcDbObjects) decompressed image in a temp file
	/// (pre-allocated, DeleteOnClose) instead of in memory. The temp file lives on disk
	/// for the lifetime of the reader, so the reader must be disposed promptly; the
	/// image is not retained in the document after the read, so saving the document is
	/// unaffected.
	/// </summary>
	/// <remarks>
	/// <para>The DWG objects section is the largest section of a file (often several GB
	/// for big drawings); holding its full decompressed image in memory doubles the peak
	/// working set (decompressed image + final object graph) and can exceed the
	/// physical-memory cap of a 32-bit process (2GB, or 4GB with the /LARGEADDRESSAWARE
	/// flag).</para>
	/// <para>Measured: a 25GB objects section (BGDY.dwg, 3,849,331 objects) read with the
	/// in-memory image peaks at ~47.5GB and OOMs; with the disk cache the same read
	/// completes with a ~23.2GB peak (the object graph itself) and a ~52MB working set
	/// after the reader is disposed. The section is still read in full, so the read time
	/// is comparable to the in-memory mode (slightly slower).</para>
	/// </remarks>
	public bool UseDiskSectionCache { get; set; } = false;

	/// <summary>
	/// [PATCH] Keep a raw-byte snapshot of the source objects section (the compressed
	/// pages, re-masked per page) and a snapshot of the decompressed image while
	/// reading, so that <see cref="DwgWriter.Write(string, ACadSharp.CadDocument, DwgWriterConfiguration, NotificationEventHandler)"/>
	/// can run the incremental path (re-serialize only the dirty objects and stitch the
	/// untouched pages back into the output).
	/// </summary>
	/// <remarks>
	/// <para>The reader builds the patch source on dispose: it snapshots the raw
	/// compressed pages and reads the lazy objects stream through to the end (or
	/// snapshots the decompressed image on the eager path). The cost is one extra full
	/// read of the objects section (~0.2s for BGDY, ~27.7k pages) plus the temp-file
	/// space for the snapshots (DeleteOnClose, auto-cleaned on dispose/crash).</para>
	/// <para>For the incremental save to be valid the document must not gain or lose
	/// objects (only in-place modification); the writer validates this and falls back
	/// to the full serialization otherwise.</para>
	/// <para>Independent of the page-cache mode: the raw pages come from the source file
	/// (re-masked per page, identical to the file bytes), so the patch source is
	/// correct in both eager and lazy read modes.</para>
	/// </remarks>
	public bool RetainPatchSource { get; set; } = false;

	/// <summary>
	/// Parallelize object-section (AcDbObjects) decoding.
	/// </summary>
	/// <remarks>
	/// <para>Object decoding is the main cost of reading (~94% of read time; 2.8 million objects take about 8.8 minutes serially).
	/// Each object is located absolutely by "handle to offset", decoded on an independent cloned view stream, and decoding only records
	/// reference handle values (reference resolution happens in the BuildDocument phase) - object decoding is self-contained and order-independent.</para>
	/// <para><c>true</c> = multi-worker parallel decoding (shared read-only decompressed image + atomic claim-set dedup + shared reference queue
	/// to discover new objects); <see cref="CadDocumentBuilder.AddTemplate(ACadSharp.IO.Templates.ICadObjectTemplate)"/> is run serially by the main thread after all workers finish,
	/// eliminating builder-dictionary contention between workers. Enabled only for R2004 (AC1018) and later
	/// (that version's entities have no prev/next chain, so single-entity decoding is self-contained); lower versions fall back to the sequential path.</para>
	/// </remarks>
	/// <value>
	/// default: false
	/// </value>
	public bool ParallelDecode { get; set; } = false;

	/// <summary>
	/// [PERF] Lazy page-decompression read mode for the objects section (AcDbObjects):
	/// opening the file only reads the header and the page directory (sub-second) and
	/// decompresses no page up front; a page is decompressed on demand when the objects
	/// in it are read (by handle), and the following pages are prefetched
	/// asynchronously (multi-threaded, in read order).
	/// </summary>
	/// <remarks>
	/// <para><c>false</c> (default) = the original eager behavior: the whole section is
	/// decompressed before the objects are read (BGDY: ~816MB, a few seconds).</para>
	/// <para><c>true</c> = lazy page decompression. Effective only on R2004 (AC1018) and
	/// later, when the section is actually compressed and the file is a disk file; on
	/// any other combination it falls back to eager automatically.</para>
	/// <para>Benefit: the open time drops from "decompress the whole section + scan the
	/// header" to sub-second; the whole section image no longer stays resident in memory
	/// - the page cache footprint is controlled by <see cref="LazyPageCacheMode"/> /
	/// budget (default LRU 512MB); page decompression overlaps the object decoding
	/// (prefetch).</para>
	/// <para>Cost: a page evicted from the cache must be decompressed again when it is
	/// read back (~0.3-0.5ms per page; with a low hit rate the total time can exceed the
	/// eager mode).</para>
	/// <para>Mutually exclusive with <see cref="ParallelDecode"/> and
	/// <see cref="UseDiskSectionCache"/>: when combined with either, this switch is
	/// ignored and the reader falls back to eager (Console.Error hint).</para>
	/// <para>Compatible with <see cref="RetainPatchSource"/>: the page-header metadata
	/// needed for the incremental save is obtained with a "headers-only scan" (no page
	/// body is decompressed; a one-off ~1-1.5s), and the patch source is built on
	/// dispose by reading the lazy stream through once (i.e. the whole section is
	/// decompressed on demand exactly once).</para>
	/// </remarks>
	public bool UseLazyObjectSection { get; set; } = false;

	/// <summary>
	/// [PERF] Page-cache strategy of the lazy objects section (only effective when
	/// <see cref="UseLazyObjectSection"/> is true).
	/// </summary>
	public DwgLazyPageCacheMode LazyPageCacheMode { get; set; } = DwgLazyPageCacheMode.Lru;

	/// <summary>
	/// [PERF] Page-cache budget (in bytes) of the lazy objects section (only effective
	/// when <see cref="UseLazyObjectSection"/> is true and <see cref="LazyPageCacheMode"/>
	/// is <c>Lru</c> or <c>Auto</c>).
	/// </summary>
	/// <remarks>
	/// <para>0 = built-in default: 512MB in <c>Lru</c> mode; in <c>Auto</c> mode the cache
	/// starts at min(value, 1GB) and adapts between 64MB and 4GB.</para>
	/// <para>Memory trade-off: a larger budget means a higher hit rate and a shorter total
	/// read time; the memory footprint is about the budget (plus the object graph of the
	/// objects currently being decoded).</para>
	/// </remarks>
	/// <value>
	/// default: 0 (Lru: 512MB / Auto: starts at 1GB)
	/// </value>
	public int LazyPageCacheBytes { get; set; } = 0;

	/// <summary>
	/// [PERF] Sliding window (page count) of the <see cref="DwgLazyPageCacheMode.EvictAfterUse"/>
	/// cache mode: only the <c>N</c> most recently read pages are kept.
	/// </summary>
	/// <remarks>
	/// Each page is ~29KB (BGDY), so 64 pages = ~1.9MB. A larger window means a higher
	/// hit rate at a slightly higher memory cost.
	/// </remarks>
	/// <value>
	/// default: 64
	/// </value>
	public int LazyEvictWindowPages { get; set; } = 64;

	/// <summary>
	/// [PERF] "Look-ahead" window (in pages) of the lazy-mode prefetch: how many pages
	/// ahead of the current read cursor are pulled into the prefetch window.
	/// </summary>
	/// <remarks>
	/// <para>Every time DwgObjectReader decodes 64 objects it submits the not-yet-decompressed
	/// pages inside the "N pages after the read cursor" window (N = this value, default 512
	/// pages = ~15MB decompressed image) to the lazy stream for asynchronous decompression;
	/// the actual number of concurrent tasks is capped by <see cref="LazyPrefetchWorkers"/> -
	/// the pages beyond the cap are skipped this round and naturally retried by the next window.</para>
	/// <para>Performance: the deeper the prefetch, the lower the chance that the main thread
	/// waits for a page decompression; a deeper window brings no extra benefit (the pages are
	/// already decompressed before they are read) and wastes a little background CPU.</para>
	/// </remarks>
	/// <value>
	/// default: 512
	/// </value>
	public int LazyPrefetchDepth { get; set; } = 512;

	/// <summary>
	/// [PERF] Number of concurrent background prefetch tasks (simultaneous page
	/// decompressions) of the lazy mode.
	/// </summary>
	/// <remarks>
	/// A page decompresses in ~0.3-0.5ms (BGDY, ~29KB pages); 4 tasks are enough to feed a
	/// single decoding thread. Raise it on many-core machines, but page decompression is a
	/// CPU+disk mix, so the gain diminishes beyond the core count.
	/// </remarks>
	/// <value>
	/// default: 4
	/// </value>
	public int LazyPrefetchWorkers { get; set; } = 4;

	/// <summary>
	/// [PERF] Size threshold (in bytes) of the decompressed objects-section image that
	/// triggers the automatic degradation of
	/// <see cref="CadReaderConfiguration.AutoDegradeOnMemoryPressure"/> regardless of the measured memory
	/// pressure (an image that large would dominate the process working set on typical
	/// machines).
	/// </summary>
	/// <value>
	/// default: 0 = 4GB
	/// </value>
	public long AutoDegradeSectionSizeBytes { get; set; } = 0;

	/// <summary>
	/// [PERF] Whether to release the per-class object templates (the
	/// <see cref="ACadSharp.IO.Templates.ICadObjectTemplate"/> instances) after the
	/// document has been built.
	/// </summary>
	/// <remarks>
	/// <para>The reader keeps one template per class (a prototype object plus the
	/// per-class parse context) while reading; for a drawing with a large number of
	/// classes (e.g. 781 classes for BGDY) the templates stay referenced by the
	/// document after the read and never become garbage (the writer needs them to
	/// re-serialize the objects). When the document is read-only, the templates are
	/// dead weight: measured, they hold ~174MB of live objects plus a large amount of
	/// string/table references.</para>
	/// <para>Set <c>true</c> when the document is read-only (statistics, queries,
	/// rendering) to release the templates at the end of the read; the reader
	/// automatically switches to the streaming path (no handle map, no template cache),
	/// so the read-time working set stays lower too. Saving the document is impossible
	/// afterwards (the writer requires the templates); reading is unaffected.</para>
	/// <para>Set <c>false</c> (default) to keep the upstream behavior: the templates are
	/// retained and the document can be saved (including the incremental
	/// <see cref="DwgWriter.Write(string, ACadSharp.CadDocument, DwgWriterConfiguration, NotificationEventHandler)"/>
	/// path).</para>
	/// </remarks>
	/// <value>
	/// default: false
	/// </value>
	public bool DiscardTemplatesAfterBuild { get; set; } = false;
}
