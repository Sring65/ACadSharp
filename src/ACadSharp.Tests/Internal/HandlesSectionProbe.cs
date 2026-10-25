using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ACadSharp;
using ACadSharp.Classes;
using ACadSharp.IO;
using ACadSharp.IO.DWG;
using ACadSharp.IO.DWG.FileHeaders;
using Xunit;

namespace ACadSharp.Tests.Internal;

/// <summary>
/// v3 byte-level probe: AC1018 Handles section (spec chunk walk + CRC brute-force) vs AcDbObjects section
/// (bit-aware record parse using ACadSharp's PRODUCTION stream methods, mirroring DwgObjectReader.getEntityType
/// + readCommonData exactly). Diagnostic only, no assertions; results go to a log file.
/// </summary>
public class HandlesSectionProbe
{
	private const int PageSize = 0x7400;

	private static readonly (string Name, string Path)[] Files =
	{
		("MODIFIED", @"D:\soft\ACadSharpTransformertest\logs\modifytest_out.dwg"),
		("ORIGINAL", @"D:\Data\BGDY\BGDY_v20260618.dwg"),
	};

	private static readonly ulong[] ProbeHandles = { 0x4953F4, 0x732, 0x1B0, 0x1AF, 0x1B1 };

	private sealed class ChunkInfo
	{
		public long Pos;
		public int Size;
		public int Window;
		public int Entries;
		public ulong FirstH;
		public long FirstLoc;
		public ulong LastH;
		public long LastLoc;
		public int CrcHi;
		public int CrcLo;
		public bool CrcBytesValid;
		public int EarlyEnd; // bytes left in window after entries stopped (0 = filled exactly)
	}

	private sealed class FileResult
	{
		public string Name;
		public byte[] HandlesImage;
		public byte[] ObjectsImage;
		public Dictionary<ulong, long> WrittenMap;
		public List<ChunkInfo> Chunks;
		public bool TerminatorFound;
	}

