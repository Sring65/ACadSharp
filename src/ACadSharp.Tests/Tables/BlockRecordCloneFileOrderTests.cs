using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ACadSharp.Tests.Tables;

/// <summary>
/// Regression tests (2026-11-01): <see cref="BlockRecord.Clone"/> must not leak the source
/// record's <c>FileOrderEntities</c> list into the clone.
/// <para>
/// <c>CadObject.Clone()</c> is a <c>MemberwiseClone</c>, which shares the source's
/// <c>FileOrderEntities</c> List reference. The DWG reader populates that list with the
/// source document's own entities (Owner = source block, Document = source document, source
/// handles, see <c>CadBlockRecordTemplate.build</c>). A deep-cloned block record that kept the
/// shared list made the streaming writers that prefer <c>FileOrderEntities</c>
/// (<c>DwgObjectWriter</c> / GDB2CAD <c>DwgStreamExporter.AddBlockDefinitionAcad</c>) treat the
/// source entities as the write list; adding them to the clone then threw
/// "The item with handle X is already assigned to a document" from
/// <c>CadDocument.AddCadObject</c> (big-DWG clip crash, P10 T4big/T5big).
/// </para>
/// </summary>
public class BlockRecordCloneFileOrderTests
{
	private static CadDocument DwgRoundTrip(CadDocument doc)
	{
		MemoryStream ms = new MemoryStream();
		DwgWriter.Write(ms, doc);
		using MemoryStream readStream = new MemoryStream(ms.ToArray());
		return DwgReader.Read(readStream);
	}

	/// <summary>
	/// Block with 4 lines (handles ascending with x) and FileOrderEntities in REVERSE handle
	/// order - proves the clone's ordered list is mapped from the source list (not re-sorted).
	/// </summary>
	private static (CadDocument doc, BlockRecord rec) NewDocWithBlock()
	{
		CadDocument doc = new CadDocument(ACadVersion.AC1027);
		var rec = new BlockRecord("TESTBLOCK");
		doc.BlockRecords.Add(rec);
		for (int i = 0; i < 4; i++)
		{
			rec.Entities.Add(new Line(new XY(i, 0), new XY(i + 1, 0)));
		}
		rec.FileOrderEntities = rec.Entities
			.OrderBy(e => e.Handle)
			.Reverse()
			.ToList();
		return (doc, rec);
	}

	[Fact]
	public void Clone_Rebuilds_FileOrderEntities_From_Clones()
	{
		var (doc, rec) = NewDocWithBlock();
		var clone = (BlockRecord)rec.Clone();

		Assert.NotNull(clone.FileOrderEntities);
		Assert.NotSame(rec.FileOrderEntities, clone.FileOrderEntities);
		Assert.Equal(rec.FileOrderEntities.Count, clone.FileOrderEntities.Count);

		foreach (var e in clone.FileOrderEntities)
		{
			// clone entities, never source-document entities
			Assert.DoesNotContain(rec.FileOrderEntities, x => ReferenceEquals(x, e));
			Assert.Contains(clone.Entities, x => ReferenceEquals(x, e));
			Assert.Equal(clone, e.Owner);
			Assert.Null(e.Document);
			Assert.Equal(0UL, e.Handle);
		}
	}

	[Fact]
	public void Clone_Preserves_Source_FileOrder()
	{
		var (doc, rec) = NewDocWithBlock();
		var clone = (BlockRecord)rec.Clone();

		// same relative order as the source's file-order list (reverse handle order here)
		var srcOrder = rec.FileOrderEntities.Select(e => ((Line)e).StartPoint.X).ToList();
		var cloneOrder = clone.FileOrderEntities.Select(e => ((Line)e).StartPoint.X).ToList();
		Assert.Equal(srcOrder, cloneOrder);
		Assert.NotEqual(
			rec.Entities.OrderBy(e => ((Line)e).StartPoint.X).Select(e => ((Line)e).StartPoint.X).ToList(),
			cloneOrder);
	}

	[Fact]
	public void Clone_Without_FileOrder_Stays_Null()
	{
		CadDocument doc = new CadDocument(ACadVersion.AC1027);
		var rec = new BlockRecord("B2");
		doc.BlockRecords.Add(rec);
		rec.Entities.Add(new Line(XY.Zero, new XY(1, 1)));

		var clone = (BlockRecord)rec.Clone();

		Assert.Null(clone.FileOrderEntities);
	}

	/// <summary>
	/// End-to-end shape of the P10 crash: DWG read-back (reader sets FileOrderEntities on every
	/// non-empty block) then a deep clone - the clone must be fully self-contained.
	/// The block contains a self-referencing INSERT (the gc257 trigger family).
	/// </summary>
	[Fact]
	public void DwgRoundTrip_ReaderFileOrder_Clone_Is_SelfContained()
	{
		CadDocument doc = new CadDocument(ACadVersion.AC1027);
		var rec = new BlockRecord("SYM");
		rec.Entities.Add(new Line(XY.Zero, new XY(1, 0)));
		// Insert(BlockRecord) clones the block when it already belongs to a document, so build
		// both inserts (self-ref + model-space ref) BEFORE registering the block.
		var ins = new Insert(rec) { InsertPoint = new XYZ(5, 0, 0) };
		rec.Entities.Add(ins);
		var msIns = new Insert(rec) { InsertPoint = new XYZ(0, 0, 0) };
		doc.BlockRecords.Add(rec);
		doc.Entities.Add(msIns);

		CadDocument rt = DwgRoundTrip(doc);
		var rtRec = rt.BlockRecords["SYM"];
		Assert.NotNull(rtRec);
		Assert.NotEmpty(rtRec.FileOrderEntities);

		var clone = (BlockRecord)rtRec.Clone();

		Assert.NotNull(clone.FileOrderEntities);
		Assert.NotSame(rtRec.FileOrderEntities, clone.FileOrderEntities);
		foreach (var e in clone.FileOrderEntities)
		{
			Assert.DoesNotContain(rtRec.FileOrderEntities, x => ReferenceEquals(x, e));
			Assert.Contains(clone.Entities, x => ReferenceEquals(x, e));
			Assert.Null(e.Document);
			Assert.Equal(0UL, e.Handle);
		}
	}
}
