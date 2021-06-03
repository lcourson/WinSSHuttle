using System.IO;
using System.Threading;

namespace WinSSHuttle
{
	public class AppOptions
	{
		public string Name { get; set; }
		public short Priority { get; set; }
		public int Output { get; set; } = 0;
		public FileInfo PlinkExe { get; set; }
		public string BastionHost { get; set; }
		public FileInfo PrivateKey { get; set; }
		public string Password { get; set; }
		public bool IncludeDNS { get; set; }
		public bool AcceptHostKey { get; set; }
		public string[] Network { get; set; }
		public CancellationToken Token { get; set; }
		public bool DryRun { get; set; }
	}
}
