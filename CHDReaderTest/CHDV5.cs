using CHDReaderTest.Utils;
using Compress.Support.Compression.LZMA;
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CHDReaderTest
{
    internal static class CHDV5
    {
        readonly static uint CHD_CODEC_NONE = 0;
        readonly static uint CHD_CODEC_ZLIB = CHD_MAKE_TAG('z', 'l', 'i', 'b');
        readonly static uint CHD_CODEC_LZMA = CHD_MAKE_TAG('l', 'z', 'm', 'a');
        readonly static uint CHD_CODEC_HUFFMAN = CHD_MAKE_TAG('h', 'u', 'f', 'f');
        readonly static uint CHD_CODEC_FLAC = CHD_MAKE_TAG('f', 'l', 'a', 'c');
        /* general codecs with CD frontend */
        readonly static uint CHD_CODEC_CD_ZLIB = CHD_MAKE_TAG('c', 'd', 'z', 'l');
        readonly static uint CHD_CODEC_CD_LZMA = CHD_MAKE_TAG('c', 'd', 'l', 'z');
        readonly static uint CHD_CODEC_CD_FLAC = CHD_MAKE_TAG('c', 'd', 'f', 'l');

        internal static uint CHD_MAKE_TAG(char a, char b, char c, char d)
        {
            return (uint)(((a) << 24) | ((b) << 16) | ((c) << 8) | (d));
        }


        internal class mapentry
        {
            public compression_type comptype;
            public ulong length;
            public ulong offset;
            public uint crc;
        }


        public static bool go(Stream file)
        {
            using BinaryReader br = new BinaryReader(file, Encoding.UTF8, true);

            uint[] compression = new uint[4];
            for (int i = 0; i < 4; i++)
                compression[i] = br.ReadUInt32BE();

            ulong totalbytes = br.ReadUInt64BE();  // total byte size of the image
            ulong mapoffset = br.ReadUInt64BE();
            ulong metaoffset = br.ReadUInt64BE();

            uint blocksize = br.ReadUInt32BE();    // length of a CHD Block
            uint unitbytes = br.ReadUInt32BE();
            byte[] rawsha1 = br.ReadBytes(20);
            byte[] sha1 = br.ReadBytes(20);
            byte[] parentsha1 = br.ReadBytes(20);

            uint blockCount = (uint)((totalbytes + blocksize - 1) / blocksize);
            //uint unitcount = (uint)((logicalbytes + unitbytes - 1) / unitbytes);

            bool chdCompressed = compression[0] != CHD_CODEC_NONE;
            uint mapentrybytes = (chdCompressed) ? (uint)12 : 4;

            byte[] rawmap;
            chd_error err = chdCompressed ?
                    decompress_v5_map(br, mapoffset, blockCount, blocksize, mapentrybytes, unitbytes, out rawmap) :
                    uncompressed_v5_map(br, mapoffset, blockCount, mapentrybytes, out rawmap);

            if (err != chd_error.CHDERR_NONE)
                return false;

            using SHA1 sha1Check = SHA1.Create();

            byte[] buffer = new byte[blocksize];

            int block = 0;
            ulong sizetoGo = totalbytes;
            while (sizetoGo > 0)
            {
                /* progress */
                if ((block % 1000) == 0)
                    Console.Write($"Verifying, {(100 - sizetoGo * 100 / totalbytes):N1}% complete...\r");

                err = readBlock(file, compression, block, mapentrybytes, rawmap, blocksize, ref buffer);
                if (err != chd_error.CHDERR_NONE)
                    return false;

                int sizenext = sizetoGo > (ulong)blocksize ? (int)blocksize : (int)sizetoGo;

                sha1Check.TransformBlock(buffer, 0, sizenext, null, 0);

                /* prepare for the next block */
                block++;
                sizetoGo -= (ulong)sizenext;

            }
            Console.WriteLine("");

            byte[] tmp = new byte[0];
            sha1Check.TransformFinalBlock(tmp, 0, 0);

            // here it is now using the rawsha1 value from the header to validate the raw binary data.
            if (!Util.ByteArrEquals(rawsha1, sha1Check.Hash))
            {
                return false;
            }

            return true;

        }

        private static chd_error uncompressed_v5_map(BinaryReader br, ulong mapoffset, uint hunkcount, uint mapentrybytes, out byte[] rawmap)
        {
            uint rawmapsize = hunkcount * mapentrybytes; // uncompressed raw map size

            rawmap = new byte[rawmapsize];
            br.BaseStream.Seek((long)mapoffset, SeekOrigin.Begin);
            br.BaseStream.Read(rawmap, 0, (int)rawmapsize);
            return chd_error.CHDERR_NONE;

        }

        private static chd_error decompress_v5_map(BinaryReader br, ulong mapoffset, uint hunkcount, uint hunkbytes, uint mapentrybytes, uint unitbytes, out byte[] rawmap)
        {
            uint rawmapsize = hunkcount * mapentrybytes;  // compressed raw map size

            /* read the reader */
            br.BaseStream.Seek((long)mapoffset, SeekOrigin.Begin);
            uint mapbytes = br.ReadUInt32BE(); //0
            ulong firstoffs = br.ReadUInt48BE(); //4
            ushort mapcrc = br.ReadUInt16BE();  //10
            byte lengthbits = br.ReadByte();  //12
            byte selfbits = br.ReadByte();    //13
            byte parentbits = br.ReadByte();    //14
                                                //15 not used

            byte[] compressed_arr = new byte[mapbytes];
            br.BaseStream.Seek((long)mapoffset + 16, SeekOrigin.Begin);
            br.BaseStream.Read(compressed_arr, 0, (int)mapbytes);

            BitStream bitbuf = new BitStream(compressed_arr, mapbytes);
            rawmap = new byte[rawmapsize];

            /* first decode the compression types */
            HuffmanDecoder decoder = new HuffmanDecoder(16, 8, bitbuf);
            if (decoder == null)
            {
                return chd_error.CHDERR_OUT_OF_MEMORY;
            }

            huffman_error err = decoder.ImportTreeRLE();
            if (err != huffman_error.HUFFERR_NONE)
            {
                return chd_error.CHDERR_DECOMPRESSION_ERROR;
            }

            int repcount = 0;
            byte lastcomp = 0;
            for (uint hunknum = 0; hunknum < hunkcount; hunknum++)
            {
                uint rawmapIndex = (hunknum * 12);
                if (repcount > 0)
                {
                    rawmap[rawmapIndex] = lastcomp;
                    repcount--;
                }
                else
                {
                    compression_type val = (compression_type)decoder.DecodeOne();
                    if (val == compression_type.COMPRESSION_RLE_SMALL)
                    {
                        rawmap[rawmapIndex] = lastcomp;
                        repcount = 2 + decoder.DecodeOne();
                    }
                    else if (val == compression_type.COMPRESSION_RLE_LARGE)
                    {
                        rawmap[rawmapIndex] = lastcomp;
                        repcount = 2 + 16 + (decoder.DecodeOne() << 4);
                        repcount += decoder.DecodeOne();
                    }
                    else
                        rawmap[rawmapIndex] = lastcomp = (byte)val;
                }
            }

            /* then iterate through the hunks and extract the needed data */
            uint last_self = 0;
            ulong last_parent = 0;
            ulong curoffset = firstoffs;
            for (uint hunknum = 0; hunknum < hunkcount; hunknum++)
            {
                uint rawmapIndex = (hunknum * 12);
                ulong offset = curoffset;
                uint length = 0;
                ushort crc = 0;
                switch ((compression_type)rawmap[rawmapIndex])
                {
                    /* base types */
                    case compression_type.COMPRESSION_TYPE_0:
                    case compression_type.COMPRESSION_TYPE_1:
                    case compression_type.COMPRESSION_TYPE_2:
                    case compression_type.COMPRESSION_TYPE_3:
                        curoffset += length = (uint)bitbuf.read(lengthbits);
                        crc = (ushort)bitbuf.read(16);
                        break;

                    case compression_type.COMPRESSION_NONE:
                        curoffset += length = hunkbytes;
                        crc = (ushort)bitbuf.read(16);
                        break;

                    case compression_type.COMPRESSION_SELF:
                        last_self = (uint)(offset = bitbuf.read(selfbits));
                        break;

                    case compression_type.COMPRESSION_PARENT:
                        offset = bitbuf.read(parentbits);
                        last_parent = offset;
                        break;

                    /* pseudo-types; convert into base types */
                    case compression_type.COMPRESSION_SELF_1:
                        last_self++;
                        goto case compression_type.COMPRESSION_SELF_0;

                    case compression_type.COMPRESSION_SELF_0:
                        rawmap[rawmapIndex] = (byte)compression_type.COMPRESSION_SELF;
                        offset = last_self;
                        break;

                    case compression_type.COMPRESSION_PARENT_SELF:
                        rawmap[rawmapIndex] = (byte)compression_type.COMPRESSION_PARENT;
                        last_parent = offset = (((ulong)hunknum) * ((ulong)hunkbytes)) / unitbytes;
                        break;

                    case compression_type.COMPRESSION_PARENT_1:
                        last_parent += hunkbytes / unitbytes;
                        goto case compression_type.COMPRESSION_PARENT_0;
                    case compression_type.COMPRESSION_PARENT_0:
                        rawmap[rawmapIndex] = (byte)compression_type.COMPRESSION_PARENT;
                        offset = last_parent;
                        break;
                }
                /* UINT24 length */
                rawmap.PutUInt24BE((int)rawmapIndex + 1, length);

                /* UINT48 offset */
                rawmap.PutUInt48BE((int)rawmapIndex + 4, offset);

                /* crc16 */
                rawmap.PutUInt16BE((int)rawmapIndex + 10, crc);
            }

            /* verify the final CRC */
            if (CRC16.calc(rawmap, hunkcount * 12) != mapcrc)
                return chd_error.CHDERR_DECOMPRESSION_ERROR;

            return chd_error.CHDERR_NONE;
        }


        private static chd_error readBlock(Stream file, uint[] compression, int block, uint mapentrybytes, byte[] rawmap, uint blocksize, ref byte[] cache)
        {
            int rawmapIndex = (int)(block * mapentrybytes);
            ulong blockoffs;

            if (compression[0] == CHD_CODEC_NONE)
            {
                blockoffs = rawmap.ReadUInt32BE(rawmapIndex) * blocksize;
                if (blockoffs != 0)
                {
                    file.Seek((long)blockoffs, SeekOrigin.Begin);
                    file.Read(cache, 0, (int)blocksize);
                }
                else
                {
                    for (int j = 0; j < blocksize; j++)
                        cache[j] = 0;
                }
                return chd_error.CHDERR_NONE;
            }


            uint blocklen = rawmap.ReadUInt24BE(rawmapIndex + 1);
            blockoffs = rawmap.ReadUInt48BE(rawmapIndex + 4);
            ushort blockcrc = rawmap.ReadUInt16BE(rawmapIndex + 10);

            switch ((compression_type)rawmap[rawmapIndex])
            {
                case compression_type.COMPRESSION_TYPE_0:
                case compression_type.COMPRESSION_TYPE_1:
                case compression_type.COMPRESSION_TYPE_2:
                case compression_type.COMPRESSION_TYPE_3:
                    file.Seek((long)blockoffs, SeekOrigin.Begin);

                    uint comp = compression[rawmap[rawmapIndex]];
                    if (comp == CHD_CODEC_ZLIB)
                    {
                        //Console.WriteLine("ZLIB");
                        using (var st = new DeflateStream(file, CompressionMode.Decompress, true))
                        {
                            int bytesRead = 0;
                            while (bytesRead < blocksize)
                            {
                                int bytes = st.Read(cache, bytesRead, (int)blocksize - bytesRead);
                                if (bytes == 0)
                                    return chd_error.CHDERR_INVALID_DATA;
                                bytesRead += bytes;
                            }
                        }
                        if (CRC16.calc(cache, blocksize) != blockcrc)
                            return chd_error.CHDERR_DECOMPRESSION_ERROR;
                    }
                    else if (comp == CHD_CODEC_LZMA)
                    {
                        //Console.WriteLine("LZMA");

                        //hacky header creator
                        byte[] properties = new byte[5];
                        int posStateBits = 2;
                        int numLiteralPosStateBits = 0;
                        int numLiteralContextBits = 3;
                        int dictionarySize = (int)blocksize;
                        properties[0] = (byte)((posStateBits * 5 + numLiteralPosStateBits) * 9 + numLiteralContextBits);
                        for (int j = 0; j < 4; j++)
                            properties[1 + j] = (Byte)((dictionarySize >> (8 * j)) & 0xFF);

                        using (Stream st = new LzmaStream(properties, file))
                        {
                            int bytesRead = 0;
                            while (bytesRead < blocksize)
                            {
                                int bytes = st.Read(cache, bytesRead, (int)blocksize - bytesRead);
                                if (bytes == 0)
                                    return chd_error.CHDERR_INVALID_DATA;
                                bytesRead += bytes;
                            }
                        }
                        if (CRC16.calc(cache, blocksize) != blockcrc)
                            return chd_error.CHDERR_DECOMPRESSION_ERROR;
                    }
                    else if (comp == CHD_CODEC_HUFFMAN)
                    {
                        //Console.WriteLine("HUFFMAN");

                        byte[] compressed_arr = new byte[blocklen];
                        file.Read(compressed_arr, 0, (int)blocklen);
                        BitStream bitbuf = new BitStream(compressed_arr, blocklen);
                        HuffmanDecoder hd = new HuffmanDecoder(256, 16, bitbuf);

                        if (hd.ImportTreeHuffman() != huffman_error.HUFFERR_NONE)
                            return chd_error.CHDERR_INVALID_DATA;

                        for (int j = 0; j < blocksize; j++)
                        {
                            cache[j] = hd.DecodeOne();
                        }

                        if (CRC16.calc(cache, blocksize) != blockcrc)
                            return chd_error.CHDERR_DECOMPRESSION_ERROR;

                    }
                    else if (comp == CHD_CODEC_FLAC)
                    {
                        //Console.WriteLine("FLAC");
                    }
                    else if (comp == CHD_CODEC_CD_ZLIB)
                    {
                        Console.WriteLine("CD_ZLIB");
                    }
                    else if (comp == CHD_CODEC_CD_LZMA)
                    {
                        Console.WriteLine("CD_LZMA");
                    }
                    else if (comp == CHD_CODEC_CD_FLAC)
                    {
                        Console.WriteLine("CD_FLAC");
                    }
                    else
                    {
                        Console.WriteLine("Unknown compression type");
                    }
                    break;

                case compression_type.COMPRESSION_NONE:
                    //Console.WriteLine("Compression_None");
                    file.Seek((long)blockoffs, SeekOrigin.Begin);
                    if (blocklen != blocksize)
                        return chd_error.CHDERR_DECOMPRESSION_ERROR;
                    file.Read(cache, 0, (int)blocksize);
                    if (CRC16.calc(cache, blocksize) != blockcrc)
                        return chd_error.CHDERR_DECOMPRESSION_ERROR;

                    break;
                case compression_type.COMPRESSION_SELF:
                    //Console.WriteLine("Compression_Self");
                    return readBlock(file, compression, (int)blockoffs, mapentrybytes, rawmap, blocksize, ref cache);

                case compression_type.COMPRESSION_PARENT:
                    Console.WriteLine("Compression_Parent");
                    return chd_error.CHDERR_INVALID_FILE;
            }


            return chd_error.CHDERR_NONE;
        }

        public enum compression_type
        {
            /* codec #0
             * these types are live when running */
            COMPRESSION_TYPE_0 = 0,
            /* codec #1 */
            COMPRESSION_TYPE_1 = 1,
            /* codec #2 */
            COMPRESSION_TYPE_2 = 2,
            /* codec #3 */
            COMPRESSION_TYPE_3 = 3,
            /* no compression; implicit length = hunkbytes */
            COMPRESSION_NONE = 4,
            /* same as another block in this chd */
            COMPRESSION_SELF = 5,
            /* same as a hunk's worth of units in the parent chd */
            COMPRESSION_PARENT = 6,

            /* start of small RLE run (4-bit length)
             * these additional pseudo-types are used for compressed encodings: */
            COMPRESSION_RLE_SMALL,
            /* start of large RLE run (8-bit length) */
            COMPRESSION_RLE_LARGE,
            /* same as the last COMPRESSION_SELF block */
            COMPRESSION_SELF_0,
            /* same as the last COMPRESSION_SELF block + 1 */
            COMPRESSION_SELF_1,
            /* same block in the parent */
            COMPRESSION_PARENT_SELF,
            /* same as the last COMPRESSION_PARENT block */
            COMPRESSION_PARENT_0,
            /* same as the last COMPRESSION_PARENT block + 1 */
            COMPRESSION_PARENT_1
        };



    }
}