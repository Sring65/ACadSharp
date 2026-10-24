using ACadSharp.Classes;
using ACadSharp.IO.Templates;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ACadSharp.IO.DWG
{
	/// <summary>
	/// [DIAG] 单对象重解析探针（P0b / P1 侧表式载荷降级 inflate 路径验证）。
	/// </summary>
	/// <remarks>
	/// <para>
	/// 给定对象段（AcDbObjects）解压映像中一个对象的完整原始字节切片
	/// （物理布局 <c>[MS 大小前缀 2B][对象数据][2B CRC]</c>，与 <see cref="ObjectByteRange"/> 一致），
	/// 使用与原始读取完全相同的类表（<see cref="DxfClassCollection"/>）与编码，
	/// 通过独立 <c>DwgObjectReader</c>（单句柄、偏移 0、空引用图）重新解码，
	/// 返回可与其内存中对象逐字段对比的 <see cref="CadObject"/>。
	/// </para>
	/// <para>
	/// 该 API 仅依赖类表（常驻于 <c>CadDocument.Classes</c>）与解码代码本身，
	/// 不依赖任何每对象模板状态，因此 <c>DiscardTemplatesAfterBuild</c> 之后依然可用——
	/// 这正是"侧表式载荷降级"（只保留句柄→字节区间，重解析时还原载荷）成立的前提。
	/// </para>
	/// <para>
	/// 重解析出的对象处于"解码完成、引用未解析"状态：句柄引用字段保存原始句柄值
	/// （Owner/Layer/Block 等指向 null 或原始 handle），与 Build 后的完整对象不同；
	/// 载荷字段（文本值、顶点列表、几何参数等）应与原对象一致。
	/// </para>
	/// </remarks>
	public static class DwgObjectReparse
	{
		/// <summary>
		/// 重新解析一个对象的原始字节切片。
		/// </summary>
		/// <param name="version">DWG 版本号（与源文件一致，如 <see cref="ACadVersion.AC1018"/>）。</param>
		/// <param name="classes">源文件的类表（<c>CadDocument.Classes</c>）。</param>
		/// <param name="encoding">对象段文本编码（与读取时一致；可用 <c>CadUtils.GetListedEncoding(doc.Header.CodePage)</c> 获得）。</param>
		/// <param name="handle">对象的句柄（用于模板登记；引用图仅含该句柄自身）。</param>
		/// <param name="slice">对象完整原始字节切片（含 2B 大小前缀与 2B CRC，长度应等于 <c>ObjectByteRange.Length</c>）。</param>
		/// <returns>重解析出的对象；解码失败（如未知对象类型）时返回 <c>null</c>。</returns>
		/// <exception cref="ArgumentNullException"><paramref name="classes"/> 或 <paramref name="slice"/> 为 <c>null</c>。</exception>
		public static CadObject Reparse(
			ACadVersion version,
			DxfClassCollection classes,
			Encoding encoding,
			ulong handle,
			byte[] slice)
		{
			if (classes == null)
				throw new ArgumentNullException(nameof(classes));
			if (slice == null)
				throw new ArgumentNullException(nameof(slice));
			if (slice.Length < 4)
				throw new ArgumentException("切片过短（至少含 2B 大小前缀 + 2B CRC）。", nameof(slice));

			// 独立映像流：切片即全部（对象 ≤ 64KB，拷贝成本可忽略）
			var imageStream = new MemoryStream(slice, 0, slice.Length, false);
			var sectionReader = DwgStreamReaderBase.GetStreamHandler(version, imageStream);
			// 缺省回退 Windows-1252（GetListedEncoding 对未知名称同样回退 1252，行为一致）
			sectionReader.Encoding = encoding ?? CadUtils.GetListedEncoding("1252");

			// 全新 builder：只用于模板登记（AddTemplate 仅写 builder 自身字典，不触碰外部文档）
			var builder = new DwgDocumentBuilder(version, new CadDocument(version), new DwgReaderConfiguration());

			// 单句柄引用图：对象自身位于切片偏移 0；其引用的句柄均查不到 → 只记录句柄值、不展开
			var handleMap = new Dictionary<ulong, long> { [handle] = 0L };
			var queue = new Queue<ulong>();
			queue.Enqueue(handle);

			var reader = new DwgObjectReader(version, builder, sectionReader, queue, handleMap, classes);
			try
			{
				reader.Read();
			}
			finally
			{
				reader.Dispose();
			}

			var template = builder.GetObjectTemplate<CadTemplate>(handle);
			return template?.CadObject;
		}
	}
}
