using System;
using System.Collections.Generic;
using System.IO;

namespace ACadSharp.IO.DWG
{
	/// <summary>
	/// [PATCH] Low-memory edit mode: splices a dirty record stream into the source decompressed
	/// objects-section image, keeping every untouched record byte-identical.
	/// <para>
	/// This is the slim sibling of <c>DwgWriter.tryWriteObjectsIncrementalDirty</c>: the original record
	/// offsets come from the Handles-section map (handle → decompressed offset, i.e. the value of the
	/// reader's <c>ctx.HandleMap</c>) instead of a pre-built <see cref="DwgObjectPatchSource.ObjectRanges"/>
	/// index, so no full-image object scan is required. Placement semantics are identical to the P0 path:
	/// equal-length → in-place overwrite; shorter → in-place + zero pad (the reader truncates per the MS size
	/// prefix); longer/absent → append at the section end + relocate in the handle map (the original location
	/// keeps dead bytes and its page is still reused).
	/// </para>
	/// <para>
	/// The handle map is mutated in place (relocated handles are updated); the caller owns the final use of
	/// that reference (the Handles-section rebuild must run on the mutated map).
	/// </para>
	/// </summary>
	internal static class DwgObjectsSectionSplicer
	{
		private const int ObjectsPageSize = 0x7400;

		/// <summary>A pending byte range to write into the final section image.</summary>
		private sealed class PendingWrite
		{
			public PendingWrite(long offset, int length, byte[] bytes)
			{
				this.Offset = offset;
				this.Length = length;
				this.Bytes = bytes;
			}

			public readonly long Offset;

			public readonly int Length;

			public readonly byte[] Bytes;
		}

		/// <summary>
		/// The splice outcome. <see cref="FinalStream"/> is an open temp file (DeleteOnClose) holding the new
		/// decompressed objects-section image; the caller feeds it to
		/// <c>DwgFileHeaderWriterAC18.AddSectionWithPageReuse</c> and disposes it after the file is written.
		/// </summary>
		public sealed class Result
		{
			public FileStream FinalStream;

			/// <summary>reusePage[i] = true ⇒ page i's compressed bytes are taken verbatim from the source file.</summary>
			public bool[] ReusePage;

			public long SectionLength;

			public int InPlaceObjects;

			public int RelocatedObjects;

			public int UnchangedObjects;

			public int ReusedPages;

			public int TotalPages;

			public long AppendedBytes;
		}

