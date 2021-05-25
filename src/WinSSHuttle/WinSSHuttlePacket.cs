using System.Net;

namespace WinSSHuttle
{
	public class WinSSHuttlePacket
	{
		public ushort DstPort { get; internal set; }
		public IPAddress DestIP { get; internal set; }
		public ushort SrcPort { get; internal set; }
		public IPAddress SourceIP { get; internal set; }
		public byte[] Payload { get; internal set; }
		public uint Ifx { get; internal set; }
		public uint SubIfx { get; internal set; }
	}
}