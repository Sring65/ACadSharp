using ACadSharp.Blocks;
using ACadSharp.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ACadSharp.IO.DWG
{
	/// <summary>
	/// [PATCH] Low-memory edit mode: dirty-object serialization into a standalone record stream.
	/// </summary>
	internal partial class DwgObjectWriter
	{
		/// <summary>
		/// [PATCH] Low-memory edit mode: serialize the given dirty top-level objects (entities, and — when a block
		/// definition itself is dirty — its BLOCK/ENDBLK framing entities) into an independent record stream using
		/// the exact same framing as the full <see cref="Write()"/> path (per-record MS size prefix + payload +
		/// CRC16, via registerObject), without the 4-byte section preamble (no Write() call).
		/// <para>
		/// An INSERT's attribute chain and SEQEND are re-emitted automatically by writeEntity/writeChildEntities and
		/// are included in the returned map, so the splicer must splice every map key, not only the roots.
		/// Referenced objects (block records, layers, styles, …) are NOT re-emitted: the worker runs with
		/// <see cref="_captureEnqueuesOnly"/> so every reference stays on the source bytes.
		/// </para>
		/// <para>
		/// The worker is a fresh instance (no dirty/existing-handle tracking), so skipClean never fires and every
		/// object in the dirty set is actually re-serialized.
		/// </para>
		/// </summary>
		/// <param name="document">The (partially built) document that owns the dirty objects.</param>
		/// <param name="encoding">The file's text encoding (from the source header).</param>
		/// <param name="dirtyObjects">The dirty top-level objects, in any order.</param>
		/// <param name="writeXRecords">Mirror of the full-save setting (default true).</param>
		/// <param name="writeXData">Mirror of the full-save setting (default true).</param>
		/// <param name="writeShapes">Mirror of the full-save setting (default true).</param>
		/// <param name="writeDynamicParameters">Dynamic block parameters are NOT re-emitted in low-memory mode
		/// (the decoded document does not carry a reliable dynamic state); default false.</param>
		/// <returns>The record bytes (concatenated, no preamble) and the local handle graph
		/// (handle → byte offset of the record's size prefix within the returned bytes).</returns>
		public static (byte[] Bytes, Dictionary<ulong, long> Map) SerializeDirtyObjects(
			CadDocument document,
			Encoding encoding,
			CadObject[] dirtyObjects,
			bool writeXRecords = true,
			bool writeXData = true,
			bool writeShapes = true,
			bool writeDynamicParameters = false)
		{
			if (document == null)
				throw new ArgumentNullException(nameof(document));
			if (dirtyObjects == null || dirtyObjects.Length == 0)
				throw new ArgumentException("低内存模式：脏对象集合不能为空", nameof(dirtyObjects));

			var workerStream = new MemoryStream();
			var worker = new DwgObjectWriter(
				workerStream,
				document,
				encoding,
				writeXRecords,
				writeXData,
				writeShapes,
				writeDynamicParameters);

			// Only the dirty set is re-emitted; referenced objects stay on the source bytes.
			worker._captureEnqueuesOnly = true;
			worker._prev = null;
			worker._next = null;

			foreach (var obj in dirtyObjects)
			{
				if (obj == null)
					throw new ArgumentException("低内存模式：脏对象集合包含空引用", nameof(dirtyObjects));

				worker._prev = null;
				worker._next = null;
				switch (obj)
				{
					case Block b:
						worker.writeBlockBegin(b);
						break;

					case BlockEnd be:
						worker.writeBlockEnd(be);
						break;

					case Entity e:
						worker.writeEntity(e);
						break;

					default:
						throw new NotSupportedException(
							$"低内存模式暂不支持序列化该对象类型：{obj.GetType().Name}（仅支持实体，以及块定义的 BLOCK/ENDBLK 框架实体）");
				}
			}

			long len = workerStream.Length;
			var bytes = new byte[len];
			workerStream.Position = 0;
			int read = 0;
			while (read < (int)len)
			{
				int n = workerStream.Read(bytes, read, (int)len - read);
				if (n <= 0)
				{
					break;
				}

				read += n;
			}

			return (bytes, worker.Map);
		}
	}
}
