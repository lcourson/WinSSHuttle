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

namespace WinSSHuttle
{
	internal class SSHQueueItem
	{
		public byte[] Header { get; set; }
		public byte[] Data { get; set; }
	}

	internal class PLinkRunner
	{
		public bool IsSSHConnectionOk { get; set; } = true;

		private StreamWriter _toSSHRelay;
		private BinaryReader _fromSSHRelay;
		private StreamReader _sshRelayDebug;
		private Process _sshProc;
		private TProxy _tproxy;

		private readonly CancellationToken _mainAppToken;
		private readonly CancellationTokenSource _classTokenSrc = new CancellationTokenSource();

		private List<byte> _inboundBuffer = new List<byte>();
		private int _inboundWant = 0;
		private readonly PacketCapture _outCap;

		private ManualResetEvent _isServeReady = new ManualResetEvent(false);

		private List<ushort> _activeChannelIds = new List<ushort>();
		private List<WinSSHuttleChannel> _udpChannels = new List<WinSSHuttleChannel>();
		private List<WinSSHuttleChannel> _tcpChannels = new List<WinSSHuttleChannel>();
		private ushort _currentChannelId = 1;

		private Queue<SSHQueueItem> _sshSendingQueue = new Queue<SSHQueueItem>();

		private readonly string _bastionHost;
		private readonly FileInfo _authKey;
		private readonly string _password;
		private readonly FileInfo _plinkExe;
		private readonly bool _autoAcceptHostKey;
		private readonly short _tproxyPriority;

		#region Constructors
		public PLinkRunner(AppOptions config): this(config.Token, config.Output, config.PlinkExe, config.Network.ToList(), config.BastionHost, config.Password, config.PrivateKey, config.IncludeDNS, config.AcceptHostKey, config.Priority)
		{
		}

		public PLinkRunner(CancellationToken token, int output, FileInfo plinkExe, List<string> networks, string bastionHost, string password, FileInfo authKey, bool includeDNS, bool autoAcceptHostKey, short priority)
		{
			_autoAcceptHostKey = autoAcceptHostKey;
			_mainAppToken = token;
			_plinkExe = plinkExe;
			_bastionHost = bastionHost;
			_password = password;
			_authKey = authKey;
			_tproxyPriority = priority;

			if (!_authKey.Exists && string.IsNullOrWhiteSpace(password))
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
			_tproxy = new TProxy(_classTokenSrc.Token);
			_tproxy.OnNewConnection += OnNewTProxyConnection;
			var tprox = _tproxy.StartServer();

			while (!_tproxy.ServerReady)
			{
				Task.Delay(100).Wait();
			}

			_outCap.TProxyPort = _tproxy.ProxyPort;
			_outCap.TProxyAddress = _tproxy.ProxyAddress;

			var keepGoing = StartSSH().Result;

			if (keepGoing)
			{

				var sshSndHandler = MonitorSSHSendQueue();
				var sshRcvHandler = MonitorSSHReceiveQueue();
				var sshDebugHandler = MonitorSSHDebug();

				_isServeReady.WaitOne();
				ColorConsole.WriteInfo("Server Is Ready!!!");

				var oCap = _outCap.StartCapture(_tproxyPriority);
				var udpQ = MonitorUDPQueue(_outCap);

				ColorConsole.WriteInfo("Client Connected!!!");
				ColorConsole.WriteInfo("Hit CTRL+C to exit...\n");

				return Task.Run(() =>
				{
					while (!_mainAppToken.IsCancellationRequested)
					{
						if (_sshProc.HasExited && !_mainAppToken.IsCancellationRequested)
						{
							ColorConsole.WriteWarning("!!!!SSH PROCESS EXITED!!!");
							IsSSHConnectionOk = false;
							break;
						}

						try
						{
							Task.Delay(1000).Wait();
						}
						catch { }
					}

					// Disconnect all TCP Channels so applications are alerted.
					_tcpChannels.ForEach((s) => {
						s.Proxy.Disconnect();
					});

					// Cancel the token so tasks finish their processing
					_classTokenSrc.Cancel();

					var x = new Stopwatch();
					x.Start();
					Helpers.Debug1("Waiting on Packet Capture to stop");
					oCap.Wait();
					x.Stop();
					Helpers.Debug1($"  Packet Capture has Stopped... {x.ElapsedMilliseconds}");
					x.Reset();

					x.Start();
					Helpers.Debug1("Waiting on TProxy to stop");
					tprox.Wait();
					x.Stop();
					Helpers.Debug1($"  TProxy has Stopped... {x.ElapsedMilliseconds}");
					x.Reset();

					x.Start();
					Helpers.Debug1("Waiting on UDP Queue to stop");
					udpQ.Wait();
					x.Stop();
					Helpers.Debug1($"  UDP Queue has Stopped... {x.ElapsedMilliseconds}");
					x.Reset();

					x.Start();
					Helpers.Debug1("Waiting on SSH Send Handler to stop");
					sshSndHandler.Wait();
					x.Stop();
					Helpers.Debug1($"  SSH Send Handler has Stopped... {x.ElapsedMilliseconds}");
					x.Reset();

					if (!_sshProc.HasExited)
					{
						_toSSHRelay.Close();
						Helpers.Debug1("Closed InputStream");
						_sshProc.WaitForExit();
						Helpers.Debug1($"PLink Process Exited: {_sshProc.ExitCode}");
					}

					x.Start();
					Helpers.Debug1("Waiting on SSH Receive Handler to stop");
					sshRcvHandler.Wait();
					x.Stop();
					Helpers.Debug1($"  SSH Receive Handler has Stopped... {x.ElapsedMilliseconds}");
					x.Reset();

					x.Start();
					Helpers.Debug1("Waiting on SSH Debug Handler to stop");
					sshDebugHandler.Wait();
					x.Stop();
					Helpers.Debug1($"  SSH Debug Handler has Stopped... {x.ElapsedMilliseconds}");

					Helpers.Debug1($"PLink Process Exited");
				});
			}
			else
			{
				_classTokenSrc.Cancel();
				tprox.Wait();
				return null;
			}
		}

