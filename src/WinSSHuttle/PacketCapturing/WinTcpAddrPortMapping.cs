using System;
using System.Net;

namespace WinSSHuttle.PacketCapturing
{
	public class WinTcpAddrPortMapping
	{
		public IPAddress OriginalSourceIP { get; set; }
		public IPAddress OriginalDestinationIP { get; set; }
		public ushort OriginalSourcePort { get; set; }
		public ushort OriginalDestinationPort { get; set; }
		public DateTime LastUsed { get; set; } = DateTime.Now;
	}
}