		/// <summary>
		/// Splices <paramref name="dirtyStream"/> (framed records, offsets from <paramref name="dirtyMap"/>)
		/// into a copy of the source objects-section image.
		/// </summary>
		/// <param name="patch">The patch source (image stream + raw page snapshots) built from the source file.</param>
		/// <param name="srcHandleMap">
		/// The Handles-section map (handle → decompressed offset of the record's size prefix). Mutated in place
		/// with relocations; returned as the same reference.
		/// </param>
		/// <param name="dirtyStream">Framed dirty records (no section preamble).</param>
		/// <param name="dirtyMap">handle → offset within <paramref name="dirtyStream"/>.</param>
		public static Result Splice(
			DwgObjectPatchSource patch,
			Dictionary<ulong, long> srcHandleMap,
			byte[] dirtyStream,
			Dictionary<ulong, long> dirtyMap)
		{
			if (patch == null)
				throw new ArgumentNullException(nameof(patch));
			if (srcHandleMap == null)
				throw new ArgumentNullException(nameof(srcHandleMap));
			if (patch.PageSize != ObjectsPageSize)
				throw new NotSupportedException($"低内存模式：对象段页大小 {patch.PageSize} 不受支持（期望 0x{ObjectsPageSize:X}）");

			var result = new Result();
			var patches = new List<PendingWrite>();
			var appends = new List<PendingWrite>();
			var relocations = new Dictionary<ulong, long>();
			long appendCursor = patch.SectionLength;
			long appendedBytes = 0;

			// (1) Per-handle placement plan
			foreach (KeyValuePair<ulong, long> entry in dirtyMap)
			{
				ulong handle = entry.Key;
				if (!TryReadObjectLength(dirtyStream, entry.Value, out int newLen))
				{
					throw new InvalidDataException($"低内存模式：无法解析脏对象 0x{handle:X8} 在脏流中的长度（偏移 {entry.Value}）");
				}

				byte[] bytes = new byte[newLen];
				ReadExact(dirtyStream, entry.Value, bytes, newLen);

				if (srcHandleMap.TryGetValue(handle, out long origOffset)
					&& TryReadObjectLength(patch.ImageStream, origOffset, out int origLen))
				{
					if (newLen == origLen)
					{
						byte[] old = patch.ReadImageBytes(origOffset, origLen);
						if (EqualBytes(bytes, old, origLen))
						{
							// Serialization result identical to the original: nothing to write
							result.UnchangedObjects++;
							continue;
						}

						patches.Add(new PendingWrite(origOffset, origLen, bytes));
						result.InPlaceObjects++;
					}
					else if (newLen < origLen)
					{
						// Shortened: write back in place and pad with zeros (the reader truncates per the MS size
						// prefix; the trailing zero bytes are never accessed)
						byte[] padded = new byte[origLen];
						Array.Copy(bytes, padded, bytes.Length);
						patches.Add(new PendingWrite(origOffset, origLen, padded));
						result.InPlaceObjects++;
					}
					else
					{
						// Grew: append at the section end and relocate; the original location keeps dead bytes
						// (page level: reused as-is)
						relocations.Add(handle, appendCursor);
						appends.Add(new PendingWrite(appendCursor, newLen, bytes));
						appendCursor += newLen;
						appendedBytes += newLen;
						result.RelocatedObjects++;
					}
				}
				else
				{
					// Absent in the source (edit-only mode: not expected): append at the section end
					relocations.Add(handle, appendCursor);
					appends.Add(new PendingWrite(appendCursor, newLen, bytes));
					appendCursor += newLen;
					appendedBytes += newLen;
					result.RelocatedObjects++;
				}
			}

			long newSectionLength = patch.SectionLength + appendedBytes;
			int totalPages = (int)((newSectionLength + ObjectsPageSize - 1) / ObjectsPageSize);

			// (2) Touched-page marking: in-place patch ranges + section-end append ranges (the original positions
			// of relocated objects are untouched — dead bytes are kept and their pages are reused)
			var touched = new bool[totalPages];
			void markPages(long start, long end)
			{
				if (end <= start)
				{
					return;
				}

				int first = (int)(start / ObjectsPageSize);
				int last = (int)((end - 1) / ObjectsPageSize);
				if (first < 0)
				{
					first = 0;
				}

				for (int i = first; i <= last && i < totalPages; i++)
				{
					touched[i] = true;
				}
			}

			foreach (PendingWrite item in patches)
			{
				markPages(item.Offset, item.Offset + item.Length);
			}

			foreach (PendingWrite item in appends)
			{
				markPages(item.Offset, item.Offset + item.Length);
			}

			int reusedPages = 0;
			for (int i = 0; i < totalPages; i++)
			{
				if (!touched[i] && i < patch.Pages.Length && !patch.Pages[i].Empty)
				{
					reusedPages++;
				}
			}

			// (3) Final image (sparse write: SetLength preallocates, writing only the original values of touched
			// pages + patches + appends; untouched pages are not materialized — the writer reuses their original
			// compressed bytes and never reads the corresponding image regions)
			string tmpPath = Path.Combine(Path.GetTempPath(), $"acsh_lowmem_{Guid.NewGuid():N}.tmp");
			var finalStream = new FileStream(tmpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.DeleteOnClose);
			try
			{
				finalStream.SetLength(newSectionLength);

				int originalPageCount = Math.Min(totalPages, patch.Pages.Length);
				var pageBuffer = new byte[ObjectsPageSize];
				for (int i = 0; i < originalPageCount; i++)
				{
					if (!touched[i])
					{
						continue;
					}

					long start = (long)i * ObjectsPageSize;
					int len = (int)Math.Min(ObjectsPageSize, patch.SectionLength - start);
					if (len <= 0)
					{
						continue;
					}

					ReadExact(patch.ImageStream, start, pageBuffer, len);
					finalStream.Position = start;
					finalStream.Write(pageBuffer, 0, len);
				}

				foreach (PendingWrite item in patches)
				{
					finalStream.Position = item.Offset;
					finalStream.Write(item.Bytes, 0, item.Length);
				}

				foreach (PendingWrite item in appends)
				{
					finalStream.Position = item.Offset;
					finalStream.Write(item.Bytes, 0, item.Length);
				}

				finalStream.Flush();
			}
			catch
			{
				finalStream.Dispose();
				throw;
			}

			// (4) Reuse mask: untouched pages are reused (the writer's reuse guard additionally requires the page
			// to be non-empty and offset-aligned, so empty/tail pages simply get fresh zero pages)
			var reusePage = new bool[totalPages];
			for (int i = 0; i < totalPages; i++)
			{
				reusePage[i] = !touched[i];
			}

			// (5) Handle graph: relocate/new handles overwrite or append into the caller's map (mutated in place)
			foreach (KeyValuePair<ulong, long> rel in relocations)
			{
				srcHandleMap[rel.Key] = rel.Value;
			}

			result.FinalStream = finalStream;
			result.ReusePage = reusePage;
			result.SectionLength = newSectionLength;
			result.ReusedPages = reusedPages;
			result.TotalPages = totalPages;
			result.AppendedBytes = appendedBytes;

			return result;
		}

