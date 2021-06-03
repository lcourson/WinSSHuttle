using System.Collections.Generic;
using System.IO;

namespace WinSSHuttle
{
	public class Proxy : Handler
	{
		#region Private Properties
		private readonly IWinSSHuttleSocket _wrap1;
		private readonly IWinSSHuttleSocket _wrap2;

		#endregion Private Properties

		#region Constructor
		public Proxy(IWinSSHuttleSocket sockWrap, IWinSSHuttleSocket mux) : base(new List<Stream>() { sockWrap.RSock, sockWrap.WSock, mux.RSock, mux.WSock })
		{
			_wrap1 = sockWrap;
			_wrap2 = mux;
			Callback = (Stream s) => Int_Callback(s);
		}

		#endregion Constructor

		#region Public Override Methods
		public override void PreSelect(ref List<object> r, ref List<object> w, ref List<object> x)
		{
			if (_wrap1.ShutWrite) { _wrap2.NoRead(); }
			if (_wrap2.ShutWrite) { _wrap1.NoRead(); }

			if (_wrap1.ConnectTo != null)
			{
				w.Add(_wrap1.RSock);
			}
			else if (_wrap1.Buffer.Count > 0)
			{
				if (!_wrap2.TooFull())
				{
					w.Add(_wrap2.WSock);
				}
			}
			else if (!_wrap1.ShutRead)
			{
				r.Add(_wrap1.RSock);
			}

			if (_wrap2.ConnectTo != null)
			{
				w.Add(_wrap2.RSock);
			}
			else if (_wrap2.Buffer.Count > 0)
			{
				if (!_wrap1.TooFull())
				{
					w.Add(_wrap1.WSock);
				}
			}
			else if (!_wrap2.ShutRead)
			{
				r.Add(_wrap2.RSock);
			}
		}

		#endregion Public Override Methods

		#region Protected Override Methods
		protected override void Int_Callback(Stream _)
		{
			_wrap1.TryConnect();
			_wrap2.TryConnect();

			_wrap1.Fill();
			_wrap2.Fill();

			_wrap1.CopyTo(_wrap2);
			_wrap2.CopyTo(_wrap1);

			if (_wrap1.Buffer.Count > 0 && _wrap2.ShutWrite)
			{
				_wrap1.Buffer.Clear();
				_wrap1.NoRead();
			}

			if (_wrap2.Buffer.Count > 0 && _wrap1.ShutWrite)
			{
				_wrap2.Buffer.Clear();
				_wrap2.NoRead();
			}

			if (_wrap1.ShutRead && _wrap2.ShutRead && _wrap1.Buffer.Count == 0 && _wrap2.Buffer.Count == 0)
			{
				Ok = false;
				_wrap1.NoWrite();
				_wrap2.NoWrite();
			}
		}

		#endregion Protected Override Methods
	}
}
