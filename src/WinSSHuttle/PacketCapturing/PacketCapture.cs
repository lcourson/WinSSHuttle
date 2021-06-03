using PacketDotNet;
using PacketDotNet.Utils;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using WinDivertSharp;

namespace WinSSHuttle.PacketCapturing
{
	public class PacketCapture
	{
		public bool LoggingEnabled { get; set; }

		string _filter;
		CancellationToken _token;
		public bool IsCaptureActive { get; set; } = false;
		public IPAddress TProxyAddress { get; set; }
		public int TProxyPort { get; set; }

		private IntPtr _ptr = default;
		private bool _closeCalled = false;

		public string MessageHeader { get; set; } = "";

		public Action<WinSSHuttlePacket> OnAcceptUDP;

		private readonly Dictionary<uint, WinTcpAddrPortMapping> _v4TcpMapping = new Dictionary<uint, WinTcpAddrPortMapping>();
		private ManualResetEvent _captureClosed = new ManualResetEvent(false);
		public Queue<WinSSHuttlePacket> OutgoingUDPQueue = new Queue<WinSSHuttlePacket>();
		public Queue<WinSSHuttlePacket> OutgoingTCPQueue = new Queue<WinSSHuttlePacket>();

		public PacketCapture(string filter, CancellationToken token)
		{
			_filter = filter;
			_token = token;
		}

		public Task StartCapture(short priority)
		{
			return Task.Run(() =>
			{
				new Thread(new ParameterizedThreadStart(CaptureTraffic)).Start(priority);
				IsCaptureActive = true;
				_captureClosed.WaitOne();
			});
		}

		private unsafe void StopCapture()
		{
			if (_token.IsCancellationRequested)
			{
				IsCaptureActive = false;
				if (!_closeCalled)
				{
					_closeCalled = true;
					_ = WinDivert.WinDivertClose(_ptr);
				}
				_captureClosed.Set();
			}
		}

		public unsafe void RelayInboundUDP(uint interfaceId, uint subInterfaceId, IPAddress srcIp, ushort srcPort, IPAddress destIP, ushort destPort, byte[] data)
		{
			var udp = new UdpPacket(srcPort, destPort)
			{
				PayloadData = data
			};

			var ip4 = new IPv4Packet(srcIp, destIP)
			{
				PayloadPacket = udp
			};

			WinDivertAddress addr = new WinDivertAddress()
			{
				Direction = WinDivertDirection.Inbound,
				PseudoIPChecksum = true,
				PseudoUDPChecksum = true,
				IfIdx = interfaceId,
				SubIfIdx = subInterfaceId
			};
			WinDivertBuffer packet = new WinDivertBuffer(ip4.Bytes);

			if (LoggingEnabled)
			{
				if (Helpers.OutputLevel > 3)
				{
					Helpers.Debug4($"{MessageHeader}{ip4.ToString(StringOutputType.Verbose)}");
				}
				else
				{
					Helpers.Debug3($"{MessageHeader}{ip4}");
				}
			}

			if (!WinDivert.WinDivertSend(_ptr, packet, (uint)ip4.TotalPacketLength, ref addr))
			{
				if (LoggingEnabled)
				{
					Helpers.LogError($"{MessageHeader}Relaying Inbound UDP Response Err: {Marshal.GetLastWin32Error()}");
				}
			}
		}

