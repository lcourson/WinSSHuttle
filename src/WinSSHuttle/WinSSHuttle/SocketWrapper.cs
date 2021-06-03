using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace WinSSHuttle
{
	public class SocketWrapper : IWinSSHuttleSocket
	{
		#region Properties
		#region Private
		private string _exc;
		private Socket _socket;

		#endregion Private

		#region Protected
		protected string PeerName { get; set; }
		protected int Id { get; private set; }

		#endregion Protected
		#region Public
		public bool ShutWrite { get; set; }
		public bool ShutRead { get; set; }
		public object ConnectTo { get; set; }
		public Stream RSock { get; private set; }
		public Stream WSock { get; private set; }
		public List<byte> Buffer { get; set; } = new List<byte>();
		#endregion Public
		#endregion Properties

		#region Constructors
		public SocketWrapper(Socket socket) : this(new NetworkStream(socket), new NetworkStream(socket))
		{
			_socket = socket;
			PeerName = $"{(_socket.RemoteEndPoint as IPEndPoint).Address}:{(_socket.RemoteEndPoint as IPEndPoint).Port}";
		}
		public SocketWrapper(Stream r, Stream w, string peerName = null)
		{
			GlobalSocketObject.SWCount += 1;
			Id = GlobalSocketObject.SWCount;
			RSock = r;
			if (RSock.CanTimeout)
			{
				RSock.ReadTimeout = 100;
			}
			WSock = w;
			Helpers.Debug3($"creating new SocketWrapper ({GlobalSocketObject.SWCount} now exist)");
			if (peerName != null && peerName.Length > 2)
			{
				PeerName = peerName;
			}
			TryConnect();
		}

		~SocketWrapper()
		{
			if (_socket != null && _socket.Connected)
			{
				_socket.Shutdown(SocketShutdown.Both);
				_socket.Close();
			}

			GlobalSocketObject.SWCount -= 1;
			Helpers.Debug1($"{this}: deleting ({GlobalSocketObject.SWCount} remain)");
			if (_exc != null)
			{
				Helpers.Debug1($"{this}: error was: {_exc}");
			}
		}
		#endregion

		#region Methods
		#region Private Methods
		private void SetErr(string error)
		{
			if (_exc == null)
			{
				_exc = error;
			}
			NoWrite();
			NoRead();
		}
		#endregion Private Methods

		#region Protected Virtual Methods
		protected virtual int uWrite(byte[] buffer)
		{
			if (ConnectTo != null)
			{
				return 0; //still connecting
			}

			try
			{
				WSock.Write(buffer, 0, buffer.Length);
				return buffer.Length;
			}
			catch (Exception ex)
			{
				Helpers.Debug1($"{this}: uwrite: got {ex.Message}");
				if (ex is IOException)
				{
					if (_socket != null && !_socket.Connected)
					{
						NoWrite();
					}
				}
				return 0;
			}
		}

		protected virtual ReaderObject uRead()
		{
			if (ConnectTo != null)
			{
				return new ReaderObject() { IsNone = true }; //still connecting
			}

			if (ShutRead)
			{
				return null;
			}

			byte[] buffer = new byte[65536];
			try
			{
				var read = -1;

				if (_socket != null)
				{
					if (_socket.Available > 0)
					{
						read = RSock.Read(buffer);
						return new ReaderObject() { Data = buffer.Take(read).ToList() };
					}

					return new ReaderObject();
				}
				else
				{
					read = RSock.Read(buffer);
					return new ReaderObject() { Data = buffer.Take(read).ToList() };
				}
			}
			catch (Exception ex)
			{
				SetErr($"{this}: uread: {ex}");
				return new ReaderObject() { IsEOF = true };
			}
		}
		#endregion Protected Virtual Methods

		#region Public Virtual Methods
		public virtual void NoRead()
		{
			if (!ShutRead)
			{
				Helpers.Debug2($"{this}: done reading");
				ShutRead = true;
				if (_socket != null && _socket.Connected)
				{
					_socket.Shutdown(SocketShutdown.Receive);
				}
			}
		}

		public virtual void NoWrite()
		{
			if (!ShutWrite)
			{
				Helpers.Debug2($"{this}: done writing");
				ShutWrite = true;
				try
				{
					if (_socket != null && _socket.Connected)
					{
						_socket.Shutdown(SocketShutdown.Send);
					}
				}
				catch (Exception e)
				{
					SetErr($"{this}: nowrite: {e}");
				}
			}
		}

		public virtual bool TooFull() => false;

		#endregion Public Virtual Methods

		#region Public Methods
		public void TryConnect()
		{
			if (ConnectTo != null && ShutWrite)
			{
				NoRead();
				ConnectTo = null;
			}

			if (ConnectTo == null)
			{
				return; //already connected
			}
		}

		public void Fill()
		{
			if (Buffer.Count > 0) return;

			if (_socket != null && !_socket.Connected)
			{
				NoRead();
			}

			var rb = uRead();
			if (rb != null && !rb.IsNone)
			{
				if (rb.IsEOF)
				{
					NoRead();
				}
				else
				{
					Buffer.AddRange(rb.Data);
				}
			}
		}

		public void CopyTo(IWinSSHuttleSocket outWrapper)
		{
			if (Buffer.Count > 0)
			{
				var wrote = outWrapper.Write(Buffer.ToArray());
				Buffer = Buffer.Skip(wrote).ToList();
			}

			if (ShutRead)
			{
				outWrapper.NoWrite();
			}
		}

		public int Write(byte[] buffer)
		{
			if (buffer.Length == 0)
			{
				throw new ArgumentException("buffer");
			}

			return uWrite(buffer);
		}

		#endregion Public Methods

		public override string ToString() => $"SW#{Id}:{PeerName}";

		#endregion Methods
	}
}
