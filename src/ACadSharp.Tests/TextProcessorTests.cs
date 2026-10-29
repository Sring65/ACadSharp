using System;
using ACadSharp.Entities;
using Xunit;

namespace ACadSharp.Tests;

/// <summary>
/// MTEXT format code parsing (ACadSharp core TextProcessor.Parse / MText.PlainText).
/// [PATCH 2026-10-29] Adds format codes missing upstream: \L/\l/\O/\o underline/strikethrough (no argument),
/// \B1;/\B0; \I1;/\I0; bold/italic, \S stacked text, \W/\w/\Q/\K/\k/\T/\D/\Z/\R parameter codes,
/// \' text style, \~ no-break space, \t tab and \U+hhhh Unicode.
/// Key fix: when a parameter code lacks its terminating semicolon the old implementation computed
/// end = -1 + 1 = 0 and Parse looped forever; it now jumps to the end of the string.
/// Newlines: \P/\n uniformly emit '\n' (GetPlainTextLines splits on \r\n/\r/\n, so it stays compatible).
/// Motivation: the DataCheck QC front end showed garbled fragments in its text layer - leftover
/// control codes mixed with CJK text, i.e. raw format codes kept verbatim in PlainText.
/// </summary>
public class TextProcessorTests
{
	private static string Parse(string text) => ACadSharp.Text.TextProcessor.Parse(text, out _);

	// ===== Existing format codes (regression: pre-patch behavior must stay unchanged) =====
	[Theory]
	[InlineData("hello 世界", "hello 世界")]
	[InlineData("", "")]
	[InlineData(@"\C1;红色", "红色")]
	[InlineData(@"\c16711680;彩色", "彩色")]
	[InlineData(@"{\fSimSun|b0|i0|c134|p2;宋体}", "宋体")]
	[InlineData(@"{\H2.5x;高度}", "高度")]
	[InlineData(@"\Aqc;居中", "居中")]
	[InlineData(@"a\\b", "a\\b")]
	// Pre-existing upstream behavior (not a patch regression): when { / } directly follows an escaped
	// backslash, the prev heuristic treats the brace as escaped and keeps it verbatim
	// ("a\\{b" -> "a\{b"). Strict MTEXT semantics would give a literal backslash + group start;
	// the current behavior is locked in here on purpose.
	[InlineData(@"a\\{b", @"a\{b")]
	[InlineData(@"a\\}b", @"a\}b")]
	[InlineData(@"{abc}", "abc")]
	[InlineData(@"\pxqc;Text in the center\P\pq*;Hello", "Text in the center\nHello")]
	public void Parse_ExistingFormatCodes(string input, string expected)
	{
		Assert.Equal(expected, Parse(input));
	}

	// ===== [PATCH 2026-10-29] New format codes =====
	[Theory]
	[InlineData(@"\L下划线\l关闭", "下划线关闭")]
	[InlineData(@"\O删除线\o", "删除线")]
	[InlineData(@"\S1/2;", "1/2")]
	[InlineData(@"\S1^2;", "1^2")]
	[InlineData(@"\S1_2;", "1_2")]
	[InlineData(@"\S1/2;分数", "1/2分数")]
	[InlineData(@"\B1;粗体", "粗体")]
	[InlineData(@"\B0;", "")]
	[InlineData(@"\I1;斜体", "斜体")]
	[InlineData(@"\i0;", "")]
	[InlineData(@"\W2.5x;加宽", "加宽")]
	[InlineData(@"\w0.5;", "")]
	[InlineData(@"\Q-15;倾斜", "倾斜")]
	[InlineData(@"\K10;字距", "字距")]
	[InlineData(@"\T2;tracking", "tracking")]
	[InlineData(@"\Dmm/dd/yy;日期", "日期")]
	[InlineData(@"\Z1;", "")]
	[InlineData(@"\R1;", "")]
	[InlineData(@"\'Standard;样式文字", "样式文字")]
	[InlineData(@"A\~B", "A B")]
	[InlineData(@"A\tB", "A\tB")]
	[InlineData(@"{\fArial|b0|i0|c1|p34;\B1;粗体\B0;正常\P}", "粗体正常\n")]
	public void Parse_NewFormatCodes(string input, string expected)
	{
		Assert.Equal(expected, Parse(input));
	}

	// ===== [PATCH 2026-10-29] Unicode \U+hhhh =====
	[Theory]
	[InlineData(@"\U+4E2D", "中")]
	[InlineData(@"\U+4E2D\U+6587\U+5B57", "中文字")]
	[InlineData(@"A\U+0041B", "AAB")]
	// On a parse failure only "\U" is skipped; the rest is kept verbatim (nothing swallowed).
	[InlineData(@"\U+ZZZZ", "+ZZZZ")]
	[InlineData(@"\U+12", "+12")]
	[InlineData(@"\U", "")]
	public void Parse_Unicode(string input, string expected)
	{
		Assert.Equal(expected, Parse(input));
	}

	// ===== [PATCH 2026-10-29] Missing terminating semicolon: the old implementation computed
	// end = 0 and Parse looped forever; it now jumps to the end of the string. =====
	// Note: if this group regresses into an infinite loop the test hangs (xunit has no default per-test timeout).
	[Theory]
	[InlineData(@"\C3", "")]
	[InlineData(@"前缀\C3", "前缀")]
	[InlineData(@"\fSimSun", "")]
	[InlineData(@"\H2.5x", "")]
	[InlineData(@"\Aqc", "")]
	[InlineData(@"\W2.5", "")]
	[InlineData(@"\B1", "")]
	[InlineData(@"\S1/2", "1/2")]
	[InlineData(@"\S;", "")]
	public void Parse_MissingSemicolonNoInfiniteLoop(string input, string expected)
	{
		Assert.Equal(expected, Parse(input));
	}

	// ===== Newline unified to '\n' + MText.PlainText / GetPlainTextLines integration =====
	[Fact]
	public void Parse_NewlineEmitsLf()
	{
		Assert.Equal("abc\ndef", Parse(@"abc\Pdef"));
		Assert.Equal("abc\ndef", Parse(@"abc\ndef"));
	}

	[Fact]
	public void MText_PlainText_AndLines()
	{
		var m = new MText { Value = @"{\fSimSun|b0|i0;第一行\P第二行}" };
		Assert.Equal("第一行\n第二行", m.PlainText);
		Assert.Equal(new[] { "第一行", "第二行" }, m.GetPlainTextLines());
	}
}
