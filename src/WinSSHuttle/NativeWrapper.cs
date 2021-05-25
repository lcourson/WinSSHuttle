using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace WinSSHuttle
{
	public static class NativeWrapper
	{
		private static ShowWindowCommands _lastState = ShowWindowCommands.Normal;
		public struct WINDOWPLACEMENT
		{
			public int length;
			public int flags;
			public int showCmd;
			public Point ptMinPosition;
			public Point ptMaxPosition;
			public Rectangle rcNormalPosition;
		}

		public enum ShowWindowCommands
		{
			NoState = -1,
			Hide = 0,
			Normal = 1,
			ShowMinimized = 2,
			Maximize = 3,
			ShowMaximized = 3,
			ShowNoActivate = 4,
			Show = 5,
			Minimize = 6,
			ShowMinNoActive = 7,
			ShowNA = 8,
			Restore = 9,
			ShowDefault = 10,
			ForceMinimize = 11
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct ParentProcessUtilities
		{
			// These members must match PROCESS_BASIC_INFORMATION
			internal IntPtr Reserved1;
			internal IntPtr PebBaseAddress;
			internal IntPtr Reserved2_0;
			internal IntPtr Reserved2_1;
			internal IntPtr UniqueProcessId;
			internal IntPtr InheritedFromUniqueProcessId;
		}

		private const uint SC_CLOSE = 0xF060;
		private const uint MF_ENABLED = 0x00000000;
		private const uint MF_DISABLED = 0x00000002;

		#region DLLImports
		[DllImport("user32.dll", EntryPoint = "EnableMenuItem")]
		private static extern bool EnableMenuItemNative(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

		[DllImport("user32.dll", EntryPoint = "GetSystemMenu")]
		private static extern IntPtr GetSystemMenuNative(IntPtr hWnd, bool bRevert);

		[DllImport("user32.dll", EntryPoint = "FindWindow")]
		private static extern IntPtr FindWindowNative(string lpClassName, string lpWindowName);

		[DllImport("user32.dll", EntryPoint = "ShowWindow")]
		private static extern bool ShowWindowNative(IntPtr hWnd, int nCmdShow);

		[DllImport("user32.dll", EntryPoint = "GetWindowPlacement")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetWindowPlacementNative(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

		[DllImport("user32.dll", EntryPoint = "BringWindowToTop")]
		private static extern bool BringWindowToTopNative(IntPtr hWnd);

		[DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
		private static extern int NtQueryInformationProcessNative(IntPtr processHandle, int processInformationClass, ref ParentProcessUtilities processInformation, int processInformationLength, out int returnLength);
		#endregion

		#region Native Wrappers
		/// <summary>
		/// Gets the parent process of a specified process.
		/// </summary>
		/// <param name="handle">The process handle.</param>
		/// <returns>An instance of the Process class.</returns>
		public static Process GetParentProcess(IntPtr handle)
		{
			ParentProcessUtilities pbi = new ParentProcessUtilities();
			int returnLength;
			int status = NtQueryInformationProcessNative(handle, 0, ref pbi, Marshal.SizeOf(pbi), out returnLength);
			if (status != 0)
			{
				throw new Win32Exception(status);
			}

			try
			{
				return Process.GetProcessById(pbi.InheritedFromUniqueProcessId.ToInt32());
			}
			catch (ArgumentException)
			{
				// not found
				return null;
			}
		}

		private static bool BringWindowToTop(IntPtr hWnd)
		{
			return BringWindowToTopNative(hWnd);
		}

		private static bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable)
		{
			return EnableMenuItemNative(hMenu, uIDEnableItem, uEnable);
		}

		private static IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert)
		{
			return GetSystemMenuNative(hWnd, bRevert);
		}

		private static IntPtr FindWindow(string lpClassName, string lpWindowName)
		{
			return FindWindowNative(lpClassName, lpWindowName);
		}

		private static bool ShowWindow(IntPtr hWnd, int nCmdShow)
		{
			return ShowWindowNative(hWnd, nCmdShow);
		}

		public static bool GetWindowPlacement(ref WINDOWPLACEMENT lpwndpl)
		{
			var hWnd = GetConsoleWindow();
			var x = GetWindowPlacementNative(hWnd, ref lpwndpl);
			if (
				(ShowWindowCommands)lpwndpl.showCmd != ShowWindowCommands.Hide
				&& (ShowWindowCommands)lpwndpl.showCmd != ShowWindowCommands.ShowMinimized
			)
			{
				_lastState = (ShowWindowCommands)lpwndpl.showCmd;
			}
			return x;
		}

		public static IntPtr GetConsoleWindow()
		{
			return FindWindow(null, Console.Title);
		}
		#endregion

		/// <summary>
		/// Gets the parent process of the current process.
		/// </summary>
		/// <returns>An instance of the Process class.</returns>
		public static Process GetParentProcess()
		{
			return GetParentProcess(Process.GetCurrentProcess().Handle);
		}

		public static ShowWindowCommands GetWindowState()
		{
			WINDOWPLACEMENT wPlacement = new WINDOWPLACEMENT();
			GetWindowPlacement(ref wPlacement);
			return (ShowWindowCommands)wPlacement.showCmd;
		}

		public static void SetConsoleWindowVisibility(bool show)
		{
			var hWnd = GetConsoleWindow();
			if (hWnd != IntPtr.Zero)
			{
				if (show)
				{
					ShowWindow(hWnd, (int)_lastState);
					BringWindowToTop(hWnd);
				}
				else
				{
					ShowWindow(hWnd, (int)ShowWindowCommands.Hide);
				}
			}
		}

		public static void DisableClose()
		{
			var hWnd = GetSystemMenu(GetConsoleWindow(), false);
			EnableMenuItem(hWnd, SC_CLOSE, (uint)(MF_ENABLED | MF_DISABLED));
		}
	}
}
