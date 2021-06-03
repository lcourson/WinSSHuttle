using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WinSSHuttle
{
	public interface IWinSSHuttleSocket
	{
		public bool ShutWrite { get; set; }
		public bool ShutRead { get; set; }
		public object ConnectTo { get; set; }
		public Stream RSock { get; }
		public Stream WSock { get; }

		public List<byte> Buffer { get; set; }
		public int Write(byte[] data);
		public void NoWrite();
		public void NoRead();
		public void TryConnect();
		public void Fill();
		public bool TooFull();
		public void CopyTo(IWinSSHuttleSocket sock);
	}
}
