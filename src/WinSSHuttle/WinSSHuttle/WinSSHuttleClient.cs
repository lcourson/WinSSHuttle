using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinSSHuttle.PacketCapturing;

namespace WinSSHuttle
{

	internal class WinSSHuttleClient
	{
		private StreamReader _sshRelayDebug;
		private Process _sshProc;
		private TProxy _tproxy;

		private readonly CancellationToken _mainAppToken;
		private readonly CancellationTokenSource _classTokenSrc = new CancellationTokenSource();

		private readonly PacketCapture _outCap;

		private List<WinSSHuttleChannel> _udpBySrc = new List<WinSSHuttleChannel>();

		private readonly string _bastionHost;
		private readonly FileInfo _authKey;
		private readonly string _password;
		private readonly FileInfo _plinkExe;
		private readonly bool _autoAcceptHostKey;
		private readonly short _tproxyPriority;

		private List<Handler> _handlers = new List<Handler>();

		#region Constructors
		public WinSSHuttleClient(AppOptions config): this(config.Token, config.Output, config.PlinkExe, config.Network.ToList(), config.BastionHost, config.Password, config.PrivateKey, config.IncludeDNS, config.AcceptHostKey, config.Priority)
		{
		}

		public WinSSHuttleClient(CancellationToken token, int output, FileInfo plinkExe, List<string> networks, string bastionHost, string password, FileInfo authKey, bool includeDNS, bool autoAcceptHostKey, short priority)
		{
			_autoAcceptHostKey = autoAcceptHostKey;
			_mainAppToken = token;
			_plinkExe = plinkExe;
			_bastionHost = bastionHost;
			_password = password;
			_authKey = authKey;
			_tproxyPriority = priority;

			if ((_authKey != null && !_authKey.Exists) && string.IsNullOrWhiteSpace(password))
			{
				throw new Exception("Authentication Method not provided");
			}
			var filter = $"outbound";
			filter += Helpers.GetHostFilter(bastionHost);
			var netFilter = new List<string>();

			foreach(var net in networks)
			{
				netFilter.Add(Helpers.GetFilterRangeFromCIDR(net));
			}

			var dnsFilters = new List<string>();
			if (includeDNS)
			{
				foreach (var dns in Helpers.GetIPv4DnsServers())
				{
					dnsFilters.Add($"ip.DstAddr == {dns}");
				}
			}

			filter += " and (";
			filter += string.Join(" or ", netFilter.ToArray());

			if (dnsFilters.Count > 0)
			{
				filter += " or (udp.DstPort == 53 and (";
				filter += string.Join(" or ", dnsFilters.ToArray());
				filter += "))";
			}

			filter += ")";

			_outCap = new PacketCapture(filter, _classTokenSrc.Token);
		}

		#endregion

