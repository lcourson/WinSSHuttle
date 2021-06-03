using System.Collections.Generic;

namespace WinSSHuttle
{
	public class ReaderObject
	{
		public bool IsNone { get; set; }
		public bool IsEOF { get; set; }
		public List<byte> Data { get; set; } = new List<byte>();
	}
}
