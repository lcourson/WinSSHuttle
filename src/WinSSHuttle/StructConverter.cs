using System;
using System.Collections.Generic;
using System.Text;

// This is a crude implementation of a format string based struct converter for C#.
// This is probably not the best implementation, the fastest implementation, the most bug-proof implementation, or even the most functional implementation.
// It's provided as-is for free. Enjoy.

namespace WinSSHuttle
{
	public class StructConverter
	{
		/// <summary>
		/// Convert a byte array into an array of objects based on Python's "struct.unpack" protocol.
		/// </summary>
		/// <param name="fmt">A "struct.pack"-compatible format string</param>
		/// <param name="bytes">An array of bytes to convert to objects</param>
		/// <returns>Array of objects.</returns>
		/// <remarks>You are responsible for casting the objects in the array back to their proper types.</remarks>
		public static object[] Unpack(string fmt, byte[] bytes)
		{
			// First we parse the format string to make sure it's proper.
			if (fmt.Length < 1) throw new ArgumentException("Format string cannot be empty.");

			bool endianFlip = false;
			if (fmt.Substring(0, 1) == "<")
			{
				// Little endian.
				// Do we need to flip endianness?
				if (BitConverter.IsLittleEndian == false) endianFlip = true;
				fmt = fmt.Substring(1);
			}
			else if ((fmt.Substring(0, 1) == ">") || (fmt.Substring(0, 1) == "!"))
			{
				// Big endian.
				// Do we need to flip endianness?
				if (BitConverter.IsLittleEndian == true) endianFlip = true;
				fmt = fmt.Substring(1);
			}

			// Now, we find out how long the byte array needs to be
			int totalByteLength = 0;
			foreach (char c in fmt.ToCharArray())
			{
				switch (c)
				{
					case 'q':
					case 'Q':
						totalByteLength += 8;
						break;
					case 'i':
					case 'I':
						totalByteLength += 4;
						break;
					case 'h':
					case 'H':
						totalByteLength += 2;
						break;
					case 'b':
					case 'B':
					case 'x':
					case 'c':
						totalByteLength += 1;
						break;
					default:
						throw new ArgumentException("Invalid character found in format string.");
				}
			}

			// Test the byte array length to see if it contains as many bytes as is needed for the string.
			if (bytes.Length != totalByteLength) throw new ArgumentException("The number of bytes provided does not match the total length of the format string.");

			// Ok, we can go ahead and start parsing bytes!
			int byteArrayPosition = 0;
			List<object> outputList = new List<object>();
			byte[] buf;

			foreach (char c in fmt.ToCharArray())
			{
				switch (c)
				{
					case 'q':
						outputList.Add((object)(long)BitConverter.ToInt64(bytes, byteArrayPosition));
						byteArrayPosition += 8;
						break;
					case 'Q':
						outputList.Add((object)(ulong)BitConverter.ToUInt64(bytes, byteArrayPosition));
						byteArrayPosition += 8;
						break;
					case 'l':
						outputList.Add((object)(int)BitConverter.ToInt32(bytes, byteArrayPosition));
						byteArrayPosition += 4;
						break;
					case 'L':
						outputList.Add((object)(uint)BitConverter.ToUInt32(bytes, byteArrayPosition));
						byteArrayPosition += 4;
						break;
					case 'h':
						buf = new byte[2];
						Array.Copy(bytes, byteArrayPosition, buf, 0, 2);
						if (endianFlip)
						{
							Array.Reverse(buf);
						}
						outputList.Add((object)(short)BitConverter.ToInt16(buf, 0));
						byteArrayPosition += 2;
						break;
					case 'H':
						buf = new byte[2];
						Array.Copy(bytes, byteArrayPosition, buf, 0, 2);
						if (endianFlip)
						{
							Array.Reverse(buf);
						}
						outputList.Add((object)(ushort)BitConverter.ToUInt16(buf, 0));
						byteArrayPosition += 2;
						break;
					case 'c':
						buf = new byte[1];
						Array.Copy(bytes, byteArrayPosition, buf, 0, 1);
						outputList.Add((object)(char)buf[0]);
						byteArrayPosition++;
						break;
					case 'b':
						buf = new byte[1];
						Array.Copy(bytes, byteArrayPosition, buf, 0, 1);
						outputList.Add((object)(sbyte)buf[0]);
						byteArrayPosition++;
						break;
					case 'B':
						buf = new byte[1];
						Array.Copy(bytes, byteArrayPosition, buf, 0, 1);
						outputList.Add((object)(byte)buf[0]);
						byteArrayPosition++;
						break;
					case 'x':
						byteArrayPosition++;
						break;
					default:
						throw new ArgumentException("You should not be here.");
				}
			}
			return outputList.ToArray();
		}

