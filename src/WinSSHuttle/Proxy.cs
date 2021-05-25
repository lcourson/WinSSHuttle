using System;
using System.Linq;
using System.Net.Sockets;

namespace WinSSHuttle
{
	public delegate void DataReceivedHandler(byte[] data, ushort channel);

	public class Proxy
	{
		public Action<Socket> Callback { get; set; }

		private readonly Socket _socket;
		private readonly ushort _channel;
		private byte[] _buffer = new byte[2048];

		public event DataReceivedHandler OnDataReceived;
		public event EventHandler OnDisconnect;

		private bool _disconnectCalled = false;

		public Proxy(Socket s, ushort channel)
		{
			_socket = s;
			_channel = channel;
		}

		public void BeginReceive()
		{
			_socket.BeginReceive(_buffer, 0, 2048, SocketFlags.None, new AsyncCallback(DataReceived), null);
		}
		private void DataReceived(IAsyncResult res)
		{
			try
			{
				SocketError err = SocketError.Success;
				int received = _socket.EndReceive(res, out err);
				if (err != SocketError.Success)
				{
					Disconnect();
					return;
				}
				if (received > 0)
				{
					OnDataReceived?.Invoke(_buffer.ToList().Take(received).ToArray(), _channel);
				}
				BeginReceive();
			}
			catch (Exception ex)
			{
				Helpers.LogError($"Proxy - DataReceived Exception: {ex}");
				Disconnect();
			}
		}

		public void SendData(byte[] data)
		{
			try
			{
				if (_socket != null && _socket.Connected)
				{
					_socket.BeginSend(data, 0, data.Length, SocketFlags.None, new AsyncCallback(DataSent), null);
				}
			}
			catch (Exception ex)
			{
				Helpers.LogError($"Proxy - SendData Exception: {ex}");
				Disconnect();
			}
		}

		public void Disconnect(bool send = true)
		{
			if (!_disconnectCalled)
			{
				_disconnectCalled = true;
				if (send)
				{
					OnDisconnect?.Invoke(_channel, null);
				}

				if (_socket.Connected)
				{
					_socket.Shutdown(SocketShutdown.Both);
					_socket.Close();
				}
			}
		}

		private void DataSent(IAsyncResult res)
		{
			try
			{
				int sent = _socket.EndSend(res);
				if (sent < 0)
				{
					Disconnect();
					return;
				}
			}
			catch (Exception ex)
			{
				Helpers.LogError($"Proxy - DataSent Exception: {ex}");
			}
		}
	}
}
