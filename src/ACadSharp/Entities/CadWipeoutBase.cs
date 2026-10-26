using ACadSharp.Attributes;
using CSMath;
using System.Collections.Generic;
using System;
using ACadSharp.Objects;
using System.Linq;
using CSUtilities.Extensions;
using ACadSharp.IO;

namespace ACadSharp.Entities;

/// <summary>
/// Common base class for <see cref="RasterImage" /> and <see cref="Wipeout" />.
/// </summary>
[DxfSubClass(null, true)]
public abstract class CadWipeoutBase : Entity
{
	/// <summary>
	/// Brightness
	/// </summary>
	/// <remarks>
	/// 0-100; default = 50
	/// </remarks>
	[DxfCodeValue(281)]
	public byte Brightness
	{
		get { return this._brightness; }
		set
		{
			if (value < 0 || value > 100)
			{
				throw new ArgumentException($"Invalid Brightness value: {value}, must be in range 0-100");
			}

			this._brightness = value;
		}
	}

	/// <summary>
	/// Class version
	/// </summary>
	[DxfCodeValue(90)]
	public int ClassVersion { get; set; }

	/// <summary>
	/// Clip boundary vertices.
	/// </summary>
	/// <remarks>
	/// For rectangular clip boundary type, two opposite corners must be specified.Default is (-0.5,-0.5), (size.x-0.5, size.y-0.5). 2) For polygonal clip boundary type, three or more vertices must be specified.Polygonal vertices must be listed sequentially
	/// </remarks>
	[DxfCodeValue(DxfReferenceType.Count, 91)]
	[DxfCollectionCodeValue(14, 24)]
	public List<XY> ClipBoundaryVertices { get; set; } = new List<XY>();

	/// <summary>
	/// Clipping state.
	/// </summary>
	[DxfCodeValue(290)]
	public ClipMode ClipMode { get; set; }

	/// <summary>
	/// Clipping state.
	/// </summary>
	[DxfCodeValue(280)]
	public bool ClippingState { get; set; }

	/// <summary>
	/// Clipping boundary type.
	/// </summary>
	[DxfCodeValue(71)]
	public ClipType ClipType { get { return this.ClipBoundaryVertices.Count > 2 ? ClipType.Polygonal : ClipType.Rectangular; } }

	/// <summary>
	/// Contrast
	/// </summary>
	/// <remarks>
	/// 0-100; default = 50
	/// </remarks>
	[DxfCodeValue(282)]
	public byte Contrast
	{
		get { return this._contrast; }
		set
		{
			if (value < 0 || value > 100)
			{
				throw new ArgumentException($"Invalid Brightness value: {value}, must be in range 0-100");
			}

			this._contrast = value;
		}
	}

	/// <summary>
	/// Image definition.
	/// </summary>
	[DxfCodeValue(DxfReferenceType.Handle, 340)]
	public virtual ImageDefinition Definition
	{
		get { return this._definition; }
		set
		{
			this._definition = this.updateCollectionEntry(value, d => this._definition = d, this.Document?.ImageDefinitions);
		}
	}

	/// <summary>
	/// Fade
	/// </summary>
	/// <remarks>
	/// 0-100; default = 0
	/// </remarks>
	[DxfCodeValue(283)]
	public byte Fade
	{
		get { return this._fade; }
		set
		{
			if (value < 0 || value > 100)
			{
				throw new ArgumentException($"Invalid Brightness value: {value}, must be in range 0-100");
			}

			this._fade = value;
		}
	}

	/// <summary>
	/// Image display properties.
	/// </summary>
	[DxfCodeValue(70)]
	public ImageDisplayFlags Flags { get => _flags; set => _flags = value; }

	/// <summary>
	/// Insertion point(in WCS)
	/// </summary>
	[DxfCodeValue(10, 20, 30)]
	public XYZ InsertPoint { get; set; }

	/// <summary>
	/// Add the ShowImage flag to the display flags property.
	/// </summary>
	public bool ShowImage
	{
		get { return this.Flags.HasFlag(ImageDisplayFlags.ShowImage); }
		set
		{
			if (value)
			{
				this._flags.AddFlag(ImageDisplayFlags.ShowImage);
			}
			else
			{
				this._flags.RemoveFlag(ImageDisplayFlags.ShowImage);
			}
		}
	}

	/// <summary>
	/// Image size in pixels.
	/// </summary>
	/// <remarks>
	/// 2D point(U and V values).
	/// </remarks>
	[DxfCodeValue(13, 23)]
	public XY Size { get; set; }