		public Task Start()
		{
			try
			{
				_tproxy = new TProxy(_classTokenSrc.Token);
				var tprox = _tproxy.StartServer();

				while (!_tproxy.ServerReady)
				{
					Task.Delay(100).Wait();
				}

				_outCap.TProxyPort = _tproxy.ProxyPort;
				_outCap.TProxyAddress = _tproxy.ProxyAddress;

				var sshDebugHandler = MonitorSSHDebug();
				var serverReady = false;
				var mux = StartSSH().Result;

				if (mux != null)
				{
					_handlers.Add(mux);

					mux.GotRoutes = (string routes) => { serverReady = true; };
					mux.GotHostList = (string hostlist) => { };

					return Task.Run(() => {
						while (!serverReady)
						{
							CheckComms(mux);
						}

						ColorConsole.WriteInfo("Server Is Ready!!!");

						_outCap.OnAcceptUDP = (packet) => OnAcceptUDP(packet, mux); //UDP processing
						_tproxy.OnNewConnection = (Socket s) => OnAcceptTCP(s, mux); //TCP processing

						var oCap = _outCap.StartCapture(_tproxyPriority);

						ColorConsole.WriteInfo("Client Connected!!!");
						ColorConsole.WriteInfo("Hit CTRL+C to exit...\n");

						while (!_mainAppToken.IsCancellationRequested)
						{
							try
							{
								CheckComms(mux);
							}
							catch (Exception ex)
							{
								Helpers.LogError($"Exception in 'RunOnce' code: {ex}");
								break;
							}
						}

						// Cancel the token so tasks finish their processing
						_classTokenSrc.Cancel();

						var x = new Stopwatch();
						x.Start();
						Helpers.Debug2("Waiting on Packet Capture to stop");
						oCap.Wait();
						x.Stop();
						Helpers.Debug2($"  Packet Capture has Stopped... {x.ElapsedMilliseconds}");
						x.Reset();

						x.Start();
						Helpers.Debug2("Waiting on TProxy to stop");
						tprox.Wait();
						x.Stop();
						Helpers.Debug2($"  TProxy has Stopped... {x.ElapsedMilliseconds}");
						x.Reset();

						if (!_sshProc.HasExited)
						{
							_sshProc.WaitForExit();
							Helpers.Debug2($"PLink Process Exited: {_sshProc.ExitCode}");
						}

						x.Start();
						Helpers.Debug2("Waiting on SSH Debug Handler to stop");
						sshDebugHandler.Wait();
						x.Stop();
						Helpers.Debug2($"  SSH Debug Handler has Stopped... {x.ElapsedMilliseconds}");

						Helpers.Debug2($"PLink Process Exited");
					});
				}
				else
				{
					_classTokenSrc.Cancel();
					tprox.Wait();
					return Task.FromResult(-1);
				}
			}
			catch(Exception ex)
			{
				Helpers.LogError($"Exception: {ex}");
				_classTokenSrc.Cancel();
				return Task.FromResult(-1);
			}
		}

		private void CheckComms(Mux mux)
		{
			if (_sshProc.HasExited)
			{
				Helpers.LogError($"ssh connection to server (pid {_sshProc.Id}) exited with returncode {_sshProc.ExitCode}");
			}
			RunOnce(_handlers, mux);
			mux.CheckFullness();
			Task.Delay(50).Wait();
		}

		private void RunOnce(List<Handler> handlers, Mux mux)
		{
			var r = new List<object>();
			var w = new List<object>();
			var x = new List<object>();
			var to_remove = handlers.Where((h) => { return !h.Ok; }).ToArray();

			foreach(var h in to_remove)
			{
				handlers.Remove(h);
			}

			foreach(var s in handlers)
			{
				s.PreSelect(ref r, ref w, ref x);
			}

			//Helpers.Debug2($"Waiting: {handlers.Count} r={r.Count} w={w.Count} x={x.Count} (fullness={mux.Fullness}/{mux.TooFull})");

			var ready = new List<object>();
			ready.AddRange(r);
			ready.AddRange(w);
			ready.AddRange(x);

			//Helpers.Debug2($"  Ready: {handlers.Count} r={r.Count} w={w.Count} x={x.Count} (fullness={mux.Fullness}/{mux.TooFull})");

			var did = new Dictionary<object, bool>();
			foreach(var h in handlers)
			{
				foreach(var s in h.Socks)
				{
					if (ready.Contains(s))
					{
						h.Callback(s);
						if (!did.ContainsKey(s))
						{
							did.Add(s, true);
						}
					}
				}
			}

			foreach(var s in ready)
			{
				if (!did.ContainsKey(s))
				{
					throw new Exception($"socket {s} was not used by any handler");
				}
			}
		}

