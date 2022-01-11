using CHDReaderTest.Utils;
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CHDReaderTest
{
    internal static class CHDV1
    {
        internal class mapentry
        {
            public ulong offset;
            public ulong length;
            public mapFlags flags;
        }

        public static bool go(Stream file)
        {
            using BinaryReader br = new BinaryReader(file, Encoding.UTF8, true);
            uint flags = br.ReadUInt32BE();
            uint compression = br.ReadUInt32BE();
            uint blocksize = br.ReadUInt32BE();
            uint totalblocks = br.ReadUInt32BE(); // total number of CHD Blocks
            uint cylinders = br.ReadUInt32BE();
            uint heads = br.ReadUInt32BE();
            uint sectors = br.ReadUInt32BE();
            byte[] md5 = br.ReadBytes(16);
            byte[] parentmd5 = br.ReadBytes(16);

            const int HARD_DISK_SECTOR_SIZE = 512;
            ulong totalbytes = cylinders * heads * sectors * HARD_DISK_SECTOR_SIZE;
            blocksize = blocksize * HARD_DISK_SECTOR_SIZE;

            mapentry[] map = new mapentry[totalblocks];

            for (int i = 0; i < totalblocks; i++)
            {
                ulong tmpu = br.ReadUInt64BE();

                mapentry me = new mapentry()
                {
                    offset = tmpu & 0xfffffffffff,
                    length = tmpu >> 44,
                };
                me.flags = (me.length == blocksize)
                               ? mapFlags.MAP_ENTRY_TYPE_UNCOMPRESSED
                               : mapFlags.MAP_ENTRY_TYPE_COMPRESSED;
                map[i] = me;
            }

            using MD5 md5Check = MD5.Create();

            byte[] buffer = new byte[blocksize];
            for (int block = 0; block < totalblocks; block++)
            {
                /* progress */
                if ((block % 1000) == 0)
                    Console.Write($"Verifying, {(block * 100 / totalblocks):N1}% complete...\r");

                /* read the block into the cache */
                hdErr err = readBlock(file, map[block], blocksize, ref buffer);
                if (err != hdErr.HDERR_NONE)
                    return false;

                md5Check?.TransformBlock(buffer, 0, (int)blocksize, null, 0);

            }
            Console.WriteLine("");

            byte[] tmp = new byte[0];
            md5Check.TransformFinalBlock(tmp, 0, 0);
            if (!Util.ByteArrCompare(md5, md5Check.Hash))
            {
                return false;
            }

            return true;
        }

        private static hdErr readBlock(Stream file, mapentry map, uint blocksize, ref byte[] cache)
        {

            file.Seek((long)map.offset, SeekOrigin.Begin);

            if (map.flags == mapFlags.MAP_ENTRY_TYPE_UNCOMPRESSED)
            {
                int bytes = file.Read(cache, 0, (int)blocksize);
                if (bytes != (int)blocksize)
                    return hdErr.HDERR_READ_ERROR;
                return hdErr.HDERR_NONE;
            }

            using (var st = new DeflateStream(file, CompressionMode.Decompress, true))
            {
                int bytes = st.Read(cache, 0, (int)blocksize);
                if (bytes != (int)blocksize)
                    return hdErr.HDERR_READ_ERROR;
                return hdErr.HDERR_NONE;
            }

        }
    }
}