		#region TCP Processes
		private void OnNewTProxyConnection(Socket s)
		{
			var chan = NextChannel();
			var px = new Proxy(s, chan);
			px.OnDisconnect += Px_onDisconnect;
			var dstEndP = (IPEndPoint)s.RemoteEndPoint;
			Helpers.Debug1($"C     : Creating new channel [{chan}] for TCP: {dstEndP.Address}:{dstEndP.Port}");

			var channel = new WinSSHuttleChannel()
			{
				Proxy = px,
				Channel = chan,
				Callback = delegate (byte[] data) { px.SendData(data); },
			};
			_tcpChannels.Add(channel);

			SendPacket(chan, SSHuttleCommands.CMD_TCP_CONNECT, $"{(int)dstEndP.AddressFamily},{dstEndP.Address},{dstEndP.Port}");

			px.OnDataReceived += Px_onDataReceived;
			px.BeginReceive();
		}

		private void Px_onDisconnect(object sender, EventArgs e)
		{
			var c = _tcpChannels.Where((c) => c.Channel == (ushort)sender).FirstOrDefault();
			if (c != default)
			{
				SendPacket((ushort)sender, SSHuttleCommands.CMD_TCP_EOF, $"");
				//	_tcpChannels.Remove(c);
				//	_activeChannelIds.Remove(c.Channel);
			}
		}

		private void Px_onDataReceived(byte[] payload, ushort channel) {
			var origPayload = payload.ToList();
			Helpers.Debug4($"Sending payload to SSH: {payload.Length}");
			while (origPayload.Count > 2048)
			{
				var data = origPayload.Take(2048).ToArray();
				SendPacket(channel, SSHuttleCommands.CMD_TCP_DATA, data);
				origPayload.RemoveRange(0, 2048);
			}
			SendPacket(channel, SSHuttleCommands.CMD_TCP_DATA, origPayload.ToArray());
		}

		#endregion

		#region UDP Processes
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

			Helpers.Debug2($"C     : Sending UDP Data from {srcIP}:{srcPort} -> {destIP}:{destPort}");

			capturer.RelayInboundUDP(interfaceId, subInterfaceId, srcIP, srcPort, destIP, destPort, data);
		}

