using System;
using System.Text.RegularExpressions;

namespace WinSSHuttle
{
	/// <summary>
	/// Console Color Helper class that provides coloring to individual commands
	/// </summary>
	public static class ColorConsole
	{
		private static object _lock = new object();
		/// <summary>
		/// WriteLine with color
		/// </summary>
		/// <param name="text"></param>
		/// <param name="color"></param>
		public static void WriteLine(string text, ConsoleColor? color = null)
		{
			if (color.HasValue)
			{
				lock (_lock)
				{
					var oldColor = Console.ForegroundColor;
					if (color == oldColor)
					{
						Console.WriteLine(text);
					}
					else
					{
						Console.ForegroundColor = color.Value;
						Console.WriteLine(text);
						Console.ForegroundColor = oldColor;
					}
				}
			}
			else
			{
				WriteInfo(text);
			}
		}

		/// <summary>
		/// Write with color
		/// </summary>
		/// <param name="text"></param>
		/// <param name="color"></param>
		public static void Write(string text, ConsoleColor? color = null)
		{
			if (color.HasValue)
			{
				lock (_lock)
				{
					var oldColor = Console.ForegroundColor;
					if (color == oldColor)
						Console.Write(text);
					else
					{
						Console.ForegroundColor = color.Value;
						Console.Write(text);
						Console.ForegroundColor = oldColor;
					}
				}
			}
			else
			{
				WriteInfo(text);
			}
		}

		/// <summary>
		/// Writes out a line with color specified as a string
		/// </summary>
		/// <param name="text">Text to write</param>
		/// <param name="color">A console color. Must match ConsoleColors collection names (case insensitive)</param>
		public static void Write(string text, string color)
		{
			if (string.IsNullOrEmpty(color))
			{
				Write(text);
				return;
			}

			if (!Enum.TryParse(color, true, out ConsoleColor col))
			{
				Write(text);
			}
			else
			{
				Write(text, col);
			}
		}

		#region Wrappers and Templates


		/// <summary>
		/// Writes a line of header text wrapped in a in a pair of lines of dashes:
		/// -----------
		/// Header Text
		/// -----------
		/// and allows you to specify a color for the header. The dashes are colored
		/// </summary>
		/// <param name="headerText">Header text to display</param>
		/// <param name="wrapperChar">wrapper character (-)</param>
		/// <param name="headerColor">Color for header text (yellow)</param>
		/// <param name="dashColor">Color for dashes (gray)</param>
		public static void WriteWrappedHeader(string headerText,
												char wrapperChar = '-',
												ConsoleColor headerColor = ConsoleColor.Yellow,
												ConsoleColor dashColor = ConsoleColor.DarkGray)
		{
			if (string.IsNullOrEmpty(headerText))
				return;

			string line = new string(wrapperChar, headerText.Length);

			WriteLine(line, dashColor);
			WriteLine(headerText, headerColor);
			WriteLine(line, dashColor);
		}

		private static Lazy<Regex> _colorBlockRegEx = new Lazy<Regex>(
			() => new Regex("\\[(?<color>.*?)\\](?<text>[^[]*)\\[/\\k<color>\\]", RegexOptions.IgnoreCase),
			isThreadSafe: true);

		/// <summary>
		/// Allows a string to be written with embedded color values using:
		/// This is [red]Red[/red] text and this is [cyan]Blue[/blue] text
		/// </summary>
		/// <param name="text">Text to display</param>
		/// <param name="baseTextColor">Base text color</param>
		public static void WriteEmbeddedColorLine(string text, ConsoleColor? baseTextColor = null)
		{
			if (baseTextColor == null)
				baseTextColor = Console.ForegroundColor;

			if (string.IsNullOrEmpty(text))
			{
				WriteLine(string.Empty);
				return;
			}

			int at = text.IndexOf("[");
			int at2 = text.IndexOf("]");
			if (at == -1 || at2 <= at)
			{
				WriteLine(text, baseTextColor);
				return;
			}

			while (true)
			{
				var match = _colorBlockRegEx.Value.Match(text);
				if (match.Length < 1)
				{
					Write(text, baseTextColor);
					break;
				}

				// write up to expression
				Write(text.Substring(0, match.Index), baseTextColor);

				// strip out the expression
				string highlightText = match.Groups["text"].Value;
				string colorVal = match.Groups["color"].Value;

				Write(highlightText, colorVal);

				// remainder of string
				text = text.Substring(match.Index + match.Value.Length);
			}

			Console.WriteLine();
		}

		#endregion

		#region Success, Error, Info, Warning Wrappers

		/// <summary>
		/// Write a Success Line - green
		/// </summary>
		/// <param name="text">Text to write out</param>
		public static void WriteSuccess(string text)
		{
			WriteLine(text, ConsoleColor.Green);
		}

		/// <summary>
		/// Write a Error Line - Red
		/// </summary>
		/// <param name="text">Text to write out</param>
		public static void WriteError(string text)
		{
			WriteLine(text, ConsoleColor.Red);
		}

		/// <summary>
		/// Write a Warning Line - Yellow
		/// </summary>
		/// <param name="text">Text to Write out</param>
		public static void WriteWarning(string text)
		{
			WriteLine(text, ConsoleColor.Yellow);
		}


		/// <summary>
		/// Write a Info Line - Cyan
		/// </summary>
		/// <param name="text">Text to write out</param>
		public static void WriteInfo(string text)
		{
			WriteLine(text, ConsoleColor.Cyan);
		}

		/// <summary>
		/// Write a Client Line - Dark Gray
		/// </summary>
		/// <param name="text"></param>
		public static void LogClient(string text)
		{
			WriteLine(text, ConsoleColor.DarkGray);
		}

		/// <summary>
		/// Write a Server Line - Gray
		/// </summary>
		/// <param name="text"></param>
		public static void LogServer(string text)
		{
			WriteLine(text, ConsoleColor.Gray);
		}

		#endregion
	}
}