		/// <summary>
		/// Parses the total byte size of the object at the given offset in the objects-section stream
		/// (size prefix + object body + 2-byte CRC). The size prefix is a modular short:
		/// size &lt; 0x4000 → 2 bytes; size &gt;= 0x4000 → 4 bytes.
		/// </summary>
		private static bool TryReadObjectLength(Stream stream, long offset, out int length)
		{
			length = 0;
			long save = stream.Position;
			try
			{
				stream.Position = offset;
				int b1 = stream.ReadByte();
				int b2 = stream.ReadByte();
				if (b1 < 0 || b2 < 0)
				{
					return false;
				}

				int size;
				int prefixLen;
				if ((b2 & 0x80) != 0)
				{
					int b3 = stream.ReadByte();
					int b4 = stream.ReadByte();
					if (b3 < 0 || b4 < 0)
					{
						return false;
					}

					size = b1 | ((b2 & 0x7F) << 8) | (b3 << 15) | ((b4 & 0x7F) << 23);
					prefixLen = 4;
				}
				else
				{
					size = b1 | ((b2 & 0x7F) << 8);
					prefixLen = 2;
				}

				if (size < 0)
				{
					return false;
				}

				length = prefixLen + size + 2;
				return length > prefixLen + 2;
			}
			finally
			{
				stream.Position = save;
			}
		}

		/// <summary>
		/// byte[] variant of <see cref="TryReadObjectLength(Stream, long, out int)"/> (no position save/restore needed).
		/// </summary>
		private static bool TryReadObjectLength(byte[] data, long offset, out int length)
		{
			length = 0;
			int p = (int)offset;
			if (p < 0 || p + 2 > data.Length)
			{
				return false;
			}

			int b1 = data[p];
			int b2 = data[p + 1];
			int size;
			int prefixLen;
			if ((b2 & 0x80) != 0)
			{
				if (p + 4 > data.Length)
				{
					return false;
				}

				size = b1 | ((b2 & 0x7F) << 8) | ((int)data[p + 2] << 15) | (((int)data[p + 3] & 0x7F) << 23);
				prefixLen = 4;
			}
			else
			{
				size = b1 | ((b2 & 0x7F) << 8);
				prefixLen = 2;
			}

			if (size < 0)
			{
				return false;
			}

			length = prefixLen + size + 2;
			return length > prefixLen + 2;
		}

		private static bool EqualBytes(byte[] a, byte[] b, int length)
		{
			int i = 0;
			for (; i + 8 <= length; i += 8)
			{
				if (BitConverter.ToInt64(a, i) != BitConverter.ToInt64(b, i))
				{
					return false;
				}
			}
			for (; i < length; i++)
			{
				if (a[i] != b[i])
				{
					return false;
				}
			}
			return true;
		}

		private static void ReadExact(Stream stream, long offset, byte[] buffer, int count)
		{
			stream.Position = offset;
			int read = 0;
			while (read < count)
			{
				int r = stream.Read(buffer, read, count - read);
				if (r <= 0)
				{
					throw new EndOfStreamException($"读取对象段失败：期望 {count}，实得 {read}（偏移 {offset}）");
				}
				read += r;
			}
		}

		/// <summary>
		/// byte[] variant of <see cref="ReadExact(Stream, long, byte[], int)"/>.
		/// </summary>
		private static void ReadExact(byte[] data, long offset, byte[] buffer, int count)
		{
			if (offset < 0 || offset + count > data.Length)
			{
				throw new EndOfStreamException($"读取对象段失败：期望 {count} 字节越界（偏移 {offset}，数据长 {data.Length}）");
			}

			Buffer.BlockCopy(data, (int)offset, buffer, 0, count);
		}
	}

	/// <summary>
	/// [PATCH] 低内存编辑模式：保存结果统计（<see cref="DwgReader.SaveLowMemory"/>）。
	/// </summary>
	public class DwgLowMemorySaveResult
	{
		/// <summary>输出文件路径。</summary>
		public string OutputPath { get; internal set; }

		/// <summary>原文件对象段页数。</summary>
		public int SourcePages { get; internal set; }

		/// <summary>新文件对象段总页数（原页数 + 尾部追加部分占用的页数）。</summary>
		public int TotalPages { get; internal set; }

		/// <summary>新文件中直接复用原文件压缩页字节的页数（未触碰的原始页）。</summary>
		public int ReusedPages { get; internal set; }

		/// <summary>新对象段（解压后）总长度（字节）。</summary>
		public long SectionLength { get; internal set; }

		/// <summary>段尾追加字节数（被重定位对象的新记录总长）。</summary>
		public long AppendedBytes { get; internal set; }

		/// <summary>在原始位置就地覆盖的脏对象数（含缩短后补零的情形）。</summary>
		public int InPlaceObjects { get; internal set; }

		/// <summary>因变长（或原文件中不存在）而追加到段尾并重定位的脏对象数。</summary>
		public int RelocatedObjects { get; internal set; }

		/// <summary>重序列化结果与原字节完全一致、无需写入的脏对象数。</summary>
		public int UnchangedObjects { get; internal set; }
	}
}
