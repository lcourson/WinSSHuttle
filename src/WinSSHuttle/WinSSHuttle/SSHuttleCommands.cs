namespace WinSSHuttle
{
	public enum SSHuttleCommands
	{
		CMD_EXIT = (ushort)0x4200,
		CMD_PING = (ushort)0x4201,
		CMD_PONG = (ushort)0x4202,
		CMD_TCP_CONNECT = (ushort)0x4203,
		CMD_TCP_STOP_SENDING = (ushort)0x4204,
		CMD_TCP_EOF = (ushort)0x4205,
		CMD_TCP_DATA = (ushort)0x4206,
		CMD_ROUTES = (ushort)0x4207,
		CMD_HOST_REQ = (ushort)0x4208,
		CMD_HOST_LIST = (ushort)0x4209,
		CMD_DNS_REQ = (ushort)0x420a,
		CMD_DNS_RESPONSE = (ushort)0x420b,
		CMD_UDP_OPEN = (ushort)0x420c,
		CMD_UDP_DATA = (ushort)0x420d,
		CMD_UDP_CLOSE = (ushort)0x420e,
		NO_OP = -1

	}
}
