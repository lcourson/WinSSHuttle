using System;
using System.Threading;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Process = System.Diagnostics.Process;
using System.CommandLine.Parsing;
using System.Security.Principal;
using System.Windows.Forms;
using System.Drawing;
using System.Threading.Tasks;

namespace WinSSHuttle
{
	internal class Program
	{
		private static readonly CancellationTokenSource _mainTokenSource = new CancellationTokenSource();
		private static bool _cancelCalled = false;
		private static ConsoleColor _originalForeground;
		private static ConsoleColor _originalBackground;

		private static readonly NotifyIcon _notifyIcon = new NotifyIcon();
		private static bool _visible = true;
		private static System.Timers.Timer _wTimer = new System.Timers.Timer(30);
		private static bool _minToTray = false;

		private static bool _useSysTray = false;

		private static void StopApp()
		{
			_mainTokenSource.Cancel();
			Application.Exit();
			_cancelCalled = true;
		}

		public static int Main(string[] args)
		{
			//Backup original Console colors
			_originalBackground = Console.BackgroundColor;
			_originalForeground = Console.ForegroundColor;

			if (!WindowsIdentity.GetCurrent().Owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
			{
				ColorConsole.WriteError("Application must be launched with administrative privileges... Exiting");
				return -1;
			}

			Task.Run(() => {
				while (!_mainTokenSource.IsCancellationRequested)
				{
					if (Console.ReadKey().Key == ConsoleKey.Enter)
					{
						ColorConsole.WriteLine("-----------------------------------------------", ConsoleColor.Green);
						ColorConsole.WriteLine("-----------------------------------------------", ConsoleColor.Green);
						ColorConsole.WriteLine("-----------------------------------------------", ConsoleColor.Green);
					}
				}
			});

			Console.CancelKeyPress += (sender, eventArgs) => {
				if (!_cancelCalled)
				{
					eventArgs.Cancel = true;
				}
				ColorConsole.WriteInfo("Found Exit Key... Exiting");
				StopApp();
			};

			CheckParentProcess();

			if (_useSysTray)
			{
				NativeWrapper.DisableClose();

				_notifyIcon.DoubleClick += (s, e) => {
					if (e is MouseEventArgs)
					{
						var mea = e as MouseEventArgs;
						if (mea.Button == MouseButtons.Left)
						{
							_visible = true;
							NativeWrapper.SetConsoleWindowVisibility(_visible);
						}
					}
				};

				Console.Title = Application.ProductName;
				_notifyIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
				_notifyIcon.Text = Application.ProductName;

				var contextMenu = new ContextMenuStrip();
				var mtt = new ToolStripMenuItem("Minimize to system tray?", null, (s, e) => {
					_minToTray = !_minToTray;
					((ToolStripMenuItem)s).Checked = _minToTray;
				});
				mtt.Checked = _minToTray;
				contextMenu.Items.Add(mtt);
				contextMenu.Items.Add("-");
				contextMenu.Items.Add("Exit", null, (s, e) => {
					ColorConsole.WriteInfo("Found Exit by Tray Icon");
					StopApp();
				});
				_notifyIcon.ContextMenuStrip = contextMenu;
			}

			RootCommand rootCommand = CreateCommandOptions();

			rootCommand.Handler = CommandHandler.Create((AppOptions config) => {
				DirectoryInfo baseDir = new DirectoryInfo(AppContext.BaseDirectory);

				if (string.IsNullOrWhiteSpace(config.Password) && config.PrivateKey == null)
				{
					ColorConsole.WriteError("SSH Authorization Is Not Provided.  Please use --help for usage instructions.");
					return;
				}

				if (config.PlinkExe == null)
				{
					var includedPLinkExe = new FileInfo(Path.Combine(baseDir.FullName, "plink.exe"));

					if (includedPLinkExe.Exists)
					{
						config.PlinkExe = includedPLinkExe;
					}
					else
					{
						ColorConsole.WriteError("ERROR!! Unable to find PLink.exe.  Please use the following option: '--plink-exe <Path to PLink application exe>'");
						return;
					}
				}
				Helpers.OutputLevel = config.Output;
				config.Priority = GetProcessCount();
				config.Token = _mainTokenSource.Token;

				if (config.DryRun)
				{
					ColorConsole.WriteInfo(GetParsedInput(config));
				}
				else
				{
					Helpers.Debug4(GetParsedInput(config));

					if (_useSysTray)
					{
						_notifyIcon.Visible = true;

						// Detect When The Console Window is Minimized and Hide it
						_wTimer.Elapsed += WTimer_Elapsed;
						_wTimer.AutoReset = true;
						_wTimer.Enabled = true;
						_wTimer.Start();

						if (!string.IsNullOrWhiteSpace(config.Name))
						{
							Console.Title += $" - {config.Name}";
							_notifyIcon.Text += $" - {config.Name}";
						}
					}

					var runner = new WinSSHuttleClient(config);
					var start = runner.Start();

					if (_useSysTray)
					{

						Application.Run();

						_wTimer.Stop();
						_wTimer.Dispose();

						// Make window visible
						_visible = true;
						NativeWrapper.SetConsoleWindowVisibility(_visible);
					}

					start.Wait();

					if (_useSysTray)
					{
						_notifyIcon.Visible = false;
					}

					ColorConsole.WriteInfo("Main Thread Exiting");
				}

				//Reset original Console colors
				Console.ForegroundColor = _originalForeground;
				Console.BackgroundColor = _originalBackground;
			});

			// Parse the incoming args and invoke the handler
			return rootCommand.InvokeAsync(args).Result;
		}

		private static void CheckParentProcess()
		{
			var parent = NativeWrapper.GetParentProcess();
			//Console.WriteLine($"Parent Process: {(parent != null ? parent.ProcessName : "NONE")}");
			if (parent == null || parent?.ProcessName == "cmd" || parent?.ProcessName == "powershell" || parent?.ProcessName == "pwsh")
			{
				_useSysTray = true;
			}
		}

		private static void WTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
		{
			if (NativeWrapper.GetWindowState() == NativeWrapper.ShowWindowCommands.ShowMinimized)
			{
				if (_minToTray)
				{
					_visible = false;
					NativeWrapper.SetConsoleWindowVisibility(_visible);
				}
			}
		}

		private static string GetParsedInput(AppOptions config)
		{
			return @$"
-------------------- Parsed Input --------------------
Priority      : {config.Priority}
BastionHost   : {config.BastionHost}
PrivateKey    : {config.PrivateKey}
Password      : {config.Password}
AcceptHostKey : {config.AcceptHostKey}
IncludeDNS    : {config.IncludeDNS}
Network(s)    : {string.Join("\n              : ", config.Network.ToArray())}
Output        : {config.Output}
PlinkExe      : {config.PlinkExe}
DryRun        : {config.DryRun}
-------------------- /Parsed Input -------------------";
		}

		private static short GetProcessCount()
		{
			return (short)(Process.GetProcessesByName(AppDomain.CurrentDomain.FriendlyName).Count() - 1);
		}

		private static string ValidateHost(ArgumentResult res)
		{
			var user = "";
			var server = "";
			var port = "";
			var intPort = 0;

			var host = res.Tokens.First()?.Value;
			if (host.Where(c => c == '@').Count() == 0)
			{
				res.ErrorMessage = $"A Username must be specified. (((<username>@)))<host>[:<port>]";
				return null;
			}

			if (host.Where(c => c == '@').Count() > 1)
			{
				res.ErrorMessage = $"There can be only one Username specified. (((<username>@)))<host>[:<port>]";
				return null;
			}

			if (host.Where(c => c == ':').Count() > 1)
			{
				res.ErrorMessage = $"There can be either zero or one Port specified. <username>@<host>[(((:<port>)))]";
			}

			if (host.Contains('@'))
			{
				user = host.Split('@')[0];
				var rest = host.Split('@')[1];

				if (string.IsNullOrWhiteSpace(user))
				{
					res.ErrorMessage = $"Username not specified. (((<username>)))@<host>[:<port>]";
				}

				if (rest.Contains(':'))
				{
					port = rest.Split(':')[1];
					server = rest.Split(':')[0];

					if (string.IsNullOrWhiteSpace(host))
					{
						res.ErrorMessage = $"Host not specified. <username>@(((<host>)))[:<port>]";
					}

					if (port.Length > 0 && !int.TryParse(port, out intPort))
					{
						res.ErrorMessage = $"Port must be a number. <username>@<host>[:(((<port>)))]";
					}
				}
				else
				{
					server = rest;
				}

				if (!IPAddress.TryParse(server, out IPAddress _))
				{
					var x = Dns.GetHostEntryAsync(server);
					if (!x.Wait(2000))
					{
						res.ErrorMessage = $"Unable to resolve Bastion Host: {server}";
					}
				}
			}

			return $"{user}@{server}{(intPort > 0 ? $":{intPort}" : "")}";
		}

		private static string[] ValidateNetworkCidrs(ArgumentResult res)
		{
			List<IPNetwork> cidrs = new List<IPNetwork>();
			foreach (var t in res.Tokens)
			{
				var network = t.Value;
				if (network.Split('.').Length != 4)
				{
					ColorConsole.WriteWarning($"WARNING: {network} is not a valid CIDR... Ignoring");
					continue;
				}

				try
				{
					if (network.Contains('\\'))
					{
						network = network.Replace('\\', '/');
					}

					var a = network.Split(new char[] { ' ', '/' });
					if (a.Length == 1)
					{
						network = $"{t.Value}/32";
					}

					var x = IPNetwork.Parse(network);
					if (cidrs.Count == 0)
					{
						cidrs.Add(x);
					}
					else
					{
						var overlapFound = false;
						foreach (var c in cidrs)
						{
							if (c.Overlap(x))
							{
								ColorConsole.WriteWarning($"WARNING: Found overlapping CIDRs: '{x}' overlaps '{c}'... Ignoring '{x}'");
								overlapFound = true;
								break;
							}
						}

						if (!overlapFound)
						{
							cidrs.Add(x);
						}
					}
				}
				catch (Exception e)
				{
					ColorConsole.WriteError($"Unable to parse network: {e}");
					ColorConsole.WriteError($"Unable to parse network: {t.Value}");
				}
			}

			if (cidrs.Count == 0)
			{
				res.ErrorMessage = "No valid networks found";
				return null;
			}

			return cidrs.Select(c => c.ToString()).ToArray();
		}

		private static RootCommand CreateCommandOptions()
		{
			var rootCommand = new RootCommand("SSH Tunnel Utility for Windows")
			{
				new Argument<string>("bastionHost", description: "Username and Server (FQDN or IP) for Bastion Host in the format: <username>@<server>[:<port>]", parse: result => ValidateHost(result)),
				new Option<string[]>(new string[] { "--network", "-n" }, description: "Network CIDR to forward through SSH Tunnel (Can be specified multiple times)", parseArgument: result => ValidateNetworkCidrs(result)),// { IsRequired = true },
				new Option<FileInfo>(new string[] { "--private-key", "-k" }, "Private key file for user authentication (.ppk)").ExistingOnly(),
				new Option<string>(new string[] { "--password", "-pw" }, "Password for user authentication"),
				//new Option<FileInfo>(new string[] { "--plink-exe", "-p" }, "PLink executable").ExistingOnly(),
				new Option<bool>(new string[] { "--include-dns", "-dns" }, "Forward DNS queries through SSH Tunnel"),
				new Option<bool>(new string[] { "--accept-host-key", "-y" }, "Networks to forward through SSH Tunnel"),
				new Option<int>(new string[] { "--output", "-v" }, "Level of output information (Default = 0)").FromAmong(new string[]{"0", "1", "2", "3", "4" }),
				new Option<bool>(new string[] { "--dry-run", "-dry" }, "Dont run... just show parsed inputs"),
				new Option<string>("--name", "Name for system tray icon")
			};

			rootCommand.AddValidator(res => {
				if (res.Children.Contains("bastionHost"))
				{
					if (res.Children.Contains("--private-key") && res.Children.Contains("--password"))
					{
						return "Only one authentication type can be supplied. '--private-key' or '--password'";
					}

					if (!res.Children.Contains("--private-key") && !res.Children.Contains("--password"))
					{
						return "Authentication option is not supplied. Use either '--private-key' or '--password'";
					}

					if (!res.Children.Contains("--network"))
					{
						return "At least one network must be supplied. Use --network <CIDR>";
					}
				}

				return null;
			});

			return rootCommand;
		}
	}
}
