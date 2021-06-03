using System.Text.RegularExpressions;

namespace WinSSHuttle
{
	class PythonShellUtils
	{
		private static bool _findUnsafe(string input)
		{
			return Regex.Match(input, "[^\\w@%+=:,./-]").Success; //Missing re.ASCII
		}

		public static string Quote(string input)
		{
			if (string.IsNullOrEmpty(input))
			{
				return "''";
			}

			if (!_findUnsafe(input))
			{
				return input;
			}

			return "'" + input.Replace("'", "'\\\"'\\\"'") + "'";
		}
	}
}
