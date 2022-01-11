using CHDReaderTest.Utils;
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CHDReaderTest
{
    internal static class CHDV3
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
            uint flags = br.ReadUInt32BE();

            // 1=HDCOMPRESSION_ZLIB
            // 2=HDCOMPRESSION_ZLIB_PLUS
            uint compression = br.ReadUInt32BE();
            uint totalblocks = br.ReadUInt32BE(); // total number of CHD Blocks

            ulong totalbytes = br.ReadUInt64BE();  // total byte size of the image
            ulong metaoffset = br.ReadUInt64BE();

            byte[] md5 = br.ReadBytes(16);
            byte[] parentmd5 = br.ReadBytes(16);
            uint blocksize = br.ReadUInt32BE();    // length of a CHD Block
            byte[] sha1 = br.ReadBytes(20);
            byte[] parentsha1 = br.ReadBytes(20);

            if (compression != 1 && compression != 2)
            {
                Console.WriteLine($"Unknown Compression type {compression}");
                return false;
            }

            mapentry[] map = new mapentry[totalblocks];

            for (int i = 0; i < totalblocks; i++)
            {
                mapentry me = new mapentry()
                {
                    offset = br.ReadUInt64BE(),
                    crc = br.ReadUInt32BE(),
                    length = br.ReadUInt16BE(),
                    flags = (mapFlags)br.ReadUInt16BE()

                };
                map[i] = me;
            }


            using MD5 md5Check = MD5.Create();
            using SHA1 sha1Check = SHA1.Create();


            byte[] buffer = new byte[blocksize];

            int block = 0;
            ulong sizetoGo = totalbytes;
            while (sizetoGo > 0)
            {
                /* progress */
                if ((block % 1000) == 0)
                    Console.Write($"Verifying, {(100 - sizetoGo * 100 / totalbytes):N1}% complete...\r");


                /* read the block into the cache */
                hdErr err = readBlock(file, block, map, (uint)blocksize, ref buffer);
                if (err != hdErr.HDERR_NONE)
                    return false;

                int sizenext = sizetoGo > (ulong)blocksize ? (int)blocksize : (int)sizetoGo;

                md5Check.TransformBlock(buffer, 0, sizenext, null, 0);
                sha1Check.TransformBlock(buffer, 0, sizenext, null, 0);

                /* prepare for the next block */
                block++;
                sizetoGo -= (ulong)sizenext;

            }
            Console.WriteLine("");

            byte[] tmp = new byte[0];
            md5Check.TransformFinalBlock(tmp, 0, 0);
            sha1Check.TransformFinalBlock(tmp, 0, 0);
            if (!Util.ByteArrCompare(md5, md5Check.Hash))
            {
                return false;
            }
            if (!Util.ByteArrCompare(sha1, sha1Check.Hash))
            {
                return false;
            }

            // now check the meta data??

            return true;
        }

        private static hdErr readBlock(Stream file, int mapindex, mapentry[] map, uint blocksize, ref byte[] cache)
        {
            bool checkCrc = true;
            mapentry mapEntry = map[mapindex];

            switch (mapEntry.flags & mapFlags.MAP_ENTRY_FLAG_TYPE_MASK)
            {
                case mapFlags.MAP_ENTRY_TYPE_COMPRESSED:
                    {
                        file.Seek((long)mapEntry.offset, SeekOrigin.Begin);
                        using (var st = new DeflateStream(file, CompressionMode.Decompress, true))
                        {
                            int bytes = st.Read(cache, 0, (int)blocksize);
                            if (bytes != (int)blocksize)
                                return hdErr.HDERR_READ_ERROR;
                        }
                        break;
                    }

                case mapFlags.MAP_ENTRY_TYPE_UNCOMPRESSED:
                    {
                        file.Seek((long)mapEntry.offset, SeekOrigin.Begin);
                        int bytes = file.Read(cache, 0, (int)blocksize);

                        if (bytes != (int)blocksize)
                            return hdErr.HDERR_READ_ERROR;
                        break;
                    }

                case mapFlags.MAP_ENTRY_TYPE_MINI:
                    {
                        byte[] tmp = BitConverter.GetBytes(mapEntry.offset);
                        for (int i = 0; i < 8; i++)
                        {
                            cache[i] = tmp[7 - i];
                        }

                        for (int i = 8; i < blocksize; i++)
                        {
                            cache[i] = cache[i - 8];
                        }

                        break;
                    }

                case mapFlags.MAP_ENTRY_TYPE_SELF_HUNK:
                    {
                        hdErr ret = readBlock(file, (int)mapEntry.offset, map, blocksize, ref cache);
                        if (ret != hdErr.HDERR_NONE)
                            return ret;
                        // check CRC in the read_block_into_cache call
                        checkCrc = false;
                        break;
                    }
                default:
                    return hdErr.HDERR_DECOMPRESSION_ERROR;

            }

            if (checkCrc && (mapEntry.flags & mapFlags.MAP_ENTRY_FLAG_NO_CRC) == 0)
            {
                if (!CRC.VerifyDigest(mapEntry.crc, cache, 0, blocksize))
                    return hdErr.HDERR_DECOMPRESSION_ERROR;
            }
            return hdErr.HDERR_NONE;
        }

    }
}
