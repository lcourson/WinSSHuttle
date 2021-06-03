using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace WinSSHuttle
{
	public class Mux : Handler
	{
		#region Properties
		#region Private Constants
		private const int MAX_CHANNELS = 65535;
		private const int LATENCY_BUFFER_SIZE = 32768;
		private const int HDR_LEN = 8;

		#endregion Private Constants

		#region Private
		private ushort _chani;
		private int _want;
		private List<byte> _inbuf = new List<byte>();
		private Queue<byte[]> _outbuf = new Queue<byte[]>();
		private bool _isReading = false;
		#endregion Private

		#region Public
		public int Fullness { get; set; }
		public bool TooFull { get; private set; }
		public Dictionary<ushort, Action<SSHuttleCommands, List<byte>>> Channels { get; set; } = new Dictionary<ushort, Action<SSHuttleCommands, List<byte>>>();
		public Stream RFile { get; private set; }
		public Stream WFile { get; private set; }

		#endregion Public

		#endregion Properties

		#region Constructor
		public Mux(Stream inputSock, Stream outputSock) : base(new List<Stream>() { inputSock, outputSock })
		{
			RFile = inputSock;
			WFile = outputSock;
			base.Callback = (Stream s) => Int_Callback(s);
			Send(0, SSHuttleCommands.CMD_PING, "chicken");
		}

		#endregion Constructor

		#region Actions
		public Action<string> GotRoutes { get; set; }
		public Action<string> GotHostList { get; set; }

		#endregion Actions

		#region Methods
		#region Public
		public void DeleteChannel(ushort channel)
		{
			Helpers.LogDevDebug($"Deleting channel: {channel}");
			Channels.Remove(channel);
		}

		public ushort NextChannel()
		{
			var it = 0;
			while (it < 1024)
			{
				_chani += 1;
				if (_chani > MAX_CHANNELS)
				{
					_chani = 1;
				}

				if (!Channels.ContainsKey(_chani))
				{
					return _chani;
				}
			}

			return 0;
		}

		public void CheckFullness()
		{
			if (Fullness > LATENCY_BUFFER_SIZE)
			{
				if (!TooFull)
				{
					Send(0, SSHuttleCommands.CMD_PING, "rttest");
				}
				TooFull = true;
			}
		}

		public void Send(ushort channel, SSHuttleCommands cmd, string data) => Send(channel, cmd, Encoding.UTF8.GetBytes(data).ToList());

		public void Send(ushort channel, SSHuttleCommands cmd, List<byte> data)
		{
			if (data.Count > 65535)
			{
				throw new Exception("Assert Data Length > 65535");
			}

			var p = StructConverter.Pack("!ccHHH", new List<object>() { 'S', 'S', channel, (ushort)cmd, (ushort)data.Count });
			p.AddRange(data);

			_outbuf.Enqueue(p.ToArray());
			Helpers.Debug2($" > channel={channel} cmd={cmd} len={data.Count} (fullness={Fullness})");
			Fullness += data.Count;
		}

		public async void Fill()
		{
			//Helpers.LogDevDebug($"Fill called on {this}");
			if (_isReading) { return; }
			new Thread(async () => {
			_isReading = true;
				//Helpers.LogDevDebug($"Mux.Fill: Starting");
				try
				{
					byte[] buffer = new byte[LATENCY_BUFFER_SIZE];

					var readBytes = await RFile.ReadAsync(buffer);
					_isReading = false;
					//Helpers.LogDevDebug($"Mux.Fill: Read {readBytes} bytes");
					if (readBytes == 1 && buffer[0] == '\0') //EOF
					{
						//Helpers.LogDevDebug("Mux.Fill: Setting 'Ok' = false");
						Ok = false;
					}

					if (readBytes > 0)
					{
						//Helpers.LogDevDebug($"Mux.Fill: Adding {readBytes} bytes to InBuffer");
						lock (_inbuf)
						{
							_inbuf.AddRange(buffer.Take(readBytes));
						}
					}

					//var read = _rFile.Read(buffer);
					//Helpers.LogDevDebug($"Mux.Fill: Read {read} bytes");
					//if (Encoding.ASCII.GetString(buffer.Take(read).ToArray()) == "")
					//{
					//	Helpers.LogDevDebug("Mux.Fill: Setting Ok = false");
					//	Ok = false;
					//}
					//
					//if (read > 0)
					//{
					//	Helpers.LogDevDebug($"Mux.Fill: Adding {read} bytes to InBuffer");
					//	_inbuf.AddRange(buffer.Take(read));
					//}
				}
				catch (Exception ex)
				{
					Helpers.LogDevDebug($"Mux.Fill: EXCEPTION!!!!!!!!!!!!!!!!!!!!!!!!!!1: {ex}");
					Ok = false;
				}
			}).Start();
			//Helpers.LogDevDebug($"Mux.Fill: Ending");
		}

		#endregion Public

		#region Private
		private int AccountQueued()
		{
			var total = 0;
			foreach (var @byte in _outbuf)
			{
				total += @byte.Length;
			}

			return total;
		}

		private void GotPacket(ushort channel, SSHuttleCommands cmd, List<byte> data)
		{
			Helpers.Debug2($"<  channel={channel} cmd={cmd} len={data.Count}");

			switch (cmd)
			{
				case SSHuttleCommands.CMD_PING:
					Send(0, SSHuttleCommands.CMD_PONG, data);
					break;
				case SSHuttleCommands.CMD_PONG:
					Helpers.Debug2($"C <- s:{channel} Got Ping Response");
					TooFull = false;
					Fullness = 0;
					break;
				case SSHuttleCommands.CMD_EXIT:
					Ok = false;
					break;
				//case SSHuttleCommands.CMD_TCP_CONNECT:
				//	Helpers.Debug2($"C <- s:{channel} Got {cmd}");
				//	break;
				//case SSHuttleCommands.CMD_DNS_REQ:
				//	break;
				//case SSHuttleCommands.CMD_UDP_OPEN:
				//	break;
				case SSHuttleCommands.CMD_ROUTES:
					Helpers.Debug2($"C <- s:{channel} Got Routes");
					GotRoutes?.Invoke(Encoding.UTF8.GetString(data.ToArray()));
					break;
				//case SSHuttleCommands.CMD_HOST_REQ:
				//	break;
				case SSHuttleCommands.CMD_HOST_LIST:
					Helpers.Debug2($"C <- s:{channel} Got Routes");
					GotHostList?.Invoke(Encoding.UTF8.GetString(data.ToArray()));
					break;
				default:
					var cb = Channels[channel];
					if (cb == null)
					{
						Helpers.Log($"Warning: closed channel {channel} got cmd={cmd} len={data.Count}");
					}
					else
					{
						cb.Invoke(cmd, data);
					}
					break;
			}
		}

		private void Flush()
		{
			if (_outbuf.TryDequeue(out var buffer))
			{
				if (buffer.Length > 0)
				{
					WFile.Write(buffer, 0, buffer.Length);
					Helpers.Debug2($"mux wrote: {buffer.Length}/{buffer.Length}");
					WFile.Flush();
				}
			}
		}

		private void Handle()
		{
			//Helpers.LogDevDebug($"Calling Fill()");
			Fill();
			int channel = -1;
			SSHuttleCommands cmd = SSHuttleCommands.NO_OP;
			ushort dataLen;

			while (true)
			{
				if (_inbuf.Count >= Math.Max(HDR_LEN, _want))
				{
					var result = SSHuttlePacketHelper.ParseHeader(_inbuf.Take(HDR_LEN).ToArray());
					channel = result.Item1;
					cmd = (SSHuttleCommands)result.Item2;
					dataLen = result.Item3;
					_want = dataLen + HDR_LEN;
				}

				if (_want > 0 && _inbuf.Count >= _want)
				{
					List<byte> data;
					lock (_inbuf)
					{
						data = _inbuf.Skip(HDR_LEN).Take(_want - HDR_LEN).ToList();
						_inbuf = _inbuf.Skip(_want).ToList();
					}
					_want = 0;
					if (channel > -1 && cmd != SSHuttleCommands.NO_OP)
					{
						GotPacket((ushort)channel, cmd, data);
					}
				}
				else
				{
					break;
				}
			}
			//Helpers.LogDevDebug($"Handle() Complete");
		}

		#endregion Private

		#region Public Override
		public override void PreSelect(ref List<object> r, ref List<object> w, ref List<object> _) {
			r.Add(RFile);
			if (_outbuf.Count > 0)
			{
				w.Add(WFile);
			}
		}

		public override string ToString() => "MUX OBJ";
		#endregion Public Override

		#region Protected Override
		protected override void Int_Callback(Stream _)
		{
			if (RFile.CanRead)
			{
				//Helpers.LogDevDebug($"Calling Handle()");
				Handle();
			}
			if (_outbuf.Count > 0 && WFile.CanWrite) {
				//Helpers.LogDevDebug($"Calling Flush()");
				Flush();
			}
		}

		#endregion Protected Override

		#endregion Methods
	}
}