	/// <summary>
	/// World-space clip preview polygon (DXF group 92/310 in the AcDbEntity section), in world coordinates.
	/// </summary>
	/// <remarks>
	/// Only present in DXF; the DWG format stores no such blob. When present, this is the
	/// authoritative world geometry of the clip boundary: some files carry 14/24 local
	/// vertices that are a mirror image of the world blob, so the blob must take precedence.
	/// Layout: int32[5] header (totalLen, 1, totalLen-8, 6, vertexCount) followed by
	/// vertexCount × (x, y, z) little-endian doubles.
	/// </remarks>
	public byte[] WorldClipPreview { get; set; }

	/// <summary>
	/// U-vector of a single pixel(points along the visual bottom of the image, starting at the insertion point) (in WCS)
	/// </summary>
	[DxfCodeValue(11, 21, 31)]
	public XYZ UVector { get; set; } = XYZ.AxisX;

	/// <summary>
	/// V-vector of a single pixel(points along the visual left side of the image, starting at the insertion point) (in WCS)
	/// </summary>
	[DxfCodeValue(12, 22, 32)]
	public XYZ VVector { get; set; } = XYZ.AxisY;

	/// <summary>
	/// Reference to image definition reactor.
	/// </summary>
	//It seems that is not necessecary, keep it hidden for now
	[DxfCodeValue(DxfReferenceType.Handle, 360)]
	internal ImageDefinitionReactor DefinitionReactor
	{
		get { return this._definitionReactor; }
		set
		{
			this._definitionReactor = value;
			this._definitionReactor.Owner = this;
		}
	}

	private byte _brightness = 50;

	private byte _contrast = 50;

	private ImageDefinition _definition;

	private ImageDefinitionReactor _definitionReactor;

	private byte _fade = 0;

	private ImageDisplayFlags _flags;

	/// <inheritdoc/>
	public override void ApplyTransform(Transform transform)
	{
		this.InsertPoint = transform.ApplyTransform(this.InsertPoint);

		// [PATCH] UVector/VVector are the plane direction vectors of the image/wipeout, not
		// locations: AutoCAD TransformBy applies only the linear part (rotation + scale) to
		// them, never the translation (an IMAGE with U (0.3077, 0) rotated by -0.92 and scaled
		// by 0.998 becomes (0.18587, -0.24431), not a point near the translation offset).
		Matrix3 linear = new Matrix3(transform.Matrix);
		this.UVector = linear * this.UVector;
		this.VVector = linear * this.VVector;

		// [PATCH] Clip boundary vertices are relative to the insert point in the OCS plane
		// (AutoCAD TransformBy leaves them untouched: a wipeout clip (-0.3889, -0.2778) and an
		// image clip (259.5, 259.5) keep their values after a transform). Do NOT transform them.

		// [PATCH] The 92/310 world clip preview blob, when present, stores world-space points
		// and must follow the transform like any other location (or it goes stale and the
		// engine renders the pre-transform shape).
		if (this.WorldClipPreview != null && TryDecodeWorldClip(this.WorldClipPreview, out List<XYZ> worldVerts))
		{
			for (int i = 0; i < worldVerts.Count; i++)
			{
				worldVerts[i] = transform.ApplyTransform(worldVerts[i]);
			}

			this.WorldClipPreview = EncodeWorldClip(worldVerts);
		}
	}

	/// <summary>
	/// Decode the 92/310 world clip preview blob into world-space vertices.
	/// </summary>
	/// <param name="data">Raw 310 bytes (int32[5] header + count × 3 LE doubles).</param>
	/// <param name="worldVertices">Decoded world vertices when the blob is structurally valid.</param>
	public static bool TryDecodeWorldClip(byte[] data, out List<XYZ> worldVertices)
	{
		worldVertices = null;

		if (data == null || data.Length < 20)
		{
			return false;
		}

		int totalLen = BitConverter.ToInt32(data, 0);
		int marker = BitConverter.ToInt32(data, 4);
		int count = BitConverter.ToInt32(data, 16);

		if (totalLen != data.Length || marker != 1 || count < 3)
		{
			return false;
		}

		if (data.Length != 20 + count * 24)
		{
			return false;
		}

		worldVertices = new List<XYZ>(count);
		for (int i = 0; i < count; i++)
		{
			int off = 20 + i * 24;
			double x = BitConverter.ToDouble(data, off);
			double y = BitConverter.ToDouble(data, off + 8);
			double z = BitConverter.ToDouble(data, off + 16);

			worldVertices.Add(new XYZ(x, y, z));
		}

		return true;
	}

