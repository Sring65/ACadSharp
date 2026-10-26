using ACadSharp.Entities;
using ACadSharp.Objects;
using System.Collections.Generic;
using System.Linq;

namespace ACadSharp.IO.Templates
{
	internal class CadWipeoutBaseTemplate : CadEntityTemplate
	{
		public ulong? ImgDefHandle { get; set; }

		public ulong? ImgReactorHandle { get; set; }

		// [PATCH] 310 chunks of the world clip preview blob (DXF 92/310, AcDbEntity section);
		// the blob can span multiple 310 lines, so they are accumulated and merged in build().
		public List<byte[]> WorldClipChunks { get; set; } = new List<byte[]>();

		public CadWipeoutBaseTemplate(CadWipeoutBase image) : base(image) { }

		protected override void build(CadDocumentBuilder builder)
		{
			base.build(builder);

			CadWipeoutBase image = this.CadObject as CadWipeoutBase;

			if (this.WorldClipChunks.Any())
			{
				image.WorldClipPreview = this.WorldClipChunks.SelectMany(c => c).ToArray();
			}

			if (builder.TryGetCadObject(this.ImgDefHandle, out ImageDefinition imgDef))
			{
				image.Definition = imgDef;
			}

			if (builder.TryGetCadObject(this.ImgReactorHandle, out ImageDefinitionReactor imgReactor))
			{
				image.DefinitionReactor = imgReactor;
			}
		}
	}
}