		private Task MonitorUDPQueue(PacketCapture capturer)
		{
			return Task.Run(() => {
				while (!_classTokenSrc.Token.IsCancellationRequested)
				{
					if (capturer.OutgoingUDPQueue.TryDequeue(out WinSSHuttlePacket packet))
					{
						WinSSHuttleChannel channel = _udpChannels.Where((c) => { return c.SourceIP.Equals(packet.SourceIP) && c.SourcePort.Equals(packet.SrcPort); }).FirstOrDefault();
						if (channel == default)
						{
							var chanId = NextChannel();

							Helpers.Debug1($"C     : Creating new channel [{chanId}] for UDP: {packet.SourceIP}:{packet.SrcPort} -> {packet.DestIP}:{packet.DstPort}");
							channel = new WinSSHuttleChannel()
							{
								SourceIP = packet.SourceIP,
								SourcePort = packet.SrcPort,
								Channel = chanId,
								ExpireTime = DateTime.Now.AddSeconds(30),
								Callback = delegate(byte[] data) { UdpDone(capturer, data, packet.Ifx, packet.SubIfx, packet.SourceIP, packet.SrcPort); }
							};
							_udpChannels.Add(channel);

							SendPacket(channel.Channel, SSHuttleCommands.CMD_UDP_OPEN, $"{(int)packet.DestIP.AddressFamily}");
						}

						channel.ExpireTime = DateTime.Now.AddSeconds(30);
						var data = Encoding.UTF8.GetBytes($"{packet.DestIP},{packet.DstPort},").ToList();
						data.AddRange(packet.Payload);

						SendPacket(channel.Channel, SSHuttleCommands.CMD_UDP_DATA, data.ToArray());
					}
					else
					{
						try
						{
							Task.Delay(500).Wait();
						}
						catch { }
					}
					ExpireConnections();
				}
			});
		}

		private void ExpireConnections()
		{
			var current = DateTime.Now;
			foreach (var c in _udpChannels.Where((c) => { return c.ExpireTime < current; }).ToList())
			{
				SendPacket(c.Channel, SSHuttleCommands.CMD_UDP_CLOSE, "");
				_udpChannels.Remove(c);
				_activeChannelIds.Remove(c.Channel);
			}
		}

		#endregion

		#region SSH Process Launching
		private async Task<bool> StartSSH()
		{
			var x = await StartSSHSession();
			if (x.Item1 == null)
			{
				return false;
			}
			_sshProc = x.Item1;
			_toSSHRelay = x.Item2;
			_fromSSHRelay = x.Item3;
			_sshRelayDebug = x.Item4;
			return true;
		}

		private async Task<Tuple<Process, StreamWriter, BinaryReader, StreamReader>> StartSSHSession()
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

			var sshProc = LaunchProcess(cmd, cmdArgs);
			Helpers.Debug1($"C     : Process Started");
			var input = sshProc.StandardInput;
			var output = new BinaryReader(sshProc.StandardOutput.BaseStream);
			var err = sshProc.StandardError;

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
					hostKeyNotTrustedFound = true;
					Console.WriteLine(outString);
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
					sshProc.Kill();
					sshProc.WaitForExit();

