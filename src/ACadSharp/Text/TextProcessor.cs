using CSUtilities.Extensions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ACadSharp.Text
{
	public static class TextProcessor
	{
		public static string Parse(string text, out List<string> groups)
		{
			groups = new List<string>();
			if (string.IsNullOrEmpty(text))
			{
				return text;
			}

			StringBuilder sb = new StringBuilder();

			int index = 0;
			bool openGroup = false;
			while (index < text.Length)
			{
				char? prev = text.TryGet(index - 1);
				char? current = text.TryGet(index);
				char? next = text.TryGet(index + 1);

				if (current == '\\' && next.HasValue)
				{
					switch (next)
					{
						case '}':
						case '{':
						case '\\':
							sb.Append(next);
							index += 2;
							break;
						case 'A':
							jump(text, index, out index);
							break;
						case 'c':
						case 'C':
							processColor(text, index, out index);
							break;
						case 'f':
						case 'F':
							processFont(text, index, out index);
							break;
						case 'h':
						case 'H':
							processHeight(text, index, out index);
							break;
						case 'p':
							processJustification(text, index, out index);
							break;
						case 'P':
						case 'n':
							sb.Append('\n');
							index += 2;
							break;
						// [PATCH 2026-10-29] Format codes missing upstream: underline/strikethrough
						// toggles (no argument), bold/italic (\B1;/\B0;, \I1;/\I0;), stacked text
						// \S1/2;, width/oblique/tracking/date parameter codes, text style \'..;,
						// no-break space \~ and Unicode \U+hhhh. Without them, PlainText keeps
						// leftover fragments such as raw "S1/2;" codes mixed with CJK text
						// (garbled text layer in the DataCheck QC front end).
						case 'L':
						case 'l':
						case 'O':
						case 'o':
							index += 2;
							break;
						case 'S':
							processStacked(text, index, out index, sb);
							break;
						case 'B':
						case 'b':
						case 'I':
						case 'i':
						case 'W':
						case 'w':
						case 'Q':
						case 'K':
						case 'k':
						case 'T':
						case 'D':
						case 'Z':
						case 'R':
						case '\'':
							jump(text, index, out index);
							break;
						case '~':
							sb.Append(' ');
							index += 2;
							break;
						case 't':
							// \t tab character (AutoCAD MTEXT format code)
							sb.Append('\t');
							index += 2;
							break;
						case 'U':
							processUnicode(text, index, out index, sb);
							break;
						default:
							index++;
							break;
					}
				}
				else if (current == '{' && prev != '\\')
				{
					openGroup = true;
					index++;
				}
				else if (current == '}' && prev != '\\')
				{
					openGroup = false;
					index++;
				}
				else
				{
					sb.Append(current);
					index++;
				}
			}

			return sb.ToString();
		}


		public static string Unescape(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return text;
			}

			StringBuilder sb = new StringBuilder();

			int index = 0;
			bool openGroup = false;
			while (index < text.Length)
			{
				int currIndex = text.IndexOf(@"\", index);
				if (currIndex <= 0)
				{
					string s = text.Substring(index, text.Length - index);
					if (openGroup && s.Contains("}"))
					{
						s = s.Replace("}", string.Empty);
						openGroup = false;
					}
					sb.Append(s);
					break;
				}

				char prev = text[currIndex - 1];
				char current = text[currIndex];
				char next = text[currIndex + 1];

				if (prev == '{')
				{
					currIndex--;
					openGroup = true;
				}

				if (currIndex > index)
				{
					string s = text.Substring(index, currIndex - index);
					if (openGroup && s.Contains("}"))
					{
						s = s.Replace("}", string.Empty);
						openGroup = false;
					}
					sb.Append(s);
				}

				int f;
				switch (next)
				{
					case 'f':
					case 'F':
						processFont(text, currIndex, out f);
						currIndex = f;
						break;
					case 'c':
					case 'C':
						processColor(text, currIndex, out f);
						currIndex = f;
						break;
					case 'P':
					case 'n':
						sb.Append('\n');
						currIndex += 2;
						break;
					case 'r':
						break;
					case '}':
					case '{':
					case '\\':
						sb.Append(next);
						break;
				}

				index = currIndex;
			}

			return sb.ToString();
		}

		private static void processFont(string text, int start, out int end)
		{
			//Example:
			//- Font: {\\fCalibri|b0|i0|c0|p34;Calibri\\Fcdm|c0; CDM \\fConsolas|b0|i0|c0|p49;Consolas\\P}
			int fi = text.IndexOf(';', start);
			end = fi < 0 ? text.Length : fi + 1;
			var data = text.Substring(start, end - start).Split('|');

			FontData fontData = new FontData
			{
				Name = data[0],
			};
		}

		private static void jump(string text, int start, out int end)
		{
			//Example:
			//\\(unknown escape code)(escape text);
			// [PATCH 2026-10-29] When the terminating semicolon is missing, jump to the end of the
			// string instead of end = 0, which made Parse loop forever.
			int i = text.IndexOf(';', start);
			end = i < 0 ? text.Length : i + 1;
		}

		private static void processStacked(string text, int start, out int end, StringBuilder sb)
		{
			// \S1/2; \S1^2; \S1_v; -> keep the numerator/denominator text, drop the control code and terminating semicolon.
			int i = text.IndexOf(';', start);
			if (i < 0)
			{
				sb.Append(text.Substring(start + 2, text.Length - start - 2));
				end = text.Length;
				return;
			}
			sb.Append(text.Substring(start + 2, i - start - 2));
			end = i + 1;
		}

		private static void processUnicode(string text, int start, out int end, StringBuilder sb)
		{
			// \U+hhhh (no terminating semicolon). On a parse failure only "\U" is skipped so the following content is not swallowed.
			int i = start + 2; // first character after "\U"
			if (i < text.Length && text[i] == '+')
			{
				int n = Math.Min(4, text.Length - i - 1);
				if (n == 4 && int.TryParse(text.Substring(i + 1, 4), NumberStyles.HexNumber, null, out int code))
				{
					sb.Append((char)code);
					end = i + 1 + n;
					return;
				}
			}
			end = start + 2;
		}

		private static void processColor(string text, int start, out int end)
		{
			//Example:
			//- Color text {\\C3;green}, {\\C5;blue}, {\\C1;red}, ByLayer, {\\C0;ByBlock}, {\\C21;\\c5872631;TrueColor}
			int ci = text.IndexOf(';', start);
			end = ci < 0 ? text.Length : ci + 1;
		}

		private static void processHeight(string text, int start, out int end)
		{
			//Example:
			//- {\\H2x;Double height \\H0.875x;height is: 0.35\\H1.14286x;\\P}
			int hi = text.IndexOf(';', start);
			end = hi < 0 ? text.Length : hi + 1;
		}

		private static void processJustification(string text, int start, out int end)
		{
			//Example:
			//\\pxqc;Text in the center\\P\\pq*;Hello this is an mText\\P
			int ji = text.IndexOf(';', start);
			end = ji < 0 ? text.Length : ji + 1;
		}
	}

	internal struct FontData
	{
		public string Name { get; set; }
	}
}
