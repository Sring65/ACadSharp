using System;
using System.Diagnostics;

namespace ACadSharp.IO
{
	/// <summary>
	/// [PATCH] Working-set management for reading huge DXF/DWG files.
	/// Reading a multi-million-entity file allocates a lot of transient garbage (templates,
	/// maps, parsed strings); without periodic collections the committed heap grows far beyond
	/// the live object graph (measured: ~23GB working set vs ~12.7GB live for a 1.9M-entity
	/// DXF with XData).
	/// </summary>
	/// <remarks>
	/// <para><see cref="Trim"/> forces a collection: gen0+gen1 for the periodic trims while a
	/// section is being read, full (gen2) at the read/write boundaries. On modern runtimes
	/// (.NET Core 3.0+) the garbage collector returns the fully-free segments to the operating
	/// system by itself, so a pure-managed, GC-based trim is enough - no native call is
	/// required (the original implementation relied on the deprecated psapi.dll
	/// EmptyWorkingSet, which has been removed).</para>
	/// <para><see cref="GetPressureInfo"/> exposes a TFM-safe memory-pressure signal
	/// (<c>System.GC.GetGCMemoryInfo()</c> on .NET Core/5+ TFMs; netstandard2.0/2.1 and
	/// net48 do not expose the API and report "not probed"). The readers use it to
	/// auto-degrade their strategy under pressure: the DWG
	/// reader switches the objects section to the lazy page-decompression mode (or to the
	/// on-disk section cache when the lazy mode is unavailable), and the DXF reader tightens
	/// the periodic GC cadence (<see cref="DxfReaderConfiguration.GCEveryNEntities"/>).</para>
	/// </remarks>
	public static class MemoryTrimmer
	{
		/// <summary>
		/// Pressure ratio (GC memory load / total available memory) at or above which the
		/// readers auto-degrade their reading strategy.
		/// </summary>
		public const double DefaultPressureRatio = 0.70;

		/// <summary>
		/// Optional log hook (set by the caller, e.g. exporter/bench tooling) to trace each
		/// trim's effect and each auto-degrade decision.
		/// </summary>
		public static Action<string> Log { get; set; }

		/// <summary>
		/// Snapshot of the process memory-pressure state.
		/// </summary>
		public readonly struct PressureInfo
		{
			/// <summary>Whether the probe ran (false where <c>GC.GetGCMemoryInfo</c> is unavailable, or on probe failure).</summary>
			public bool Probed { get; }

			/// <summary>Total memory available to the process, in bytes (0 = unknown).</summary>
			public long TotalAvailableMemoryBytes { get; }

			/// <summary>Memory the GC currently has loaded, in bytes (0 = unknown).</summary>
			public long MemoryLoadBytes { get; }

			/// <summary>MemoryLoadBytes / TotalAvailableMemoryBytes (0 = unknown).</summary>
			public double Ratio { get; }

			internal PressureInfo(bool probed, long totalAvailable, long memoryLoad)
			{
				this.Probed = probed;
				this.TotalAvailableMemoryBytes = totalAvailable;
				this.MemoryLoadBytes = memoryLoad;
				this.Ratio = totalAvailable > 0 ? (double)memoryLoad / totalAvailable : 0;
			}
		}

		/// <summary>
		/// Probe the current memory pressure. Where
		/// <c>System.GC.GetGCMemoryInfo()</c> is unavailable (netstandard2.0/2.1, net48)
		/// and on any probe failure the result reports <see cref="PressureInfo.Probed"/> =
		/// false; callers then fall back to the size-based triggers only.
		/// </summary>
		public static PressureInfo GetPressureInfo()
		{
#if NETSTANDARD2_0 || NETSTANDARD2_1 || NETFRAMEWORK
			// The netstandard APIs lack GC.GetGCMemoryInfo (it is a .NET Core 3.0+ API), and the
			// net48 reference assemblies in this build do not expose it either; report "not
			// probed" so the callers fall back to the size-based triggers only.
			return new PressureInfo(false, 0, 0);
#else
			try
			{
				GCMemoryInfo info = GC.GetGCMemoryInfo();
				return new PressureInfo(true, info.TotalAvailableMemoryBytes, info.MemoryLoadBytes);
			}
			catch
			{
				return new PressureInfo(false, 0, 0);
			}
#endif
		}

		/// <summary>
		/// True when the memory pressure is at or above <paramref name="ratioThreshold"/>.
		/// Returns false when the probe is unavailable (the size-based triggers should decide).
		/// </summary>
		public static bool IsUnderPressure(double ratioThreshold = DefaultPressureRatio)
		{
			PressureInfo info = GetPressureInfo();
			return info.Probed && info.Ratio >= ratioThreshold;
		}

		/// <summary>
		/// Trim the working set.
		/// </summary>
		/// <param name="full">
		/// true = full collection (gen2 + finalizers) for the read/write boundaries;
		/// false (default) = light gen0+gen1 collection for the periodic trims while a section
		/// is being read.
		/// </param>
		/// <remarks>
		/// Pure-managed: after the collection, .NET (Core) 3.0+ runtimes return the fully-free
		/// segments to the OS by themselves; on .NET Framework the committed working set may
		/// stay elevated after a collection (no supported managed API returns it), which is
		/// why the readers use <see cref="GetPressureInfo"/> to avoid growing the heap in the
		/// first place instead of relying on the trim to shrink it.
		/// </remarks>
		public static void Trim(bool full = false)
		{
			long wsBefore = wsMB();
			long managedBefore = GC.GetTotalMemory(false) / (1024 * 1024);

			var sw = Stopwatch.StartNew();
			if (full)
			{
				GC.Collect();
				GC.WaitForPendingFinalizers();
				GC.Collect();
			}
			else
			{
				GC.Collect(1);
			}
			sw.Stop();

			long wsAfter = wsMB();
			long managedAfter = GC.GetTotalMemory(false) / (1024 * 1024);

			Log?.Invoke($"[MemoryTrimmer] {(full ? "full" : "gen1")} WS {wsBefore}MB (managed {managedBefore}MB) -> {wsAfter}MB (managed {managedAfter}MB) after {sw.ElapsedMilliseconds}ms");
		}

		private static long wsMB() => Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
	}
}