		private unsafe void CaptureTraffic(object priority)
		{
			using (_token.Register(() => StopCapture()))
			{
				try
				{
					if (LoggingEnabled)
					{
						Helpers.Debug1($"{MessageHeader}Starting Packet Capture -> {_filter}");
					}
					var packet = new WinDivertBuffer();
					var address = new WinDivertAddress();
					uint length = 0;
					_ptr = WinDivert.WinDivertOpen(_filter, WinDivertLayer.Network, (short)priority, WinDivertOpenFlags.None);
					while (IsCaptureActive && _ptr != default)
					{
						if (LoggingEnabled)
						{
							Helpers.Debug4($"C     : {MessageHeader}Looking for Packet...");
						}
						if (!WinDivert.WinDivertRecv(_ptr, packet, ref address, ref length))
						{
							var err = Marshal.GetLastWin32Error();
							if (err == 995 || err == 6)
							{
								break;
							}
						}

						if (!IsCaptureActive)
						{
							break;
						}
						var packetBytes = new byte[length];
						for (var i = 0; i < length; i++)
						{
							packetBytes[i] = packet[i];
						}
						var ip = new IPv4Packet(new ByteArraySegment(packetBytes));

						if (Helpers.OutputLevel > 3)
						{
							if (LoggingEnabled)
							{
								Helpers.Debug4($"C     : {MessageHeader}IPv4 Packet: {ip.ToString(StringOutputType.Verbose)}");
							}
						}
						else
						{
							if (LoggingEnabled)
							{
								Helpers.Debug3($"C     : {MessageHeader}IPv4 Packet: {ip}");
							}
						}

						if (ip.PayloadPacket is TcpPacket)
						{
							TcpPacket tPacket = ip.PayloadPacket as TcpPacket;

							//check to see if this packet is from the TProxy
							//if (address.Direction == WinDivertDirection.Outbound && ip.SourceAddress.Equals(TProxyAddress) && tPacket.SourcePort == TProxyPort) //Look into changing to this line
							if (address.Direction == WinDivertDirection.Outbound && tPacket.SourcePort == TProxyPort)
							{
								address = RelayTcpFromTProxy(address, ip, tPacket);
							}
							else
							{
								if (LoggingEnabled)
								{
									Helpers.Debug2($"C     : Accept TCP: {ip.SourceAddress}:{tPacket.SourcePort} -> {ip.DestinationAddress}:{tPacket.DestinationPort}");
								}
								RelayTCPToTProxy(ip, address.IfIdx, address.SubIfIdx);
							}
						}

						if (ip.PayloadPacket is UdpPacket)
						{
							UdpPacket uPacket = ip.PayloadPacket as UdpPacket;

							if (LoggingEnabled)
							{
								Helpers.Debug2($"C     : Accept UDP: {ip.SourceAddress}:{uPacket.SourcePort} -> {ip.DestinationAddress}:{uPacket.DestinationPort}");
							}

							OnAcceptUDP?.Invoke(new WinSSHuttlePacket() {
								SrcIP = ip.SourceAddress,
								SrcPort = uPacket.SourcePort,
								DstIP = ip.DestinationAddress,
								DstPort = uPacket.DestinationPort,
								Payload = uPacket.PayloadData,
								Ifx = address.IfIdx,
								SubIfx = address.SubIfIdx
							});

							OutgoingUDPQueue.Enqueue(new WinSSHuttlePacket()
							{
								SrcIP = ip.SourceAddress,
								SrcPort = uPacket.SourcePort,
								DstIP = ip.DestinationAddress,
								DstPort = uPacket.DestinationPort,
								Payload = uPacket.PayloadData,
								Ifx = address.IfIdx,
								SubIfx = address.SubIfIdx
							});
						}
					}
				}
				catch (Exception e)
				{
					if (LoggingEnabled)
					{
						Helpers.LogError($"{MessageHeader}Capture Packet Exception: {e}");
					}
				}
				finally
				{
					if (_ptr != default)
					{
						if (LoggingEnabled)
						{
							Helpers.Debug2($"{MessageHeader}Closing packet capture connection");
						}
						if (!_closeCalled)
						{
							_closeCalled = true;
							_ = WinDivert.WinDivertClose(_ptr);
						}
					}
				}
			}
		}

		private unsafe WinDivertAddress RelayTcpFromTProxy(WinDivertAddress address, IPv4Packet ip, TcpPacket tPacket)
		{
			if (LoggingEnabled)
			{
				Helpers.Debug4($"{MessageHeader}Looks like a response from TProxy");
			}

			WinTcpAddrPortMapping mapping = default;

			if (_v4TcpMapping.ContainsKey(tPacket.AcknowledgmentNumber))
			{
				mapping = _v4TcpMapping[tPacket.AcknowledgmentNumber];
				_v4TcpMapping[tPacket.AcknowledgmentNumber].LastUsed = DateTime.Now;
				if (LoggingEnabled)
				{
					Helpers.Debug4($"FromTProxy - Found mapping for id: {tPacket.AcknowledgmentNumber}");
				}
			}

			var toRemove = _v4TcpMapping.Where((m) => m.Value.LastUsed < DateTime.Now.AddSeconds(-30)).ToArray();
			foreach(var r in toRemove)
			{
				if (LoggingEnabled)
				{
					Helpers.Debug4($"FromTProxy - Removing mapping for id: {r.Key}");
				}
				_v4TcpMapping.Remove(r.Key);
			}

			if (mapping != default)
			{
				if (LoggingEnabled)
				{
					Helpers.Debug4($"{MessageHeader}Found TcpResponse Packet Mapping");
				}
				ip.SourceAddress = ip.DestinationAddress;
				tPacket.SourcePort = tPacket.DestinationPort;
				ip.DestinationAddress = mapping.OriginalSourceIP;
				tPacket.DestinationPort = mapping.OriginalSourcePort;

				//Helpers.LogDevDebug($"Payload: {Encoding.UTF8.GetString(tPacket.PayloadData)}");
				if (!tPacket.Acknowledgment)
				{
					if (!_v4TcpMapping.ContainsKey(tPacket.SequenceNumber + (uint)tPacket.PayloadData.Length + 1))
					{
						if (LoggingEnabled)
						{
							Helpers.Debug4($"FromTProxy - Adding mapping for id: {tPacket.SequenceNumber + (uint)tPacket.PayloadData.Length + 1}");
						}
						_v4TcpMapping[tPacket.SequenceNumber + (uint)tPacket.PayloadData.Length + 1] = mapping;
					}
				}
				address.Direction = WinDivertDirection.Inbound;
				WinDivertBuffer tcpResponsePacket = new WinDivertBuffer(ip.Bytes);

				var sumsCalculated = WinDivert.WinDivertHelperCalcChecksums(tcpResponsePacket, tcpResponsePacket.Length, ref address, WinDivertChecksumHelperParam.All);
				if (sumsCalculated <= 0)
				{
					if (LoggingEnabled)
					{
						Helpers.LogError("Modified packet reported that no checksums were calculated");
					}
				}

				//if (Helpers.OutputLevel > 3)
				//{
				//	Helpers.Debug4($"C     : {MessageHeader}RELAY From TPROXY: {ip.ToString(StringOutputType.Verbose)}");
				//}
				//else
				//{
				if (LoggingEnabled)
				{
					Helpers.Debug4($"C     : {MessageHeader}RELAY From TPROXY: {ip}");
				}
				//}

				if (LoggingEnabled)
				{
					Helpers.Debug2($"C     : {MessageHeader}Redirecting TCP Response: {ip.SourceAddress}:{(ip.PayloadPacket as TcpPacket).SourcePort} -> {ip.DestinationAddress}:{(ip.PayloadPacket as TcpPacket).DestinationPort}");
				}
				if (!WinDivert.WinDivertSendEx(_ptr, tcpResponsePacket, tcpResponsePacket.Length, 0, ref address))
				{
					if (LoggingEnabled)
					{
						Helpers.LogError($"{MessageHeader}Relaying Inbound TCP Response Err: {Marshal.GetLastWin32Error()}");
					}
				}
			}
			else
			{
				if (LoggingEnabled)
				{
					Helpers.LogError($"Unable to find a TCP mapping for {tPacket.AcknowledgmentNumber}");
				}
			}


			//Why are we returning address here?
			return address;
		}

