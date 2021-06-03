using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WinSSHuttle
{
	public class TProxy
	{
		public int ProxyPort { get; private set; }
		public IPAddress ProxyAddress { get; private set; }
		private TcpListener _listener;
		private readonly ManualResetEvent _acceptDone = new ManualResetEvent(false);
		private readonly ManualResetEvent _serverDone = new ManualResetEvent(false);
		private readonly CancellationToken _token;

		public Action<Socket> OnNewConnection;

		public bool ServerReady { get; set; } = false;

		public TProxy(CancellationToken token)
		{
			_token = token;
		}

		public Task StartServer() {
			return Task.Run(() =>
			{
				var locIP = GetLocalIPv4();
				_listener = new TcpListener(locIP, 0);
				_listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
				_listener.Start(100);

				ProxyPort = (_listener.LocalEndpoint as IPEndPoint).Port;
				ProxyAddress = locIP;

				Helpers.Debug1($"C     : TProxy: Server listening on {ProxyAddress}:{ProxyPort}");
				new Thread(new ThreadStart(AcceptConnections)).Start();
				ServerReady = true;

				_serverDone.WaitOne();
			});
		}

		internal static IPAddress GetLocalIPv4()
		{
			foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces()) // Iterate over each network interface
			{
				if (item.NetworkInterfaceType == NetworkInterfaceType.Ethernet && item.OperationalStatus == OperationalStatus.Up)
				{
					IPInterfaceProperties adapterProperties = item.GetIPProperties();
					if (adapterProperties.GatewayAddresses.FirstOrDefault() != null)
					{
						foreach (UnicastIPAddressInformation ip in adapterProperties.UnicastAddresses)
						{
							if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
							{
								return ip.Address;
							}
						}
					}
				}
			}
			return null;
		}

		private void StopListening()
		{
			if (_token.IsCancellationRequested)
			{
				_listener.Stop();
				_serverDone.Set();
			}
		}

		private void AcceptConnections()
		{
			using (_token.Register(() => StopListening()))
			{
				while (!_token.IsCancellationRequested)
				{
					try
					{
						_acceptDone.Reset();
						Helpers.Debug2($"C     : TProxy: Waiting for connection...");
						_listener.BeginAcceptSocket(new AsyncCallback(AcceptClient), _listener);
						_acceptDone.WaitOne();
					}
					catch (Exception e)
					{
						Helpers.LogError($"TProxy - AcceptConnections Exception: {e}");
					}
				}
			}
		}

		private void AcceptClient(IAsyncResult res)
		{
			try
			{
				TcpListener px = (TcpListener)res.AsyncState;
				if (!_token.IsCancellationRequested)
				{
					Socket x = px.EndAcceptSocket(res);
					Helpers.Debug2($"C     : TProxy: Found connection!!!");
					OnNewConnection?.Invoke(x);
				}
			}
			catch (Exception ex)
			{
				Helpers.LogError($"TProxy - AcceptClient Exception: {ex}");
			}
			finally
			{
				_acceptDone.Set();
			}
		}
	}
}
