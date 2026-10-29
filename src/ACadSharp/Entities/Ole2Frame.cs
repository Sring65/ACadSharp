using System;
using ACadSharp.Attributes;
using CSMath;

namespace ACadSharp.Entities;

/// <summary>
/// Represents a <see cref="Ole2Frame"/> entity.
/// </summary>
/// <remarks>
/// Object name <see cref="DxfFileToken.EntityOle2Frame"/> <br/>
/// Dxf class name <see cref="DxfSubclassMarker.Ole2Frame"/>
/// </remarks>
[DxfName(DxfFileToken.EntityOle2Frame)]
[DxfSubClass(DxfSubclassMarker.Ole2Frame)]
public class Ole2Frame : Entity
{
	/// <summary>
	/// Gets or sets the binary data associated with the object.
	/// </summary>
	[DxfCodeValue(310)]
	public byte[] BinaryData { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether the object resides in paper space.
	/// </summary>
	[DxfCodeValue(72)]
	public bool IsPaperSpace { get; set; } = false;

	/// <summary>
	/// Gets or sets the lower-right corner of the object in world coordinate system (WCS).
	/// </summary>
	[DxfCodeValue(11, 21, 31)]
	public XYZ LowerRightCorner { get; set; } = XYZ.Zero;

	/// <inheritdoc/>
	public override string ObjectName => DxfFileToken.EntityOle2Frame;

	/// <inheritdoc/>
	public override ObjectType ObjectType => ObjectType.OLE2FRAME;

	/// <summary>
	/// Gets or sets the OLE data payload.
	/// </summary>
	public byte[] OleData { get; set; }

	/// <summary>
	/// Gets or sets the type of the OLE (Object Linking and Embedding) object.
	/// </summary>
	/// <remarks>The <see cref="OleObjectType"/> property specifies the behavior and storage type of the OLE object:
	/// <list type="bullet">
	/// <item> <description><see cref="OleObjectType.Link"/>: The object is linked to an external source.</description>
	/// </item> <item> <description><see cref="OleObjectType.Embedded"/>: The object is embedded within the current document.</description> </item>
	/// <item> <description><see cref="OleObjectType.Static"/>: The object is a static representation.</description>
	/// </item>
	/// </list>
	/// </remarks>
	[DxfCodeValue(71)]
	public OleObjectType OleObjectType { get; set; } = OleObjectType.Embedded;

	/// <summary>
	/// Gets or sets the name of the application that created or modified the associated data.
	/// </summary>
	[DxfCodeValue(3)]
	public string SourceApplication { get; set; }

	/// <inheritdoc/>
	public override string SubclassMarker => DxfSubclassMarker.Ole2Frame;

	/// <summary>
	/// Gets or sets the upper-left corner of the object in world coordinate system (WCS).
	/// </summary>
	[DxfCodeValue(10, 20, 30)]
	public XYZ UpperLeftCorner { get; set; } = new XYZ(1, 1, 0);

	/// <summary>
	/// OLE version number.
	/// </summary>
	[DxfCodeValue(70)]
	public short Version { get; internal set; } = 2;

	/// <inheritdoc/>
	public override void ApplyTransform(Transform transform)
	{
		this.UpperLeftCorner = transform.ApplyTranslation(this.UpperLeftCorner);
		this.LowerRightCorner = transform.ApplyTranslation(this.LowerRightCorner);
	}

	/// <inheritdoc/>
	public override BoundingBox GetBoundingBox()
	{
		return BoundingBox.FromPoints([this.LowerRightCorner, this.UpperLeftCorner]);
	}

	/// <summary>
	/// [PATCH] Extracts the embedded BMP (the DIB preview of the OLE object) from the group code 310 <see cref="BinaryData"/>.
	/// Layout (measured on Drawing1Image): [0..2)=0x8055 prefix, [2..98)=4x3 WCS corner doubles, [98..128)=OLE wrapper,
	/// from 128 the CFB container begins, and the BMP ("BM" magic) lives inside the CFB DIB stream (@3200 in that sample).
	/// Scans linearly for "BM" from offset 98 and validates a full BITMAPFILEHEADER+BITMAPINFOHEADER
	/// (24/32-bit BI_RGB, plausible dimensions, pixel data within bounds), taking the first valid candidate;
	/// returns the bytes from the magic to the end of the data (trailing CFB residue is ignored by the
	/// decoder according to the length declared in the header).
	/// </summary>
	/// <param name="binary">Raw group code 310 bytes.</param>
	/// <param name="bmp">Extracted BMP bytes (starting at the "BM" magic).</param>
	/// <returns>True when a valid BMP was extracted.</returns>
	public static bool TryExtractBmp(byte[] binary, out byte[] bmp)
	{
		bmp = null;
		if (binary == null || binary.Length < 98 + 54) return false;
		for (int i = 98; i + 54 <= binary.Length; i++)
		{
			if (binary[i] != 0x42 || binary[i + 1] != 0x4D) continue; // "BM"
			// Header validation (same standard as RasterHelpers.DecodeBmpToRgba, to reject coincidental matches)
			int dataOff = BitConverter.ToInt32(binary, i + 10);
			int biSize = BitConverter.ToInt32(binary, i + 14);
			if (biSize < 40) continue;
			int w = BitConverter.ToInt32(binary, i + 18);
			int h = BitConverter.ToInt32(binary, i + 22);
			short planes = BitConverter.ToInt16(binary, i + 26);
			short bpp = BitConverter.ToInt16(binary, i + 28);
			int compression = BitConverter.ToInt32(binary, i + 30);
			if (planes != 1 || (bpp != 24 && bpp != 32)) continue;
			if (compression != 0) continue;
			if (w <= 0 || w > 16384 || h == 0 || Math.Abs(h) > 16384) continue;
			int stride = (w * (bpp / 8) + 3) & ~3;
			if (dataOff < 54 || (long)dataOff + (long)stride * Math.Abs(h) > binary.Length - i) continue;
			bmp = new byte[binary.Length - i];
			Buffer.BlockCopy(binary, i, bmp, 0, bmp.Length);
			return true;
		}
		return false;
	}
}