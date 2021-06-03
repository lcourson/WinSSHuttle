using System;
using System.Collections.Generic;
using System.IO;

namespace WinSSHuttle
{
	public class Handler
	{
		#region Properties
		public bool Ok { get; set; }
		public List<Stream> Socks;

		#endregion Properties

		#region Actions
		public Action<Stream> Callback;

		#endregion Actions

		#region Constructor
		public Handler(List<Stream> socks = null, Action<Stream> callback = null)
		{
			Ok = true;
			Socks = socks;
			if (callback != null)
			{
				Callback = callback;
			}
			else
			{
				Callback = (Stream s) => Int_Callback(s);
			}
		}

		#endregion Constructor

		#region Methods
		#region Public Virtual Methods
		public virtual void PreSelect(ref List<object> r, ref List<object> w, ref List<object> x)
		{
			foreach (var i in Socks)
			{
				r.Add(i);
			}
		}

		#endregion Public Virtual Methods

		#region Protected Virtual Methods
		protected virtual void Int_Callback(Stream _)
		{
			Helpers.Log($"--no callback defined-- {this}");

			foreach (var s in Socks)
			{
				if (!s.CanRead)
				{
					Helpers.Log($"--closed-- {this}");
					Socks = new List<Stream>();
					Ok = false;
				}

			}
		}

		#endregion Protected Virtual Methods

		#endregion Methods
	}
}
