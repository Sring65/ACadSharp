using ACadSharp.Attributes;
using ACadSharp.Classes;

namespace ACadSharp.Entities;

/// <summary>
/// Represents a <see cref="Wipeout"/> entity.
/// </summary>
/// <remarks>
/// Object name <see cref="DxfFileToken.EntityWipeout"/> <br/>
/// Dxf class name <see cref="DxfSubclassMarker.Wipeout"/>
/// </remarks>
[DxfName(DxfFileToken.EntityWipeout)]
[DxfSubClass(DxfSubclassMarker.Wipeout)]
public class Wipeout : CadWipeoutBase, IDxfClassDefined
{
	/// <inheritdoc/>
	public override string ObjectName => DxfFileToken.EntityWipeout;

	/// <inheritdoc/>
	public override ObjectType ObjectType => ObjectType.UNLISTED;

	/// <inheritdoc/>
	public override string SubclassMarker => DxfSubclassMarker.Wipeout;

	/// <summary>
	/// Default constructor.
	/// </summary>
	public Wipeout()
	{
		this.Flags = ImageDisplayFlags.ShowImage | ImageDisplayFlags.ShowNotAlignedImage | ImageDisplayFlags.UseClippingBoundary;
	}

	/// <summary>
	/// Border width (modern WIPEOUT, group code 41, units = drawing units).
	/// 0/absent = no border (legacy R13 eraser behavior).
	/// </summary>
	[DxfCodeValue(41)]
	public double BorderWidth { get; set; }

	/// <summary>
	/// Fill color (modern WIPEOUT, group code 42): 0=background(eraser), 71=black, 72=white, 73=gray.
	/// 0/absent = background eraser (legacy R13 WIPEOUT with no image erases to the background color).
	/// NOTE: group code 42 is a Real in DXF, so the property must be double to match the reader value type
	/// (a non-double property would fail DxfPropertyBase.SetValue's strict type check and be silently dropped).
	/// </summary>
	[DxfCodeValue(42)]
	public double FillColor { get; set; }

	/// <summary>
	/// Fill transparency (modern WIPEOUT, group code 43), range 0..1.
	/// Parsed for completeness; not yet applied to the raster/SVG output.
	/// </summary>
	[DxfCodeValue(43)]
	public double FillTransparency { get; set; }

	/// <inheritdoc/>
	public DxfClass GetDxfClass()
	{
		return new DxfClass
		{
			ApplicationName = "WipeOut",
			CppClassName = DxfSubclassMarker.Wipeout,
			DwgVersion = ACadVersion.AC1015,
			DxfName = DxfFileToken.EntityWipeout,
			ItemClassId = 498,
			MaintenanceVersion = 0,
			ProxyFlags = ProxyFlags.EraseAllowed | ProxyFlags.TransformAllowed | ProxyFlags.ColorChangeAllowed | ProxyFlags.LayerChangeAllowed | ProxyFlags.LinetypeChangeAllowed | ProxyFlags.LinetypeScaleChangeAllowed | ProxyFlags.VisibilityChangeAllowed | ProxyFlags.R13FormatProxy,
			WasZombie = false,
		};
	}
}