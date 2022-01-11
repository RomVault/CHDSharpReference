using CHDReaderTest.Utils;
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CHDReaderTest
{
    internal static class CHDV5
    {
        internal class mapentry
        {
            public ulong offset;
            public uint crc;
            public ulong length;
            public mapFlags flags;
        }

        public static bool go(Stream file)
        {
            using BinaryReader br = new BinaryReader(file, Encoding.UTF8, true);

            uint[] compressions=new uint[4];
            for (int i = 0; i < 4; i++)
                compressions[i] = br.ReadUInt32BE();

            ulong totalbytes = br.ReadUInt64BE();  // total byte size of the image
            ulong mapoffset = br.ReadUInt64BE();
            ulong metaoffset = br.ReadUInt64BE();

            uint blocksize = br.ReadUInt32BE();    // length of a CHD Block
            uint unitbytes = br.ReadUInt32BE();
            byte[] rawsha1 = br.ReadBytes(20);
            byte[] sha1 = br.ReadBytes(20);
            byte[] parentsha1 = br.ReadBytes(20);

            return false;
        }

    }
}