		public static List<byte> Pack(string fmt, List<object> objects)
		{
			// First we parse the format string to make sure it's proper.
			if (fmt.Length < 1) throw new ArgumentException("Format string cannot be empty.");

			bool endianFlip = false;
			if (fmt.Substring(0, 1) == "<")
			{
				// Little endian.
				// Do we need to flip endianness?
				if (BitConverter.IsLittleEndian == false) endianFlip = true;
				fmt = fmt.Substring(1);
			}
			else if ((fmt.Substring(0, 1) == ">") || (fmt.Substring(0, 1) == "!"))
			{
				// Big endian.
				// Do we need to flip endianness?
				if (BitConverter.IsLittleEndian == true) endianFlip = true;
				fmt = fmt.Substring(1);
			}

			if (fmt.Length != objects.Count)
			{
				throw new Exception("Format string and object count do not match");
			}

			var outputBytes = new List<byte>();
			for (var i = 0; i < fmt.Length; i++)
			{
				switch (fmt[i])
				{
					//case 'q':
					//    outputList.Add((object)(long)BitConverter.ToInt64(bytes, byteArrayPosition));
					//    byteArrayPosition += 8;
					//    break;
					//case 'Q':
					//    outputList.Add((object)(ulong)BitConverter.ToUInt64(bytes, byteArrayPosition));
					//    byteArrayPosition += 8;
					//    break;
					//case 'l':
					//    outputList.Add((object)(int)BitConverter.ToInt32(bytes, byteArrayPosition));
					//    byteArrayPosition += 4;
					//    break;
					//case 'L':
					//    outputList.Add((object)(uint)BitConverter.ToUInt32(bytes, byteArrayPosition));
					//    byteArrayPosition += 4;
					//    break;
					//case 'h':
					//    buf = new byte[2];
					//    Array.Copy(bytes, byteArrayPosition, buf, 0, 2);
					//    if (endianFlip)
					//    {
					//        Array.Reverse(buf);
					//    }
					//    outputList.Add((object)(short)BitConverter.ToInt16(buf, 0));
					//    byteArrayPosition += 2;
					//    break;
					case 'H':
						var bs = BitConverter.GetBytes((ushort)objects[i]);

						if (endianFlip)
						{
							Array.Reverse(bs);
						}
						outputBytes.AddRange(bs);
						break;
					case 'c':
						outputBytes.AddRange(Encoding.ASCII.GetBytes(((char)objects[i]).ToString()));
						break;
					//case 'b':
					//    buf = new byte[1];
					//    Array.Copy(bytes, byteArrayPosition, buf, 0, 1);
					//    outputList.Add((object)(sbyte)buf[0]);
					//    byteArrayPosition++;
					//    break;
					//case 'B':
					//    buf = new byte[1];
					//    Array.Copy(bytes, byteArrayPosition, buf, 0, 1);
					//    outputList.Add((object)(byte)buf[0]);
					//    byteArrayPosition++;
					//    break;
					//case 'x':
					//    byteArrayPosition++;
					//    break;
					default:
						throw new ArgumentException("You should not be here.");
				}
			}

			return outputBytes;
		}
	}
}