	/// <summary>
	/// Re-encode world-space vertices into the 92/310 blob layout (int32[5] header + count × 3 LE doubles).
	/// </summary>
	public static byte[] EncodeWorldClip(List<XYZ> worldVertices)
	{
		int count = worldVertices.Count;
		byte[] data = new byte[20 + count * 24];
		Buffer.BlockCopy(BitConverter.GetBytes(20 + count * 24), 0, data, 0, 4);
		Buffer.BlockCopy(BitConverter.GetBytes(1), 0, data, 4, 4);
		Buffer.BlockCopy(BitConverter.GetBytes(12 + count * 24), 0, data, 8, 4);
		Buffer.BlockCopy(BitConverter.GetBytes(6), 0, data, 12, 4);
		Buffer.BlockCopy(BitConverter.GetBytes(count), 0, data, 16, 4);

		for (int i = 0; i < count; i++)
		{
			int off = 20 + i * 24;
			Buffer.BlockCopy(BitConverter.GetBytes(worldVertices[i].X), 0, data, off, 8);
			Buffer.BlockCopy(BitConverter.GetBytes(worldVertices[i].Y), 0, data, off + 8, 8);
			Buffer.BlockCopy(BitConverter.GetBytes(worldVertices[i].Z), 0, data, off + 16, 8);
		}

		return data;
	}

	/// <inheritdoc/>
	public override CadObject Clone()
	{
		CadWipeoutBase clone = (CadWipeoutBase)base.Clone();

		clone.Definition = (ImageDefinition)this.Definition?.Clone();

		return clone;
	}

	/// <inheritdoc/>
	public override BoundingBox GetBoundingBox()
	{
		// [PATCH] The 92/310 world clip preview blob is already in world coordinates.
		if (this.WorldClipPreview != null && TryDecodeWorldClip(this.WorldClipPreview, out List<XYZ> worldVerts))
		{
			double wMinX = worldVerts.Min(v => v.X);
			double wMinY = worldVerts.Min(v => v.Y);
			double wMinZ = worldVerts.Min(v => v.Z);
			double wMaxX = worldVerts.Max(v => v.X);
			double wMaxY = worldVerts.Max(v => v.Y);
			double wMaxZ = worldVerts.Max(v => v.Z);

			return new BoundingBox(new XYZ(wMinX, wMinY, wMinZ), new XYZ(wMaxX, wMaxY, wMaxZ));
		}

		if (!this.ClipBoundaryVertices.Any())
		{
			return BoundingBox.Null;
		}

		// [PATCH] Clip vertices are image-normalized coordinates (origin at the image
		// center, y pointing down), not plain offsets from the insert point.
		// Transform each vertex: world = InsertPoint + (x+0.5) * UVector + (0.5 - y) * VVector.
		// (The standard formula origin + x·U + y·V yields a y-mirrored position;
		// verified point-by-point against the 310 world-clip blob of sample files.)
		XYZ? min = null;
		XYZ? max = null;
		foreach (XY bv in this.ClipBoundaryVertices)
		{
			XYZ world = this.InsertPoint + this.UVector * (bv.X + 0.5) + this.VVector * (0.5 - bv.Y);

			if (min == null)
			{
				min = world;
				max = world;
				continue;
			}

			min = new XYZ(Math.Min(min.Value.X, world.X), Math.Min(min.Value.Y, world.Y), Math.Min(min.Value.Z, world.Z));
			max = new XYZ(Math.Max(max.Value.X, world.X), Math.Max(max.Value.Y, world.Y), Math.Max(max.Value.Z, world.Z));
		}

		return new BoundingBox(min.Value, max.Value);
	}

	/// <inheritdoc/>
	public override bool IsValid(CadFileFormat format, ACadVersion version, out IList<string> errors)
	{
		var result = base.IsValid(format, version, out errors);

		if (this.ClipBoundaryVertices.Count < 2)
		{
			errors.Add($"Invalid {nameof(ClipBoundaryVertices)} count: {this.ClipBoundaryVertices.Count}, must be at least 2.");
			result = false;
		}

		return result;
	}

	internal override void AssignDocument(CadDocument doc)
	{
		base.AssignDocument(doc);

		this._definition = this.updateCollectionEntry(this.Definition, d => this._definition = d, doc.ImageDefinitions);
	}

	internal override void UnassignDocument()
	{
		base.UnassignDocument();

		this._definition = (ImageDefinition)this.Definition?.Clone();
	}
}