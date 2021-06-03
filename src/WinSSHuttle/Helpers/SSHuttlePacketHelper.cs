using System;
using System.Collections.Generic;
using System.Text;

namespace WinSSHuttle
{
	public delegate void PacketEvent(SSHuttleCommands command, ushort channel, byte[] data);
	public class SSHuttlePacketHelper
	{
		public event PacketEvent OnPacket;

		public static Tuple<ushort, ushort, ushort> ParseHeader(byte[] header)
		{
			char s1;
			char s2;
			ushort channel = 0;
			ushort cmd = 0;
			ushort datalen;

			var outputArr = StructConverter.Unpack("!ccHHH", header);
			s1 = (char)outputArr[0];
			s2 = (char)outputArr[1];
			channel = (ushort)outputArr[2];
			cmd = (ushort)outputArr[3];
			datalen = (ushort)outputArr[4];

			//if (s1 != 'S') { Console.WriteLine($"First byte is not 'S'"); }
			//if (s2 != 'S') { Console.WriteLine($"Second byte is not 'S'"); }
			return new Tuple<ushort, ushort, ushort>(channel, cmd, datalen);
		}

		public void ProcessPacket(ushort channel, SSHuttleCommands cmd, byte[] data) => OnPacket?.Invoke(cmd, channel, data);
	}
}
