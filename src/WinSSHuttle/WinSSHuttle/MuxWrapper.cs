using System;
using System.Collections.Generic;
using System.Linq;

namespace WinSSHuttle
{
	public class MuxWrapper : SocketWrapper
	{
		#region Private Properties
		private readonly Mux _mux;
		private readonly ushort _channel;
		//private readonly List<object> _socks = new List<object>();

		#endregion Private Properties

		#region Constructor
		public MuxWrapper(Mux mux, ushort channel) : base(mux.RFile, mux.WFile)
		{
			_mux = mux;
			_channel = channel;
			_mux.Channels.Add(_channel, (cmd, data) => GotPacket(cmd, data));
			Helpers.Debug2($"New channel: {channel}");
		}

		~MuxWrapper()
		{
			NoWrite();
		}
		#endregion Constructor

		#region Methods
		#region Private
		private void SetNoRead()
		{
			if (!ShutRead)
			{
				Helpers.Debug2($"{this}: done reading");
				ShutRead = true;
				MaybeClose();
			}
		}

		private void SetNoWrite()
		{
			if (!ShutWrite)
			{
				Helpers.Debug2($"{this}: done writing");
				ShutWrite = true;
				MaybeClose();
			}
		}

		private void MaybeClose()
		{
			if (ShutRead && ShutWrite)
			{
				Helpers.Debug2($"{this}: closing connection");
				//_mux.DeleteChannel(_channel);
				_mux.Channels.Remove(_channel);
				GC.Collect();
			}
		}

		private void GotPacket(SSHuttleCommands cmd, List<byte> data)
		{
			if (cmd == SSHuttleCommands.CMD_TCP_EOF)
			{
				SetNoRead();
			}
			else if (cmd == SSHuttleCommands.CMD_TCP_STOP_SENDING)
			{
				SetNoWrite();
			}
			else if (cmd == SSHuttleCommands.CMD_TCP_DATA)
			{
				Buffer.AddRange(data);
			}
			else
			{
				throw new Exception($"Unknown command {cmd}, ({data.Count} byte)");
			}

		}

		#endregion Private

		#region Public Override
		public override void NoRead()
		{
			if (!ShutRead)
			{
				_mux.Send(_channel, SSHuttleCommands.CMD_TCP_STOP_SENDING, "");
				SetNoRead();
			}
		}

		public override void NoWrite()
		{
			if (!ShutWrite)
			{
				_mux.Send(_channel, SSHuttleCommands.CMD_TCP_EOF, "");
				SetNoWrite();
			}
		}

		public override bool TooFull() => _mux.TooFull;

		public override string ToString() => $"SW#{base.Id}:Mux#{_channel}";

		#endregion Public

		#region Protected Override
		protected override int uWrite(byte[] buffer)
		{
			if (_mux.TooFull)
			{
				return 0;
			}
			if (buffer.Length > 2048)
			{
				buffer = buffer.Take(2048).ToArray();
			}

			_mux.Send(_channel, SSHuttleCommands.CMD_TCP_DATA, buffer.ToList());
			return buffer.Length;
		}

		protected override ReaderObject uRead()
		{
			var ret = new ReaderObject();
			if (ShutRead)
			{
				ret.IsEOF = true;
				return ret;
			}

			ret.IsNone = true;
			return ret; //No data available right now
		}

		#endregion Protected Override

		#endregion Methods
	}
}
