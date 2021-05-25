using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace WinSSHuttle
{
	public class BastionHostInfo
	{
		public string User { get; set; }
		public List<IPAddress> Addresses { get; set; }
		public int Port { get; set; }
	}

	public class Helpers
	{
		public static int OutputLevel { get; set; }
		public static void Debug1(string message) { if (OutputLevel >= 1) { Log(message); } }
		public static void Debug2(string message) { if (OutputLevel >= 2) { Log(message); } }
		public static void Debug3(string message) { if (OutputLevel >= 3) { Log(message); } }
		public static void Debug4(string message) { if (OutputLevel >= 4) { Log(message); } }
		public static void Log(string message) {
			if (message.Length > 7 && message[6] == ':')
			{
				if (message[0] == 'C')
				{
					ColorConsole.LogClient(message);
				}
				else if (message[5] == 'S')
				{
					ColorConsole.LogServer(message);
				}
				else
				{
					ColorConsole.WriteInfo(message);
				}
			}
			else
			{
				ColorConsole.WriteInfo(message);
			}
		}

		public static void LogError(string message)
		{
			if (OutputLevel > 2)
			{
				ColorConsole.WriteError(message);
			}
		}

		public static string GetFilterRangeFromCIDR(string cidr)
		{
			var ipn = IPNetwork.Parse(cidr);
			if (ipn.Cidr == 32)
			{
				return $"ip.DstAddr == {ipn.FirstUsable}";
			}
			return $"(ip.DstAddr >= {ipn.FirstUsable} and ip.DstAddr <= {ipn.LastUsable})";
		}

		public static List<IPAddress> GetIPv4DnsServers()
		{
			List<IPAddress> dnsServers = new List<IPAddress>();
			foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces()) // Iterate over each network interface
			{
				if (item.NetworkInterfaceType == NetworkInterfaceType.Ethernet)// && item.OperationalStatus == OperationalStatus.Up)
				{
					IPInterfaceProperties adapterProperties = item.GetIPProperties();
					foreach (IPAddress dns in adapterProperties.DnsAddresses)
					{
						if (dns.ToString().StartsWith("10."))
						{
							dnsServers.Add(dns);
						}
					}
				}
			}
			return dnsServers;
		}

		public static BastionHostInfo GetHostInfo(string host)
		{
			string user = null;
			int port = -1;

			string bastionName;
			if (host.Contains("@"))
			{
				user = host.Split("@")[0];
				bastionName = host.Split("@")[1];
			}
			else
			{
				bastionName = host;
			}

			if (bastionName.Contains(":"))
			{
				bastionName = bastionName.Split(":")[0];
				if (!int.TryParse(bastionName.Split(":")[1], out port))
				{
					port = 22;
				}
			}

			var ip = Dns.GetHostAddresses(bastionName).ToList();

			return new BastionHostInfo()
			{
				User = user,
				Addresses = ip,
				Port = port
			};
		}
		public static string GetHostFilter(string bastionHost)
		{
			var hi = GetHostInfo(bastionHost);

			var filter = "";
			foreach (var bastionIP in hi.Addresses)
			{
				filter += $" and ip.DstAddr != {bastionIP}";
			}

			return filter;
		}
	}
}