	[Fact]
	public void ProbeBothFiles()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine($"HandlesSectionProbe v3 @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
		sb.AppendLine("recipe: page image assembly == getSectionBuffer18 (IsCompressed/IsEmpty branched);");
		sb.AppendLine("        Handles decode == production DwgHandleReader + spec chunk walk (next=pos+2+size, window=min(size-2,2032));");
		sb.AppendLine("        objects walk == bit-aware production-method parse (ReadModularShort/ReadObjectType/ReadRawLong/HandleReference).");

		var results = new List<FileResult>();
		foreach ((string name, string path) in Files)
		{
			try
			{
				ProbeFile(name, path, sb, results);
			}
			catch (Exception ex)
			{
				sb.AppendLine($"==== {name}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
				sb.AppendLine(ex.StackTrace);
			}
		}

		// ---- cross-file CRC comparison
		sb.AppendLine();
		sb.AppendLine("================ CROSS-FILE CRC SUMMARY ================");
		foreach (FileResult fr in results)
		{
			var good = FrGoodVariants.TryGetValue(fr.Name, out var set) ? set : new List<string>();
			sb.AppendLine($"  {fr.Name}: chunk0-verified variants: {(good.Count == 0 ? "NONE" : string.Join(", ", good))}");
		}
		if (results.Count == 2 && FrGoodVariants.TryGetValue("ORIGINAL", out var orig) && FrGoodVariants.TryGetValue("MODIFIED", out var mod) && orig.Count > 0)
		{
			var common = orig.Intersect(mod).ToList();
			sb.AppendLine($"  common to both files: {(common.Count == 0 ? "NONE" : string.Join(", ", common))}");
		}

		string dir = @"D:\soft\ACadSharpTransformertest\logs";
		Directory.CreateDirectory(dir);
		string log = Path.Combine(dir, $"handles_probe_{DateTime.Now:yyyyMMdd-HHmmss}.txt");
		File.WriteAllText(log, sb.ToString());
	}

	private static readonly Dictionary<string, List<string>> FrGoodVariants = new Dictionary<string, List<string>>();

	private static void ProbeFile(string name, string path, StringBuilder sb, List<FileResult> results)
	{
		sb.AppendLine();
		sb.AppendLine($"================ {name}: {path} ================");
		if (!File.Exists(path))
		{
			sb.AppendLine("  MISSING FILE");
			return;
		}

		long fileSize = new FileInfo(path).Length;
		sb.AppendLine($"  file size: {fileSize}");

		using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		byte[] magic = new byte[9];
		fs.Read(magic, 0, 9);
		string magicStr = BitConverter.ToString(magic).Replace("-", " ");
		sb.AppendLine($"  magic: {magicStr}");
		if (!magicStr.StartsWith("41 43 31 30 31 38")) // "AC1018"
		{
			sb.AppendLine("  NOT an AC1018 file - skipping");
			return;
		}

		DwgReader reader = new DwgReader(path);
		DwgFileHeaderAC18 header = reader.readFileHeader() as DwgFileHeaderAC18;
		if (header == null)
		{
			sb.AppendLine("  readFileHeader did not return DwgFileHeaderAC18");
			return;
		}
		sb.AppendLine($"  header: SectionAmount={header.SectionAmount} PageMapAddress={header.PageMapAddress} LastSectionAddr={header.LastSectionAddr}");

		DwgSectionDescriptor handlesDesc = header.Descriptors[DwgSectionDefinition.Handles];
		DwgSectionDescriptor objectsDesc = header.Descriptors[DwgSectionDefinition.AcDbObjects];
		DwgSectionDescriptor classesDesc = header.Descriptors[DwgSectionDefinition.Classes];
		sb.AppendLine($"  sections: Handles pages={handlesDesc.LocalSections.Count} IsCompressed={handlesDesc.IsCompressed} | objects pages={objectsDesc.LocalSections.Count} IsCompressed={objectsDesc.IsCompressed} | classes pages={classesDesc.LocalSections.Count} IsCompressed={classesDesc.IsCompressed}");

		// ---- class table (ClassNumber -> DxfName)
		byte[] classesImage = BuildSectionImage(fs, classesDesc, sb, "classes");
		sb.AppendLine($"  classes image: {classesImage.Length} bytes; first 64: {Hex(classesImage, 0, 64)}");
		var classes = new DxfClassCollection(new CadDocument());
		DwgClassesReader creader = new DwgClassesReader(
			ACadVersion.AC1018,
			DwgStreamReaderBase.GetStreamHandler(ACadVersion.AC1018, new MemoryStream(classesImage), resetPositon: true),
			header,
			classes);
		creader.Read();
		var className = classes.ToDictionary(c => (int)c.ClassNumber, c => c.DxfName);
		sb.AppendLine($"  classes decoded: {classes.Count}");
		string Cls(int idx) => className.TryGetValue(idx, out string n) ? n : $"<cls#{idx}>";
		sb.AppendLine("  FILE class table (decoded entries):");
		foreach (var c in classes.OrderBy(x => x.ClassNumber))
		{
			sb.AppendLine($"    num={c.ClassNumber} cpp={c.CppClassName} dxf={c.DxfName} app={c.ApplicationName} flags={c.ProxyFlags} zombie={c.WasZombie} item={c.ItemClassId} inst={c.InstanceCount} dwgVer={c.DwgVersion}");
		}

		FileResult fr = new FileResult { Name = name };

		// ---- Handles section: production decode + spec chunk walk
		fr.HandlesImage = BuildSectionImage(fs, handlesDesc, sb, "handles");
		byte[] handlesImage = fr.HandlesImage;
		sb.AppendLine($"  handles image: {handlesImage.Length} bytes; first 64: {Hex(handlesImage, 0, 64)}");
		fr.Chunks = WalkHandlesChunks(handlesImage, out bool term);
		fr.TerminatorFound = term;
		int totalEntries = fr.Chunks.Sum(c => c.Entries);
		sb.AppendLine($"  chunks: {fr.Chunks.Count} data chunks, terminator={term}, entries total={totalEntries}");
		DumpChunks(fr.Chunks, 0, Math.Min(6, fr.Chunks.Count), sb);
		DumpChunks(fr.Chunks, Math.Max(0, fr.Chunks.Count - 2), fr.Chunks.Count, sb);

		IDwgStreamReader hsReader = DwgStreamReaderBase.GetStreamHandler(
			ACadVersion.AC1018, new MemoryStream(handlesImage), resetPositon: true);
		fr.WrittenMap = new DwgHandleReader(ACadVersion.AC1018, hsReader).Read();
		sb.AppendLine($"  writtenMap (DwgHandleReader): {fr.WrittenMap.Count} entries (chunk entries={totalEntries}, match={totalEntries == fr.WrittenMap.Count})");
		var first20 = fr.WrittenMap.OrderBy(kv => kv.Value).Take(20);
		sb.AppendLine("  writtenMap first 20 by offset: " + string.Join(", ", first20.Select(kv => $"{kv.Key:X8}->{kv.Value:X8}")));

		// ---- CRC brute force on chunk 0, then verify all chunks
		var good = CrcBruteForce(handlesImage, fr.Chunks, sb, name);
		FrGoodVariants[name] = good;

		// ---- AcDbObjects section
		fr.ObjectsImage = BuildSectionImage(fs, objectsDesc, sb, "objects");
		byte[] objImage = fr.ObjectsImage;
		sb.AppendLine($"  objects image: {objImage.Length} bytes; first 64: {Hex(objImage, 0, 64)}");
		bool hasPrefix = objImage.Length >= 4
			&& objImage[0] == 0xCA && objImage[1] == 0x0D && objImage[2] == 0x00 && objImage[3] == 0x00;
		sb.AppendLine($"  0x0DCA prefix at 0: {hasPrefix}");

		IDwgStreamReader pr = DwgStreamReaderBase.GetStreamHandler(ACadVersion.AC1018, new MemoryStream(objImage), resetPositon: true);

		// ---- walk-start detection: sample-parse candidates with production methods
		long minMapOffset = fr.WrittenMap.Count > 0 ? fr.WrittenMap.Values.Min() : 4;
		var candidates = new List<long>();
		foreach (long c in new long[] { 0, 2, 4, 6, 8, 12, 16, minMapOffset })
		{
			if (!candidates.Contains(c))
				candidates.Add(c);
		}
		sb.AppendLine("  walk-start candidates (production parse):");
		long bestStart = -1;
		int bestScore = -1;
		foreach (long c in candidates)
		{
			var p = ParseRecord(pr, objImage, c);
			string line = $"    S={c,4}: ok={p.ok} why={p.why} prefix={p.prefixLen} size={p.size} initPos={p.initialPos} cls={p.cls}({Cls(p.cls)}) RL={p.rl} handlesTarget={p.handlesTarget} own={p.own:X8}";
			int score = 0;
			if (p.ok && p.size > 0)
			{
				score += 2;
				if (fr.WrittenMap.TryGetValue(p.own, out long ownOff) && ownOff == c)
				{
					score += 2;
					line += "  MATCH writtenMap[own]==S";
				}
			}
			if (p.ok && p.size > 0 && score > bestScore)
			{
				bestScore = score;
				bestStart = c;
			}
			sb.AppendLine(line);
		}

		// ---- sample-parse first 16 writtenMap offsets
		sb.AppendLine("  first 16 writtenMap offsets (production parse):");
		int sampleOk = 0;
		foreach (var kv in fr.WrittenMap.OrderBy(kv2 => kv2.Value).Take(16))
		{
			var p = ParseRecord(pr, objImage, kv.Value);
			bool self = p.ok && p.size > 0 && p.own == kv.Key;
			if (p.ok && p.size > 0)
				sampleOk++;
			sb.AppendLine($"    {kv.Value:X8} (h={kv.Key:X8}): ok={p.ok} why={p.why} prefix={p.prefixLen} size={p.size} initPos={p.initialPos} cls={p.cls}({Cls(p.cls)}) RL={p.rl} own={p.own:X8} self={self}");
		}
		sb.AppendLine($"  sample parse ok: {sampleOk}/16");

		if (bestStart < 0)
		{
			sb.AppendLine("  NO valid walk-start candidate found - skipping full walk");
			results.Add(fr);
			return;
		}
		if (bestScore < 4)
		{
			sb.AppendLine($"  WARNING: best start {bestStart:X8} has no writtenMap[own]==S confirmation (score={bestScore})");
		}

		// ---- full sequential walk from best start
		sb.AppendLine($"  full walk from {bestStart:X8}:");
		var embedded = new Dictionary<ulong, (long Off, int Cls)>();
		var byOffset = new Dictionary<long, (ulong Own, int Cls)>();
		var dups = new List<ulong>();
		int records = 0;
		int zeroSize = 0;
		long pos = bestStart;
		long desyncAt = -1;
		string desyncWhy = "";
		var firstRecs = new List<string>();
		while (pos + 4 <= (long)objImage.Length)
		{
			var p = ParseRecord(pr, objImage, pos);
			if (!p.ok)
			{
				desyncAt = pos;
				desyncWhy = p.why;
				break;
			}
			if (p.size == 0)
			{
				zeroSize++;
				pos += p.prefixLen + 2;
				continue;
			}
			records++;
			if (records <= 10)
			{
				firstRecs.Add($"{pos:X8}: own={p.own:X8} cls={Cls(p.cls)} RL={p.rl} size={p.size}");
			}
			if (embedded.ContainsKey(p.own))
			{
				dups.Add(p.own);
			}
			else
			{
				embedded[p.own] = (pos, p.cls);
				byOffset[pos] = (p.own, p.cls);
			}
			pos += p.prefixLen + (long)p.size + 2;
			if (records >= 4000000)
			{
				desyncAt = pos;
				desyncWhy = "record cap 4M";
				break;
			}
		}
		sb.AppendLine($"  walk: records={records} embedded={embedded.Count} dups={dups.Count} zeroSize={zeroSize} desyncAt={(desyncAt == -1 ? "none (clean end)" : desyncAt.ToString("X8"))} ({desyncWhy})");
		var otHist = embedded.Values.GroupBy(v => v.Cls).Select(g => new { Cls = g.Key, N = g.Count() }).OrderBy(x => x.Cls).ToList();
		sb.AppendLine($"  OT histogram ({otHist.Count} distinct classes referenced in object records):");
		foreach (var h in otHist)
		{
			sb.AppendLine($"    cls {h.Cls}: n={h.N} inTable={className.ContainsKey(h.Cls)} name={Cls(h.Cls)}");
		}
		if (desyncAt == -1)
		{
			sb.AppendLine($"  clean end at {pos:X8} vs image {objImage.Length:X8} (trailing pad={objImage.Length - pos})");
		}
		foreach (string r in firstRecs)
		{
			sb.AppendLine($"    rec {r}");
		}
		if (dups.Count > 0)
		{
			sb.AppendLine("  duplicate embedded handles (first 20): " + string.Join(", ", dups.Take(20).Select(d => d.ToString("X8"))));
		}

		// ---- diff writtenMap vs embeddedMap
		int both = 0, sameOff = 0, diffOff = 0;
		var deltaHist = new Dictionary<long, int>();
		int pointsToPrev = 0, pointsToNext = 0;
		var sortedRecs = byOffset.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Own, kv.Value.Cls)).ToList();
		foreach (var kv in fr.WrittenMap)
		{
			if (!embedded.TryGetValue(kv.Key, out var rec))
			{
				continue;
			}
			both++;
			if (rec.Off == kv.Value)
			{
				sameOff++;
				continue;
			}
			diffOff++;
			long d = rec.Off - kv.Value;
			deltaHist.TryGetValue(d, out int c);
			deltaHist[d] = c + 1;
			if (byOffset.TryGetValue(kv.Value, out var atW))
			{
				int i = IndexOfOffset(sortedRecs, rec.Off);
				if (i > 0 && sortedRecs[i - 1].Item1 == kv.Value)
				{
					pointsToPrev++;
				}
				if (i >= 0 && i + 1 < sortedRecs.Count && sortedRecs[i + 1].Item1 == kv.Value)
				{
					pointsToNext++;
				}
			}
		}
		var onlyWritten = fr.WrittenMap.Where(kv => !embedded.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
		var onlyEmbedded = embedded.Where(kv => !fr.WrittenMap.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
		sb.AppendLine($"  DIFF: both={both} sameOffset={sameOff} offsetMismatch={diffOff} onlyInWritten={onlyWritten.Count} onlyInEmbedded={onlyEmbedded.Count}");
		if (diffOff > 0)
		{
			sb.AppendLine($"  mismatch pattern: written->prevRecord={pointsToPrev} written->nextRecord={pointsToNext} (of {diffOff})");
			sb.AppendLine("  delta histogram (embedded.off - written.off, top 10):");
			foreach (var kv in deltaHist.OrderByDescending(kv => kv.Value).Take(10))
			{
				sb.AppendLine($"    {kv.Key,10} ({kv.Key:X8}) x {kv.Value}");
			}
		}
		if (onlyWritten.Count > 0)
		{
			sb.AppendLine("  onlyInWritten (first 20): " + string.Join(", ", onlyWritten.Take(20).Select(d => d.ToString("X8"))));
		}
		if (onlyEmbedded.Count > 0)
		{
			sb.AppendLine("  onlyInEmbedded (first 20): " + string.Join(", ", onlyEmbedded.Take(20).Select(d => d.ToString("X8"))));
		}

		// ---- probe handles
		sb.AppendLine("  probe handles:");
		foreach (ulong h in ProbeHandles)
		{
			bool inW = fr.WrittenMap.TryGetValue(h, out long wOff);
			bool inE = embedded.TryGetValue(h, out var eRec);
			string line = $"    {h:X8}: written={(inW ? wOff.ToString("X8") : "MISSING")}";
			if (inW)
			{
				line += byOffset.TryGetValue(wOff, out var atW)
					? $" class@written={Cls(atW.Cls)} own@written={atW.Own:X8}"
					: " (no record at written offset!)";
			}
			if (inE)
			{
				line += $" embedded={eRec.Off:X8} class@embedded={Cls(eRec.Cls)}";
				int i = IndexOfOffset(sortedRecs, eRec.Off);
				if (i > 0)
				{
					line += $" prev({sortedRecs[i - 1].Item1:X8}={Cls(sortedRecs[i - 1].Cls)})";
				}
				if (i + 1 < sortedRecs.Count)
				{
					line += $" next({sortedRecs[i + 1].Item1:X8}={Cls(sortedRecs[i + 1].Cls)})";
				}
				if (inW)
				{
					if (wOff == eRec.Off)
					{
						line += " SAME";
					}
					else if (i > 0 && sortedRecs[i - 1].Item1 == wOff)
					{
						line += " written==PREV record";
					}
					else if (i + 1 < sortedRecs.Count && sortedRecs[i + 1].Item1 == wOff)
					{
						line += " written==NEXT record";
					}
					else
					{
						line += $" DELTA={eRec.Off - wOff,10}";
					}
				}
			}
			else
			{
				line += " embedded=MISSING";
			}
			sb.AppendLine(line);
		}

		// ---- full production read: document class collection = writer input
		sb.AppendLine("  FULL production read:");
		try
		{
			var reader2 = new DwgReader(path);
			var swFull = System.Diagnostics.Stopwatch.StartNew();
			var doc = reader2.Read();
			swFull.Stop();
			short maxNum = doc.Classes.Any() ? doc.Classes.Max(c => c.ClassNumber) : (short)0;
			sb.AppendLine($"    ok in {swFull.Elapsed.TotalSeconds:F1}s; doc.Classes={doc.Classes.Count} maxClassNum={maxNum}");
			foreach (var c in doc.Classes.OrderBy(x => x.ClassNumber))
			{
				sb.AppendLine($"    doc num={c.ClassNumber} cpp={c.CppClassName} dxf={c.DxfName} zombie={c.WasZombie} inst={c.InstanceCount}");
			}
			// document released when ProbeFile returns
		}
		catch (Exception ex)
		{
			sb.AppendLine($"    FAILED: {ex.GetType().Name}: {ex.Message}");
		}

		results.Add(fr);
	}

	/// <summary>
	/// Sequential record parse with ACadSharp's production stream methods, mirroring
	/// DwgObjectReader.getEntityType (AC1018 branch) + readCommonData:
	///   [MS size prefix][class ReadObjectType][RL ReadRawLong bit-aware][own HandleReference]
	/// ok=true with size==0 marks a placeholder record (prefix + CRC only).
	/// </summary>
	private static (bool ok, string why, int prefixLen, int size, long initialPos, int cls, long rl, long handlesTarget, ulong own) ParseRecord(IDwgStreamReader r, byte[] img, long off)
	{
		try
		{
			if (off + 4 > (long)img.Length)
			{
				return (false, "OOB", 0, 0, 0, 0, 0, 0, 0);
			}
			r.Position = off;
			int size = (int)r.ReadModularShort();
			int prefixLen = (int)(r.Position - off);
			long end = off + prefixLen + (long)size + 2;
			if (size == 0)
			{
				return (true, "", prefixLen, 0, r.PositionInBits(), 0, 0, 0, 0);
			}
			if (end > (long)img.Length)
			{
				return (false, $"end {end:X} > img {img.Length:X}", prefixLen, size, r.PositionInBits(), 0, 0, 0, 0);
			}
			long initialPos = r.PositionInBits();
			int cls = (int)r.ReadObjectType();
			long rl = r.ReadRawLong();
			long handlesTarget = rl + initialPos;
			// v4: production has NO rl%8 guard - RL is a bit offset ("size of object data in bits,
			// number of bits before the handles"), the handle stream may start mid-byte.
			// Keep only the true OOB sanity checks.
			if (rl < 0 || rl / 8 > size)
			{
				return (false, $"bad RL {rl:X} (rl/8={rl / 8}, size={size})", prefixLen, size, initialPos, cls, rl, handlesTarget, 0);
			}
			ulong own = r.HandleReference();
			if (own == 0)
			{
				return (false, "own==0", prefixLen, size, initialPos, cls, rl, handlesTarget, 0);
			}
			return (true, "", prefixLen, size, initialPos, cls, rl, handlesTarget, own);
		}
		catch (Exception ex)
		{
			return (false, $"{ex.GetType().Name}: {ex.Message}", 0, 0, 0, 0, 0, 0, 0);
		}
	}

	/// <summary>
	/// Spec §20 chunk walk: [BE16 size][entries: MC handle_delta + MC loc_delta ...][CRC16 MSB-first],
	/// per-chunk baseline reset, window = min(size-2, 2032), next chunk at pos+2+size, terminator size==2.
	/// </summary>
	private static List<ChunkInfo> WalkHandlesChunks(byte[] img, out bool terminatorFound)
	{
		var chunks = new List<ChunkInfo>();
		terminatorFound = false;
		IDwgStreamReader r = DwgStreamReaderBase.GetStreamHandler(
			ACadVersion.AC1018, new MemoryStream(img), resetPositon: true);
		long pos = 0;
		while (pos + 2 <= (long)img.Length)
		{
			int size = (img[pos] << 8) | img[pos + 1];
			if (size == 2)
			{
				terminatorFound = true;
				return chunks;
			}
			if (size < 3 || pos + 2 + (long)size > (long)img.Length)
			{
				// structural error: record what we have and stop
				return chunks;
			}
			int window = Math.Min(size - 2, 2032);
			long chunkEnd = pos + 2 + window;
			var ci = new ChunkInfo { Pos = pos, Size = size, Window = window };
			r.Position = pos + 2;
			ulong lh = 0;
			long ll = 0;
			while (r.Position < chunkEnd && ci.Entries < 4096)
			{
				ulong dh = r.ReadModularChar();
				lh += dh;
				ll += r.ReadSignedModularChar();
				if (ci.Entries == 0)
				{
					ci.FirstH = lh;
					ci.FirstLoc = ll;
				}
				ci.LastH = lh;
				ci.LastLoc = ll;
				ci.Entries++;
			}
			ci.EarlyEnd = (int)(chunkEnd - r.Position);
			if (chunkEnd + 2 <= (long)img.Length)
			{
				ci.CrcHi = img[chunkEnd];
				ci.CrcLo = img[chunkEnd + 1];
				ci.CrcBytesValid = true;
			}
			chunks.Add(ci);
			pos = pos + 2 + (long)size;
		}
		return chunks;
	}

	private static void DumpChunks(List<ChunkInfo> chunks, int from, int to, StringBuilder sb)
	{
		for (int i = from; i < to; i++)
		{
			ChunkInfo c = chunks[i];
			sb.AppendLine($"    chunk {i}: pos={c.Pos:X8} size={c.Size} window={c.Window} entries={c.Entries} first=({c.FirstH:X8},{c.FirstLoc}) last=({c.LastH:X8},{c.LastLoc}) crc={(c.CrcBytesValid ? $"{c.CrcHi:X2}{c.CrcLo:X2}" : "?")} earlyEnd={c.EarlyEnd}");
		}
	}

	// ---- CRC-16 brute force

	private static readonly (string Name, ushort Poly, ushort Init, bool Refin, bool Refout, ushort XorOut)[] CrcVariants =
	{
		("CRC-16/ARC", 0x8005, 0x0000, true, true, 0x0000),
		("CRC-16/GENIBUS", 0x8005, 0x0000, false, false, 0x0000),
		("CRC-16/V-42", 0x8005, 0xFFFF, true, true, 0x0000),
		("CRC-16/FCS", 0x8005, 0xFFFF, false, false, 0x0000),
		("CRC-16/MAXIM", 0x8005, 0x0000, true, true, 0xFFFF),
		("CRC-16/CD-MV", 0x8005, 0x8005, true, true, 0x0000),
		("CRC-16/X-25", 0x1021, 0x0000, true, true, 0xFFFF),
		("CRC-16/CCITT-REF", 0x1021, 0xFFFF, true, true, 0x0000),
		("CRC-16/CCITT-FALSE", 0x1021, 0xFFFF, false, false, 0x0000),
		("CRC-16/XMODEM", 0x1021, 0x0000, false, false, 0x0000),
		("CRC-16/USB", 0x8005, 0xFFFF, true, true, 0xFFFF),
		("CRC-16/MCRF4XX", 0x1021, 0xFFFF, false, false, 0xFFFF),
		("CRC-16/AUG-CCITT", 0x8005, 0x1D0F, false, false, 0x0000),
		("CRC-16/DNP", 0x3D65, 0x0000, true, true, 0xFFFF),
	};

	private static List<string> CrcBruteForce(byte[] img, List<ChunkInfo> chunks, StringBuilder sb, string fileName)
	{
		var good = new List<string>();
		if (chunks.Count == 0 || !chunks[0].CrcBytesValid)
		{
			sb.AppendLine("  CRC: no chunk 0 to test");
			return good;
		}
		ChunkInfo c0 = chunks[0];
		int stored = (c0.CrcHi << 8) | c0.CrcLo;
		int[] rangeOffs = { (int)c0.Pos + 2, (int)c0.Pos, (int)c0.Pos };
		int[] rangeLens = { c0.Window, c0.Window + 2, c0.Size + 2 };
		string[] rangeNames = { "entries", "size+entries", "fullChunk" };
		sb.AppendLine($"  CRC brute force on chunk 0 (stored={stored:X4}):");
		foreach (var (vn, poly, init, refin, refout, xorout) in CrcVariants)
		{
			for (int ri = 0; ri < 3; ri++)
			{
				int off = rangeOffs[ri];
				int len = rangeLens[ri];
				if (off + len > (long)img.Length)
				{
					continue;
				}
				ushort calc = Crc16(poly, init, refin, refout, xorout, img, off, len);
				if (calc == (ushort)stored)
				{
					string label = $"{vn}/{rangeNames[ri]}";
					// verify all chunks with this (variant, range)
					int fails = 0;
					foreach (ChunkInfo c in chunks)
					{
						if (!c.CrcBytesValid)
						{
							fails++;
							continue;
						}
						int offC = ri == 0 ? (int)c.Pos + 2 : ri == 1 ? (int)c.Pos : (int)c.Pos;
						int lenC = ri == 0 ? c.Window : ri == 1 ? c.Window + 2 : c.Size + 2;
						if (offC + lenC > (long)img.Length)
						{
							fails++;
							continue;
						}
						ushort calcC = Crc16(poly, init, refin, refout, xorout, img, offC, lenC);
						int storedC = (c.CrcHi << 8) | c.CrcLo;
						if (calcC != (ushort)storedC)
							fails++;
					}
					good.Add(label);
					sb.AppendLine($"    MATCH {label}: all-chunk verify fails={fails}/{chunks.Count}");
				}
			}
		}
		if (good.Count == 0)
		{
			sb.AppendLine("    no standard CRC-16 variant matches chunk 0 of this file");
		}
		return good;
	}

	private static ushort Reverse16(ushort v)
	{
		ushort r = 0;
		for (int i = 0; i < 16; i++)
		{
			r = (ushort)((r << 1) | (v & 1));
			v >>= 1;
		}
		return r;
	}

	private static ushort Crc16(ushort poly, ushort init, bool refin, bool refout, ushort xorout, byte[] data, int off, int len)
	{
		ushort[] table = new ushort[256];
		if (refin)
		{
			ushort rpoly = Reverse16(poly);
			for (int i = 0; i < 256; i++)
			{
				ushort c = (ushort)i;
				for (int b = 0; b < 8; b++)
				{
					c = (c & 1) != 0 ? (ushort)((c >> 1) ^ rpoly) : (ushort)(c >> 1);
				}
				table[i] = c;
			}
			ushort crc = init;
			for (int i = 0; i < len; i++)
			{
				crc = (ushort)((crc >> 8) ^ table[(crc ^ (ushort)data[off + i]) & 0xFF]);
			}
			if (refout)
			{
				crc = Reverse16(crc);
			}
			return (ushort)(crc ^ xorout);
		}
		else
		{
			for (int i = 0; i < 256; i++)
			{
				ushort c = (ushort)(i << 8);
				for (int b = 0; b < 8; b++)
				{
					c = (c & 0x8000) != 0 ? (ushort)((c << 1) ^ poly) : (ushort)(c << 1);
				}
				table[i] = c;
			}
			ushort crc = init;
			for (int i = 0; i < len; i++)
			{
				crc = (ushort)((crc << 8) ^ table[((crc >> 8) ^ (ushort)data[off + i]) & 0xFF]);
			}
			if (refout)
			{
				crc = Reverse16(crc);
			}
			return (ushort)(crc ^ xorout);
		}
	}

	/// <summary>
	/// Assembles a section image exactly like DwgReader.getSectionBuffer18: MemoryStream preallocated to
	/// DecompressedSize*PageCount, pages in LocalSections order; empty pages zero-advance; compressed pages
	/// via DwgLZ77AC18Decompressor.DecompressPageToBuffer(fs, Seeker+32); UNCOMPRESSED pages read the
	/// CompressedSize bytes (from the decrypted 32B page header) at Seeker+32.
	/// </summary>
	private static byte[] BuildSectionImage(FileStream fs, DwgSectionDescriptor descriptor, StringBuilder sb, string label)
	{
		long prealloc = (long)descriptor.DecompressedSize * descriptor.LocalSections.Count;
		if (prealloc > int.MaxValue)
		{
			throw new InvalidOperationException($"section too large: {prealloc}");
		}
		MemoryStream ms = new MemoryStream((int)prealloc);
		int rawPages = 0, compPages = 0, emptyPages = 0;
		long rawTotal = 0;
		foreach (DwgLocalSectionMap page in descriptor.LocalSections)
		{
			if (page.IsEmpty)
			{
				ms.Position += (long)page.DecompressedSize;
				emptyPages++;
			}
			else if (descriptor.IsCompressed)
			{
				(byte[] data, int written) = DwgLZ77AC18Decompressor.DecompressPageToBuffer(fs, page.Seeker + 32);
				ms.Write(data, 0, written);
				compPages++;
			}
			else
			{
				// replicate decryptDataSection: field = LE u32 XOR (0x4164536B ^ fieldFilePos); CompressedSize at +0x08
				long p = page.Seeker;
				int secMask = 0x4164536B ^ (int)p;
				fs.Position = p;
				byte[] hdr = new byte[32];
				int rd = 0;
				while (rd < 32)
				{
					int n = fs.Read(hdr, rd, 32 - rd);
					if (n <= 0)
						break;
					rd += n;
				}
				uint rawLe = (uint)(hdr[8] | hdr[9] << 8 | hdr[10] << 16 | (uint)hdr[11] << 24);
				int len = (int)(rawLe ^ (uint)secMask);
				if (len <= 0 || len > 0x100000)
				{
					throw new InvalidOperationException($"{label}: bad raw page size {len} at seeker {p:X}");
				}
				byte[] buf = new byte[len];
				fs.Position = p + 32;
				rd = 0;
				while (rd < len)
				{
					int n = fs.Read(buf, rd, len - rd);
					if (n <= 0)
						break;
					rd += n;
				}
				ms.Write(buf, 0, rd);
				rawPages++;
				rawTotal += len;
			}
		}
		ms.Position = 0;
		if (label == "classes" || label == "handles")
		{
			sb.AppendLine($"  {label} pages: raw={rawPages} compressed={compPages} empty={emptyPages} rawBytes={rawTotal}");
		}
		return ms.ToArray();
	}

	private static int IndexOfOffset(List<(long Item1, ulong Item2, int Item3)> sortedRecs, long off)
	{
		int lo = 0, hi = sortedRecs.Count - 1;
		while (lo <= hi)
		{
			int mid = (lo + hi) >> 1;
			if (sortedRecs[mid].Item1 == off)
			{
				return mid;
			}
			if (sortedRecs[mid].Item1 < off)
			{
				lo = mid + 1;
			}
			else
			{
				hi = mid - 1;
			}
		}
		return -1;
	}

	private static string Hex(byte[] b, int off, int len)
	{
		if (b.Length < off + len)
		{
			len = Math.Max(0, b.Length - off);
		}
		return BitConverter.ToString(b, off, len).Replace("-", " ");
	}
}