					sshProc = LaunchProcess(cmd, cmdArgs);
					input = sshProc.StandardInput;
					output = new BinaryReader(sshProc.StandardOutput.BaseStream);//, Encoding.UTF8);
					err = sshProc.StandardError;
				}
				else
				{
					return new Tuple<Process, StreamWriter, BinaryReader, StreamReader>(null, null, null, null);
				}
			}

			input.Write(assembler);
			input.Write(plSSHuttle + plCMDLineOpts + plhelpers + plssnet + plhostwatch + plserver + plEOP);
			input.Flush();

			return new Tuple<Process, StreamWriter, BinaryReader, StreamReader>(sshProc, input, output, err);
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

		#region Monitory SSH Queue Tasks
		private Task MonitorSSHSendQueue()
		{
			return Task.Run(() => {
				while (!_classTokenSrc.Token.IsCancellationRequested && IsSSHConnectionOk)
				{
					if (_sshSendingQueue.TryDequeue(out SSHQueueItem item))
					{
						_toSSHRelay.BaseStream.Write(item.Header, 0, item.Header.Length);
						_toSSHRelay.BaseStream.Write(item.Data, 0, item.Data.Length);
						Helpers.Debug2($"C     : mux wrote {item.Header.Length + item.Data.Length}");
						_toSSHRelay.Flush();
					}
					else
					{
						Task.Delay(100).Wait();
					}
				}
			});
		}

		private Task MonitorSSHReceiveQueue()
		{
			return Task.Run(() =>
			{
				var initString = "SSHUTTLE0001";
				var initStringFound = false;
				Helpers.Debug4($"C     : Starting SSH Receive Monitor");
				while (!_classTokenSrc.Token.IsCancellationRequested)
				{
					if (IsSSHConnectionOk)
					{
						try
						{
							var readByte = _fromSSHRelay.ReadByte();
							_inboundBuffer.Add(readByte);
						}
						catch (EndOfStreamException)
						{
							IsSSHConnectionOk = false;
							break;
						}
						catch (Exception e)
						{
							Helpers.LogError($"Exception from SSH Receive Monitor: {e}");
							break;
						}

						if (!_sshProc.HasExited)
						{
							try
							{
								if (!initStringFound && _inboundBuffer.Count() == initString.Length)
								{
									if (Encoding.UTF8.GetString(_inboundBuffer.Take(initString.Length).ToArray()) == initString)
									{
										initStringFound = true;
										_inboundBuffer = _inboundBuffer.Skip(initString.Length).ToList();
									}
									else
									{
										_inboundBuffer.RemoveAt(0);
									}
								}

								if (initStringFound)
								{
									if (_inboundBuffer.Count() >= 8)
									{
										var res = ParseHeader(_inboundBuffer.Take(8).ToArray());

										ushort channel = res.Item1;
										SSHuttleCommands cmd = (SSHuttleCommands)res.Item2;
										int datalen = res.Item3;
										if (cmd != 0)
										{
											_inboundWant = datalen + 8;

											if (_inboundWant > 0 && _inboundBuffer.Count >= _inboundWant)
											{
												Helpers.Debug1($"C <- s:{channel} channel={channel} cmd={cmd} len={datalen}");
												var data = _inboundBuffer.Skip(8).Take(_inboundWant).ToArray();
												Helpers.Debug4($"C <- s:{channel} {string.Join(' ', data.Select<byte, string>((b) => { return b.ToString("X2"); }).ToArray())}");
												_inboundBuffer.RemoveRange(0, _inboundWant);

												_inboundWant = 0;
												ProcessPacket(channel, cmd, data);
											}
											else if (_inboundBuffer.Count % 256 == 0)
											{
												Helpers.Debug4($"Read {_inboundBuffer.Count} bytes so far... waiting for {_inboundWant}");
											}
										}
									}
								}
							}
							catch (Exception e)
							{
								Helpers.Debug4($"Monitor SSH Receive Queue Failed: {e}");
							}
						}
					}
				}
			});
		}

		private Task MonitorSSHDebug()
		{
			return Task.Run(async () => {
				Helpers.Debug4($"C     : Starting SSH Debug Monitor");

				while (!_classTokenSrc.Token.IsCancellationRequested)
				{
					if (!_sshProc.HasExited && IsSSHConnectionOk)
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

		#endregion

		#region Packet Parsing & Processing
		private Tuple<ushort, ushort, ushort> ParseHeader(byte[] header)
		{
			char s1;
			char s2;
			ushort channel = 0;
			ushort cmd = 0;
			ushort datalen;

			var outputArr = StructConverter.Unpack("!ccHHH", header);
			s1 = (char)outputArr[0];
			s2 = (char)outputArr[1];
			channel = (ushort)outputArr[2];
			cmd = (ushort)outputArr[3];
			datalen = (ushort)outputArr[4];

			//if (s1 != 'S') { Console.WriteLine($"First byte is not 'S'"); }
			//if (s2 != 'S') { Console.WriteLine($"Second byte is not 'S'"); }
			return new Tuple<ushort, ushort, ushort>(channel, cmd, datalen);
		}

		private void ProcessPacket(ushort channel, SSHuttleCommands cmd, byte[] data)
		{
			WinSSHuttleChannel c = default;
			switch (cmd)
			{
				case SSHuttleCommands.CMD_EXIT:
					break;
				case SSHuttleCommands.CMD_PING:
					SendPacket((ushort)0, SSHuttleCommands.CMD_PONG, data);
					break;
				case SSHuttleCommands.CMD_PONG:
					Helpers.Debug2($"C <- s:{channel} Got Ping Response");
					break;
				case SSHuttleCommands.CMD_TCP_CONNECT:
					Helpers.Debug2($"C <- s:{channel} Got {cmd}");
					break;
				case SSHuttleCommands.CMD_TCP_STOP_SENDING:
					Helpers.Debug2($"C <- s:{channel} Got {cmd}");
					break;
				case SSHuttleCommands.CMD_TCP_EOF:
					Helpers.Debug2($"C <- s:{channel} Got {cmd}");
					c = _tcpChannels.Where((c) => { return c.Channel == channel; }).FirstOrDefault();
					if (c != default)
					{
						c.Proxy.Disconnect(false);
						_tcpChannels.Remove(c);
						_activeChannelIds.Remove(c.Channel);
					}
					else
					{
						Helpers.LogError($"No TCP Channel found with Id: {channel}");
					}
					break;
				case SSHuttleCommands.CMD_TCP_DATA:
					Helpers.Debug2($"C <- s:{channel} Got {cmd}");
					c = _tcpChannels.Where((c) => { return c.Channel == channel; }).FirstOrDefault();
					if (c != default)
					{
						if (c.Callback != null)
						{
							c.Callback(data);
						}
						else
						{
							Helpers.LogError($"No TCP Callback found on Channel: {channel}");
						}
					}
					else
					{
						Helpers.LogError($"No TCP Channel found with Id: {channel}");
					}
					break;
				case SSHuttleCommands.CMD_ROUTES:
					Helpers.Debug2($"C <- s:{channel} Got Routes");
					_isServeReady.Set();
					break;
				case SSHuttleCommands.CMD_HOST_REQ:
					break;
				case SSHuttleCommands.CMD_HOST_LIST:
					break;
				case SSHuttleCommands.CMD_DNS_REQ:
					break;
				case SSHuttleCommands.CMD_DNS_RESPONSE:
					break;
				case SSHuttleCommands.CMD_UDP_OPEN:
					break;
				case SSHuttleCommands.CMD_UDP_DATA:
					Helpers.Debug2($"C <- s:{channel} Got UDP Data Response");
					c = _udpChannels.Where((c) => { return c.Channel == channel; }).FirstOrDefault();
					if (c != default)
					{
						if (c.Callback != null)
						{
							c.Callback(data);
						}
						else
						{
							Helpers.LogError($"No UDP Callback found on Channel: {channel}");
						}
					}
					else
					{
						Helpers.LogError($"No UDP Channel found with Id: {channel}");
					}
					break;
				case SSHuttleCommands.CMD_UDP_CLOSE:
					break;
				case SSHuttleCommands.NO_OP:
				default:
					break;
			}
		}

		#endregion

		private ushort NextChannel()
		{
			ushort chanId = 0;
			for (var i = _currentChannelId; i < 1024 + _currentChannelId; i++)
			{
				if (_currentChannelId > 65535) { _currentChannelId = 1; }

				if (!_activeChannelIds.Contains(_currentChannelId))
				{
					chanId = _currentChannelId;
					_activeChannelIds.Add(chanId);
					break;
				}

				_currentChannelId++;
			}
			if (chanId == 0)
			{
				throw new Exception("Too Many Channels opened");
			}
			return chanId;
		}

		private void SendPacket(ushort channel, SSHuttleCommands cmd, string data) => SendPacket(channel, cmd, Encoding.UTF8.GetBytes(data));

		private void SendPacket(ushort channel, SSHuttleCommands cmd, byte[] data)
		{
			var header = StructConverter.Pack("!ccHHH", new List<object>() { 'S', 'S', channel, (ushort)cmd, (ushort)data.Length }).ToArray();
			Helpers.Debug1($"C -> s:{channel} channel={channel} cmd={cmd} len={data.Length}");
			Helpers.Debug4($"C -> s:{channel} Header: {string.Join(' ', header.Select<byte, string>((b) => { return b.ToString("X2"); }).ToArray())}");
			Helpers.Debug4($"C -> s:{channel} Data  : {string.Join(' ', data.Select<byte, string>((b) => { return b.ToString("X2"); }).ToArray())}");

			_sshSendingQueue.Enqueue(new SSHQueueItem()
			{
				Header = header,
				Data = data
			});

		}
	}
}