		private void OnAcceptTCP(Socket s, Mux mux)
		{
			var dstEndP = (IPEndPoint)s.RemoteEndPoint;
			Helpers.Debug1($"Accept TCP: x:x -> {dstEndP.Address}:{dstEndP.Port}");

			var chanId = mux.NextChannel();
			mux.Send(chanId, SSHuttleCommands.CMD_TCP_CONNECT, $"{(int)dstEndP.AddressFamily},{dstEndP.Address},{dstEndP.Port}");
			var outWrap = new MuxWrapper(mux, chanId);

			var px = new Proxy(new SocketWrapper(s), outWrap);
			_handlers.Add(px);

			ExpireConnections(DateTime.Now, mux);
		}

		private void ExpireConnections(DateTime time, Mux mux)
		{
			foreach (var c in _udpBySrc.Where((c) => { return c.ExpireTime < time; }).ToList())
			{
				Helpers.Debug3($"expiring UDP channel channel={c.Channel} peer={c.SrcIP}:{c.SrcPort}");
				mux.Send(c.Channel, SSHuttleCommands.CMD_UDP_CLOSE, "");
				mux.Channels.Remove(c.Channel);
				_udpBySrc.Remove(c);
			}
			Helpers.Debug3($"Remaining UDP channels: {_udpBySrc.Count}");
		}

		private void OnAcceptUDP(WinSSHuttlePacket packet, Mux mux)
		{
			Helpers.Debug1($"Accept UDP: {packet.SrcIP}:{packet.SrcPort} -> {packet.DstIP}:{packet.DstPort}");
			WinSSHuttleChannel channel = _udpBySrc.Where((c) => { return c.SrcIP.Equals(packet.SrcIP) && c.SrcPort.Equals(packet.SrcPort); }).FirstOrDefault();
			if (channel == default)
			{
				var chan = mux.NextChannel();
				mux.Channels[chan] = (cmd, data) => UdpDone(_outCap, data.ToArray(), packet.Ifx, packet.SubIfx, packet.SrcIP, packet.SrcPort);

				channel = new WinSSHuttleChannel() {
					SrcIP = packet.SrcIP,
					SrcPort = packet.SrcPort,
					Channel = chan,
					ExpireTime = DateTime.Now.AddSeconds(30)
				};
				_udpBySrc.Add(channel);

				mux.Send(channel.Channel, SSHuttleCommands.CMD_UDP_OPEN, $"{(int)packet.DstIP.AddressFamily}");
			}

			channel.ExpireTime = DateTime.Now.AddSeconds(30);
			var data = Encoding.UTF8.GetBytes($"{packet.DstIP},{packet.DstPort},").ToList();
			data.AddRange(packet.Payload);

			mux.Send(channel.Channel, SSHuttleCommands.CMD_UDP_DATA, data);

			ExpireConnections(DateTime.Now, mux);
		}

		private void UdpDone(PacketCapture capturer, byte[] data, uint interfaceId, uint subInterfaceId, IPAddress destIP, ushort destPort)
		{
			var commaCount = 0;
			var datalist = data.ToList();
			var headerIP = new List<byte>();
			var headerPort = new List<byte>();
			try
			{
				while (true)
				{
					if (commaCount < 2 && datalist[0] == Encoding.UTF8.GetBytes(",").ToList().First())
					{
						commaCount++;

						datalist.RemoveAt(0);
						continue;
					}

					if (commaCount == 0)
					{
						headerIP.Add(datalist[0]);
					}
					if (commaCount == 1)
					{
						headerPort.Add(datalist[0]);
					}
					if (commaCount == 2)
					{
						data = datalist.ToArray();
						break;
					}
					datalist.RemoveAt(0);
				}
			}
			catch(Exception e)
			{
				Helpers.LogError($"UDP Done - Header Parsing Exception: {e}");
			}

			var srcIP = IPAddress.Parse(Encoding.UTF8.GetString(headerIP.ToArray()));
			var srcPort = ushort.Parse(Encoding.UTF8.GetString(headerPort.ToArray()));

			Helpers.Debug2($"Doing send from {srcIP}:{srcPort} to {destIP}:{destPort}");

			capturer.RelayInboundUDP(interfaceId, subInterfaceId, srcIP, srcPort, destIP, destPort, data);
		}

