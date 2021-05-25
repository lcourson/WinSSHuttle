using System;
using System.Collections.Generic;
using System.Net;

namespace WinSSHuttle
{
	internal class WinSSHuttleChannel
	{
		public IPAddress SourceIP { get; internal set; }
		public ushort SourcePort { get; internal set; }
		public ushort Channel { get; internal set; }
		public DateTime ExpireTime { get; internal set; }
		public Action<byte[]> Callback { get; internal set; }
		public Proxy Proxy { get; internal set; }
	}
}