		private void RelayTCPToTProxy(IPv4Packet ip, uint ifId, uint subIfId)
		{
			var tPacket = (ip.PayloadPacket as TcpPacket);
			var mapping = new WinTcpAddrPortMapping()
			{
				OriginalSourceIP = ip.SourceAddress,
				OriginalSourcePort = tPacket.SourcePort,
				OriginalDestinationIP = ip.DestinationAddress,
				OriginalDestinationPort = tPacket.DestinationPort
			};

			//Adding one to Seq Number since Ack will be incremented
			if (tPacket.PayloadData.Length > 0)
			{
				if (!_v4TcpMapping.ContainsKey(tPacket.SequenceNumber + (uint)tPacket.PayloadData.Length))
				{
					if (LoggingEnabled)
					{
						Helpers.Debug4($"C     : {MessageHeader}ToTProxy - Adding mapping for id: {tPacket.SequenceNumber + (uint)tPacket.PayloadData.Length}");
					}
					_v4TcpMapping[tPacket.SequenceNumber + (uint)tPacket.PayloadData.Length] = mapping;
				}
			}
			else
			{
				if (!_v4TcpMapping.ContainsKey(tPacket.SequenceNumber + 1))
				{
					if (LoggingEnabled)
					{
						Helpers.Debug4($"C     : {MessageHeader}ToTProxy - Adding mapping for id: {tPacket.SequenceNumber + 1}");
					}
					_v4TcpMapping[tPacket.SequenceNumber + 1] = mapping;
				}
			}

			tPacket.DestinationPort = (ushort)TProxyPort;
			tPacket.SourcePort = mapping.OriginalDestinationPort;

			ip.DestinationAddress = TProxyAddress;
			ip.SourceAddress = mapping.OriginalDestinationIP;

			WinDivertAddress addr = new WinDivertAddress()
			{
				Direction = WinDivertDirection.Inbound,
				IfIdx = ifId,
				SubIfIdx = subIfId
			};

			WinDivertBuffer packet = new WinDivertBuffer(ip.Bytes);

			var sumsCalculated = WinDivert.WinDivertHelperCalcChecksums(packet, packet.Length, ref addr, WinDivertChecksumHelperParam.All);
			if (sumsCalculated <= 0)
			{
				ColorConsole.WriteWarning("Modified packet reported that no checksums were calculated");
			}

			//if (Helpers.OutputLevel > 3)
			//{
			//	Helpers.Debug4($"C     : {MessageHeader}RELAY To TPROXY: {ip.ToString(StringOutputType.Verbose)}");
			//}
			//else
			//{
			if (LoggingEnabled)
			{
				Helpers.Debug3($"C     : {MessageHeader}RELAY To TPROXY: {ip}");
			}
			//}

			if (LoggingEnabled)
			{
				Helpers.Debug2($"C     : Redirecting TCP: {ip.SourceAddress}:{(ip.PayloadPacket as TcpPacket).SourcePort} -> {ip.DestinationAddress}:{(ip.PayloadPacket as TcpPacket).DestinationPort}");
			}
			if (!WinDivert.WinDivertSendEx(_ptr, packet, packet.Length, 0, ref addr))
			{
				if (LoggingEnabled)
				{
					Helpers.LogError($"{MessageHeader}Relaying Outbound TCP Response Err: {Marshal.GetLastWin32Error()}");
				}
			}
		}
	}
}