		#region SSH Process Launching
		private async Task<Mux> StartSSH()
		{
			var x = await StartSSHSession();
			if (x.Item1 == null)
			{
				return null;
			}

			{ //Check for init string
				var fromSSHRelay = x.Item2;
				var expected = "SSHUTTLE0001";

				var y = fromSSHRelay.ReadByte();
				while (y != 0)
				{
					y = fromSSHRelay.ReadByte();
				}

				y = fromSSHRelay.ReadByte();
				while (y != 0)
				{
					y = fromSSHRelay.ReadByte();
				}

				var initStr = Encoding.ASCII.GetString(fromSSHRelay.ReadBytes(expected.Length));

				if (initStr != expected)
				{
					throw new Exception($"expecting server init string {expected}; got {initStr}");
				}

				Helpers.Log("Connected to server.");
			}

			var m = new Mux(x.Item2.BaseStream, x.Item1.BaseStream);

			_sshRelayDebug = x.Item3;
			return m;
		}

		private async Task<Tuple<StreamWriter, BinaryReader, StreamReader>> StartSSHSession()
		{
			Helpers.Debug1($"C     : Starting SSH Connection");
			var assembler = ResourceHelpers.ReadResourceFile("assembler-NoCompression.py");
			var pyScript = $@"
				import sys, os;
				verbosity={Helpers.OutputLevel};
				sys.stderr.write(\""\n bs: BootStrap\"" + \"" Starting\n\"");
				sys.stderr.flush();
				sys.stdin = os.fdopen(0, \""rb\"");
				exec(compile(sys.stdin.read({assembler.Length}), \""assembler.py\"", \""exec\""));
			";
			pyScript = Regex.Replace(pyScript.Trim(), @"\s+", " ");
			var pyCommand = $@"P=python3; $P -V 1>/dev/null || P=python; exec \""$P\"" -c '{pyScript}'; exit 97";
			pyCommand = $"\"/bin/sh -c {PythonShellUtils.Quote(pyCommand)}\"";
			pyCommand = Regex.Replace(pyCommand, @"\s+", " ").Trim();

			var commandLineOptions = "latency_control=True\nlatency_buffer_size=65536\nauto_hosts=False\nto_nameserver=None\nauto_nets=False\nttl=63\n";
			var plSSHuttle = ResourceHelpers.GetPayloadFile("sshuttle.py", "sshuttle");
			var plhelpers = ResourceHelpers.GetPayloadFile("helpers.py", "sshuttle.helpers");
			var plssnet = ResourceHelpers.GetPayloadFile("ssnet.py", "sshuttle.ssnet");
			var plhostwatch = ResourceHelpers.GetPayloadFile("hostwatch.py", "sshuttle.hostwatch");
			var plserver = ResourceHelpers.GetPayloadFile("server.py", "sshuttle.server");
			var plCMDLineOpts = $"sshuttle.cmdline_options\n{commandLineOptions.Length}\n{commandLineOptions}";
			var plEOP = "EOPayLoad\n";

			var cmd = _plinkExe;
			var cmdArgs = $"-no-sanitise-stderr -no-sanitise-stdout -ssh {_bastionHost}";
			if (!string.IsNullOrWhiteSpace(_password))
			{
				cmdArgs += $" -pw {_password}";
			}
			else if (_authKey.Exists)
			{
				cmdArgs += $" -i \"{_authKey.FullName}\"";
			}
			cmdArgs += $" {pyCommand}";

			_sshProc = LaunchProcess(cmd, cmdArgs);

			Helpers.Debug1($"C     : Process Started");
			var input = _sshProc.StandardInput;
			var output = new BinaryReader(_sshProc.StandardOutput.BaseStream);
			var err = _sshProc.StandardError;

			// check for host key not trusted prompt
			bool hostKeyNotTrustedFound = false;
			string outString = "";
			int @out;
			while ((@out = err.Read()) > -1)
			{
				if (outString.Length == 0 && (char)@out == '\n') continue;
				outString += (char)@out;
				if ((char)@out == '\n' && !outString.Contains("bs: BootStrap Starting"))
				{
					await Task.Delay(500);
					Console.WriteLine(outString);
					hostKeyNotTrustedFound = true;
					while ((@out = err.Read()) > -1)
					{
						Console.Write((char)@out);
						if ((char)@out == ')')
						{
							Console.Write(": ");
							break;
						}
					}
					break;
				}
				else if ((char)@out == '\n' && outString.Contains("bs: BootStrap Starting"))
				{
					break;
				}
			}

			// prompt or auto accept host key
			if (hostKeyNotTrustedFound)
			{
				bool accept = false;
				if (!_autoAcceptHostKey)
				{
					ConsoleKeyInfo k;
					do
					{
						k = Console.ReadKey(true);
					} while (k.KeyChar != 'n' && k.KeyChar != 'N' && k.KeyChar != 'y' && k.KeyChar != 'Y' && k.Key != ConsoleKey.Enter);

					if (k.KeyChar == 'y' || k.KeyChar == 'Y')
					{
						accept = true;
					}

					Console.WriteLine(k.KeyChar);
					input.WriteLine(k.KeyChar);
					input.Flush();
				}
				else
				{
					accept = true;
					input.WriteLine("y");
					input.Flush();
				}

				if (accept)
				{
					await Task.Delay(1000);
					_sshProc.Kill();
					_sshProc.WaitForExit();

					_sshProc = LaunchProcess(cmd, cmdArgs);
					input = _sshProc.StandardInput;
					output = new BinaryReader(_sshProc.StandardOutput.BaseStream);//, Encoding.UTF8);
					err = _sshProc.StandardError;
				}
				else
				{
					return new Tuple<StreamWriter, BinaryReader, StreamReader>(null, null, null);
				}
			}

			input.Write(assembler);
			input.Write(plSSHuttle + plCMDLineOpts + plhelpers + plssnet + plhostwatch + plserver + plEOP);
			input.Flush();

			return new Tuple<StreamWriter, BinaryReader, StreamReader>(input, output, err);
		}

