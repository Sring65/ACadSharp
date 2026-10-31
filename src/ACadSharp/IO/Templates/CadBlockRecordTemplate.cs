using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSUtilities.Extensions;
using System.Collections.Generic;

namespace ACadSharp.IO.Templates;

internal class CadBlockRecordTemplate : CadTableEntryTemplate<BlockRecord>, ICadOwnerTemplate
{
	public ulong? BeginBlockHandle { get; set; }

	public CadBlockEntityTemplate BlockEntityTemplate { get; set; }

	public ulong? EndBlockHandle { get; set; }

	public ulong? FirstEntityHandle { get; set; }

	public List<ulong> InsertHandles { get; } = new();

	public ulong? LastEntityHandle { get; set; }

	public ulong? LayoutHandle { get; set; }

	public HashSet<ulong> OwnedObjectsHandlers { get; } = new();

	/// <summary>
	/// [PATCH] Ordered mirror of <see cref="OwnedObjectsHandlers"/>: owned handles in the order they
	/// appear in the file (readers add them in file order). <see cref="build"/> iterates it first so
	/// that block entities are attached in canonical file order — HashSet iteration is hash order,
	/// which is not stable across processes.
	/// </summary>
	public List<ulong> OwnedObjectsFileOrder { get; } = new();

	/// <summary>[PATCH] Register an owned handle preserving file order (duplicates filtered via the HashSet).</summary>
	public void AddOwnedHandlerInFileOrder(ulong handle)
	{
		if (this.OwnedObjectsHandlers.Add(handle))
			this.OwnedObjectsFileOrder.Add(handle);
	}

	/// <summary>
	/// [PATCH] Ordered mirror of the referenced-entity registration (DXF read path). Was a
	/// HashSet&lt;CadEntityTemplate&gt; whose iteration order is hash-based (non-deterministic
	/// block entity order in the built document).
	/// </summary>
	public List<CadEntityTemplate> ReferenceTemplates { get; } = new();

	private readonly HashSet<CadEntityTemplate> _referenceTemplatesSeen = new();

	/// <summary>[PATCH] Register a referenced entity template preserving file order (duplicates filtered).</summary>
	public void AddReferenceTemplate(CadEntityTemplate template)
	{
		if (template != null && this._referenceTemplatesSeen.Add(template))
			this.ReferenceTemplates.Add(template);
	}

	public CadBlockRecordTemplate() : base(new BlockRecord())
	{
	}

	public CadBlockRecordTemplate(BlockRecord block) : base(block)
	{
	}

	public void SetBlockToRecord(CadDocumentBuilder builder, DWG.DwgHeaderHandlesCollection headerHandles)
	{
		if (builder.TryGetCadObject(this.BeginBlockHandle, out Block block))
		{
			if (!block.Name.IsNullOrEmpty())
			{
				this.CadObject.Name = block.Name;
			}

			block.Flags = this.CadObject.BlockEntity.Flags;
			block.BasePoint = this.CadObject.BlockEntity.BasePoint;
			block.XRefPath = this.CadObject.BlockEntity.XRefPath;
			block.Comments = this.CadObject.BlockEntity.Comments;
			block.IsUnloaded = this.CadObject.BlockEntity.IsUnloaded;

			this.CadObject.BlockEntity = block;
		}

		if (builder.TryGetCadObject(this.EndBlockHandle, out BlockEnd blockEnd))
		{
			this.CadObject.BlockEnd = blockEnd;
		}

		this.ensureCorrectNaming(builder, headerHandles.MODEL_SPACE, BlockRecord.ModelSpaceName);
		this.ensureCorrectNaming(builder, headerHandles.PAPER_SPACE, BlockRecord.PaperSpaceName);
	}

	protected override void build(CadDocumentBuilder builder)
	{
		base.build(builder);

		if (builder.TryGetCadObject(this.LayoutHandle, out Layout layout))
		{
			this.CadObject.Layout = layout;
		}

		// [PATCH] Canonical file-order list of the block's entities: BlockRecord.Entities is
		// HashSet-backed (iteration = hash order, not stable across processes). Renderers, the DXF
		// writer and the DWG writer iterate FileOrderEntities when it is set, so every consumer
		// agrees on one deterministic order (the order the entities were registered/read in).
		var fileOrder = new List<Entity>();

		if (this.FirstEntityHandle.HasValue && this.FirstEntityHandle.Value != 0)
		{
			foreach (Entity e in this.getEntitiesCollection<Entity>(builder, this.FirstEntityHandle.Value, this.LastEntityHandle.Value))
			{
				this.addEntity(builder, e, fileOrder);
			}
		}
		else
		{
			if (this.BlockEntityTemplate != null)
			{
				this.OwnedObjectsHandlers.UnionWith(this.BlockEntityTemplate.OwnedObjectsHandlers);
			}

			// Two passes: file-order handles first, then the HashSet remainder (handles coming from
			// BlockEntityTemplate.UnionWith that carry no file-order entry, e.g. pre-R2004 paths).
			var fileOrderSeen = new HashSet<ulong>(this.OwnedObjectsFileOrder);
			foreach (ulong handle in this.OwnedObjectsFileOrder)
			{
				if (builder.TryGetCadObject(handle, out Entity child))
				{
					this.addEntity(builder, child, fileOrder);
				}
			}

			foreach (ulong handle in this.OwnedObjectsHandlers)
			{
				if (fileOrderSeen.Contains(handle))
					continue;

				if (builder.TryGetCadObject(handle, out Entity child))
				{
					this.addEntity(builder, child, fileOrder);
				}
			}
		}

		foreach (var item in this.ReferenceTemplates)
		{
			this.addEntity(builder, item.CadObject, fileOrder);
		}

		if (fileOrder.Count > 0)
		{
			this.CadObject.FileOrderEntities = fileOrder;
		}
	}

	private void addEntity(CadDocumentBuilder builder, Entity entity, List<Entity> fileOrder)
	{
		if (!builder.KeepUnknownEntities && entity is UnknownEntity)
		{
			return;
		}

		this.CadObject.Entities.Add(entity);
		fileOrder.Add(entity);
	}

	private void ensureCorrectNaming(CadDocumentBuilder builder, ulong? handle, string expected)
	{
		if (this.CadObject.Handle == handle
			&& !this.CadObject.Name.Equals(expected, System.StringComparison.InvariantCultureIgnoreCase))
		{
			builder.Notify($"Invalid name for {this.CadObject.Name} changed to {expected}", NotificationType.Warning);
			this.CadObject.Name = expected;
		}
	}
}