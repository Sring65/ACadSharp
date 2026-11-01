using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ACadSharp.Tests.Entities;

/// <summary>
/// Regression tests for the LWPOLYLINE constant width (DXF group 43 / "global width")
/// serialization gates: the writers must emit 43 when the value is non-zero OR when the
/// source explicitly specified it (even 0.0), and must not emit a spurious 43 for
/// plain default polylines.
/// </summary>
public class LwPolylineConstantWidthTests
{
	private static CadDocument NewDocWithPolyline(out LwPolyline polyline)
	{
		CadDocument doc = new CadDocument(ACadVersion.AC1027);
		polyline = new LwPolyline(new XY(0, 0), new XY(10, 0), new XY(10, 10));
		doc.Entities.Add(polyline);
		return doc;
	}

	private static CadDocument DwgRoundTrip(CadDocument doc)
	{
		MemoryStream ms = new MemoryStream();
		DwgWriter.Write(ms, doc);
		using MemoryStream readStream = new MemoryStream(ms.ToArray());
		return DwgReader.Read(readStream);
	}

	private static CadDocument DxfRoundTrip(CadDocument doc)
	{
		MemoryStream ms = new MemoryStream();
		DxfWriter.Write(ms, doc);
		using MemoryStream readStream = new MemoryStream(ms.ToArray());
		return DxfReader.Read(readStream);
	}

	[Fact]
	public void NonZeroConstantWidth_DwgRoundTrip_Keeps43()
	{
		CadDocument doc = NewDocWithPolyline(out LwPolyline polyline);
		// GDB2CAD synthesis state: value set, ConstantWidthSpecified left false.
		polyline.ConstantWidth = 0.15;

		LwPolyline roundTripped = DwgRoundTrip(doc).Entities.OfType<LwPolyline>().Single();

		Assert.True(roundTripped.ConstantWidthSpecified, "DWG reader must set ConstantWidthSpecified when flag 0x4 present");
		Assert.Equal(0.15, roundTripped.ConstantWidth, 9);
	}

	[Fact]
	public void NonZeroConstantWidth_DxfRoundTrip_Keeps43()
	{
		CadDocument doc = NewDocWithPolyline(out LwPolyline polyline);
		polyline.ConstantWidth = 0.15;

		LwPolyline roundTripped = DxfRoundTrip(doc).Entities.OfType<LwPolyline>().Single();

		Assert.True(roundTripped.ConstantWidthSpecified, "DXF reader must set ConstantWidthSpecified when 43 present");
		Assert.Equal(0.15, roundTripped.ConstantWidth, 9);
	}

	[Fact]
	public void ExplicitZeroConstantWidth_DwgRoundTrip_PreservesZero43()
	{
		CadDocument doc = NewDocWithPolyline(out LwPolyline polyline);
		polyline.ConstantWidth = 0.0;
		polyline.ConstantWidthSpecified = true;

		LwPolyline roundTripped = DwgRoundTrip(doc).Entities.OfType<LwPolyline>().Single();

		Assert.True(roundTripped.ConstantWidthSpecified, "explicit 43=0 must survive the DWG round trip");
		Assert.Equal(0.0, roundTripped.ConstantWidth, 9);
	}

	[Fact]
	public void ExplicitZeroConstantWidth_DxfRoundTrip_PreservesZero43()
	{
		CadDocument doc = NewDocWithPolyline(out LwPolyline polyline);
		polyline.ConstantWidth = 0.0;
		polyline.ConstantWidthSpecified = true;

		LwPolyline roundTripped = DxfRoundTrip(doc).Entities.OfType<LwPolyline>().Single();

		Assert.True(roundTripped.ConstantWidthSpecified, "explicit 43=0 must survive the DXF round trip");
		Assert.Equal(0.0, roundTripped.ConstantWidth, 9);
	}

	[Fact]
	public void DefaultLwPolyline_DwgRoundTrip_NoSpurious43()
	{
		CadDocument doc = NewDocWithPolyline(out _);

		LwPolyline roundTripped = DwgRoundTrip(doc).Entities.OfType<LwPolyline>().Single();

		Assert.False(roundTripped.ConstantWidthSpecified, "default polyline must not gain a constant width flag");
		Assert.Equal(0.0, roundTripped.ConstantWidth, 9);
	}

	/// <summary>
	/// Parses ASCII DXF text into (code, value) pairs. Codes and values strictly alternate
	/// on consecutive lines (a value may be an empty line, e.g. code 3), so no line filtering.
	/// </summary>
	private static List<(int Code, string Value)> ParseDxfPairs(string text)
	{
		string[] lines = text.Split('\n');
		for (int i = 0; i < lines.Length; i++)
		{
			lines[i] = lines[i].TrimEnd('\r');
		}

		List<(int Code, string Value)> pairs = new List<(int, string)>();
		for (int i = 0; i + 1 < lines.Length; i += 2)
		{
			if (string.IsNullOrWhiteSpace(lines[i]))
			{
				break;
			}

			pairs.Add((int.Parse(lines[i].Trim()), lines[i + 1].Trim()));
		}

		return pairs;
	}

	/// <summary>
	/// Returns the code/value pairs belonging to the first LWPOLYLINE entity in the ENTITIES
	/// section (from its 0/LWPOLYLINE marker up to the next 0 marker).
	/// </summary>
	private static List<(int Code, string Value)> GetLwPolylineEntityPairs(List<(int Code, string Value)> pairs)
	{
		int start = -1;
		for (int i = 0; i < pairs.Count; i++)
		{
			if (pairs[i].Code == 0 && pairs[i].Value == "LWPOLYLINE")
			{
				start = i;
				break;
			}
		}

		Assert.True(start >= 0, "DXF must contain an LWPOLYLINE entity");

		List<(int Code, string Value)> entity = new List<(int, string)>();
		for (int i = start; i < pairs.Count; i++)
		{
			if (i > start && pairs[i].Code == 0)
			{
				break;
			}

			entity.Add(pairs[i]);
		}

		return entity;
	}

	[Fact]
	public void DxfText_NonZeroConstantWidth_WritesGroup43()
	{
		CadDocument doc = NewDocWithPolyline(out LwPolyline polyline);
		polyline.ConstantWidth = 0.15;

		MemoryStream ms = new MemoryStream();
		DxfWriter.Write(ms, doc);
		string text = Encoding.UTF8.GetString(ms.ToArray());

		List<(int Code, string Value)> entity = GetLwPolylineEntityPairs(ParseDxfPairs(text));
		List<string> group43 = entity.Where(p => p.Code == 43).Select(p => p.Value).ToList();

		Assert.Equal(1, group43.Count);
		Assert.Equal(0.15, double.Parse(group43[0], System.Globalization.CultureInfo.InvariantCulture), 9);
	}

	[Fact]
	public void DxfText_DefaultLwPolyline_NoGroup43()
	{
		CadDocument doc = NewDocWithPolyline(out _);

		MemoryStream ms = new MemoryStream();
		DxfWriter.Write(ms, doc);
		string text = Encoding.UTF8.GetString(ms.ToArray());

		List<(int Code, string Value)> entity = GetLwPolylineEntityPairs(ParseDxfPairs(text));
		int group43Count = 0;
		foreach ((int Code, string Value) p in entity)
		{
			if (p.Code == 43)
			{
				group43Count++;
			}
		}
		Assert.Equal(0, group43Count);
	}
}
