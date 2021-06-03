using System.IO;
using System.Reflection;
using System.Text;

namespace WinSSHuttle
{
	public class ResourceHelpers
	{
		public static string ReadResourceFile(string name)
		{
			var resourceName = $"WinSSHuttle.PythonSrc.{name}";
			string result;

			using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
			using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
			{
				result = reader.ReadToEnd();
			}

			return result;
		}
		public static string GetPayloadFile(string name, string moduleName)
		{
			string result = ReadResourceFile(name);
			return $"{moduleName}\n{result.Length}\n{result}";
		}
	}
}
