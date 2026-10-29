using System.Collections.Generic;

namespace ACadSharp.IO;

/// <summary>
/// Base configuration for all the <see cref="CadReaderBase{T}"/> classes.
/// </summary>
public abstract class CadReaderConfiguration
{
	/// <summary>
	/// The reader will try to continue when an exception is thrown
	/// </summary>
	/// <remarks>
	/// The result file may be incomplete or with some objects missing due the error
	/// </remarks>
	public bool Failsafe { get; set; } = true;

	/// <summary>
	/// The reader will try to read and add to the document all <see cref="ObjectType.UNLISTED"/> that are linked to a <see cref="Classes.DxfClass"/>
	/// which may be a proxy or an entity that is not yet supported by ACadSharp, default value is set to false.
	/// </summary>
	/// <remarks>
	/// These entities do not contain any geometric information and will be ignored by the writers.
	/// </remarks>
	public bool KeepUnknownEntities { get; set; } = false;

	/// <summary>
	/// The reader will try to read and add to the document all <see cref="ObjectType.UNLISTED"/> that are linked to a <see cref="Classes.DxfClass"/>
	/// which may be a proxy or an NonGraphicalObject that is not yet supported by ACadSharp, default value is set to false.
	/// </summary>
	/// <remarks>
	/// These NonGraphicalObjects do not contain any information and will be ignored by the writers.
	/// </remarks>
	public bool KeepUnknownNonGraphicalObjects { get; set; } = false;

	/// <summary>
	/// If set to true the reader will skip the proxy graphics section.
	/// </summary>
	/// <value>
	/// default: true
	/// </value>
	[System.Obsolete("This property is deprecated and will be removed in future versions.")]
	public bool IgnoreProxyGraphics { get; set; } = true;

	/// <summary>
	/// Additional directories where the shape files (<c>.shx</c>) referenced by the
	/// document are searched.
	/// </summary>
	/// <remarks>
	/// A <see cref="Entities.Shape"/> is stored by name in DXF, but by number plus a
	/// reference to its shape file in DWG. The reader resolves the name against the
	/// <c>.shx</c> files of the shape file styles in the document; add the folder that
	/// holds the project shape files here when it is not a standard AutoCAD fonts
	/// folder nor the folder of the drawing itself.
	/// </remarks>
	public List<string> FontSearchDirectories { get; } = new List<string>();

	/// <summary>
	/// [PATCH] Automatically degrade the reading strategy when the machine runs low on
	/// memory (or the in-memory structure the reader is about to build would exceed a size
	/// budget), instead of risking an out-of-memory failure:
	/// </summary>
	/// <remarks>
	/// <list type="bullet">
	/// <item><description>DWG: when the objects section would have to be decompressed into
	/// a large in-memory image, the reader switches to the lazy page-decompression mode
	/// (<see cref="DwgReaderConfiguration.UseLazyObjectSection"/>, LRU page cache) before
	/// decompressing, or - for file versions where the lazy mode is unavailable
	/// (R2007/AC1021) - to the on-disk section cache
	/// (<see cref="DwgReaderConfiguration.UseDiskSectionCache"/>).</description></item>
	/// <item><description>DXF: the periodic GC cadence
	/// (<see cref="DxfReaderConfiguration.GCEveryNEntities"/>) is tightened (quarter, floor
	/// 10K) while the pressure persists.</description></item>
	/// </list>
	/// <para>The decision uses the <see cref="MemoryTrimmer.GetPressureInfo"/> probe
	/// (GC memory load / total available memory, threshold
	/// <see cref="MemoryTrimmer.DefaultPressureRatio"/>) plus, for DWG, the decompressed
	/// section image size against
	/// <see cref="DwgReaderConfiguration.AutoDegradeSectionSizeBytes"/>.</para>
	/// <para>Only applied when the caller has not chosen a strategy explicitly: setting
	/// <see cref="DwgReaderConfiguration.UseLazyObjectSection"/>,
	/// <see cref="DwgReaderConfiguration.ParallelDecode"/> or
	/// <see cref="DwgReaderConfiguration.UseDiskSectionCache"/> for DWG takes precedence
	/// and the auto-degrade stays out of the way.</para>
	/// </remarks>
	/// <value>
	/// default: true
	/// </value>
	public bool AutoDegradeOnMemoryPressure { get; set; } = true;
}