		private Process LaunchProcess(FileInfo processExe, string arguments = null)
		{
			if (!File.Exists(processExe.FullName))
			{
				return null;
			}
			Helpers.Debug3($"C     : Launching: {processExe.Name}");
			ProcessStartInfo psi = new ProcessStartInfo(processExe.FullName);
			if (!string.IsNullOrWhiteSpace(arguments))
			{
				Helpers.Debug4($"C     :   With Args: {arguments}");
				psi.Arguments = arguments;
			}
			psi.RedirectStandardInput = true;
			psi.RedirectStandardOutput = true;
			psi.RedirectStandardError = true;
			psi.WindowStyle = ProcessWindowStyle.Maximized;
			psi.UseShellExecute = false;
			psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;


			Process e2 = new Process();
			e2.StartInfo = psi;
			e2.Start();
			e2.StandardInput.AutoFlush = true;
			return e2;
		}

		#endregion

		private Task MonitorSSHDebug()
		{
			return Task.Run(async () => {
				Helpers.Debug4($"C     : Starting SSH Debug Monitor");

				while (!_classTokenSrc.Token.IsCancellationRequested)
				{
					if (_sshProc != null && !_sshProc.HasExited && _sshRelayDebug != null)
					{
						try
						{
							var line = await _sshRelayDebug.ReadLineAsync();
							if (!string.IsNullOrWhiteSpace(line))
							{
								Helpers.Debug1(line);
							}
						}
						catch (Exception e)
						{
							Helpers.LogError($"Exception from SSH Debug Monitor: {e}");
							break;
						}
					}
				}
			});
		}
	}
}
