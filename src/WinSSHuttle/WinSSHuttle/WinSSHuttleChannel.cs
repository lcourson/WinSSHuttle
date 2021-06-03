using System;
using System.Net;

namespace WinSSHuttle
{
	internal class WinSSHuttleChannel
	{
		public IPAddress SrcIP { get; internal set; }
		public ushort SrcPort { get; internal set; }
		public ushort Channel { get; internal set; }
		public DateTime ExpireTime { get; internal set; }
	}
}
