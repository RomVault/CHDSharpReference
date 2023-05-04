using CHDReaderTest.Utils;
using Compress.Support.Compression.LZMA;
using System;
using System.IO;
using System.IO.Compression;
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

        internal class chd_header
        {
            internal uint[] compression;
            internal ulong logicalbytes;  // total byte size of the image
            internal ulong mapoffset;
            internal ulong metaoffset;

            internal uint hunkbytes;    // length of a CHD Block
            internal uint unitbytes;
            internal byte[] rawsha1;
            internal byte[] sha1;
            internal byte[] parentsha1;

            internal uint hunkcount;
            internal uint unitcount;

            internal uint mapentrybytes;
            internal uint totalhunks;

            internal byte[] rawmap;
        }

        public static bool go(Stream file)
        {
            using BinaryReader br = new BinaryReader(file, Encoding.UTF8, true);

            chd_header header = new chd_header();

            header.compression = new uint[4];
            for (int i = 0; i < 4; i++)
                header.compression[i] = br.ReadUInt32BE();

            header.logicalbytes = br.ReadUInt64BE();  // total byte size of the image
            header.mapoffset = br.ReadUInt64BE();
            header.metaoffset = br.ReadUInt64BE();

            header.hunkbytes = br.ReadUInt32BE();    // length of a CHD Block
            header.unitbytes = br.ReadUInt32BE();
            header.rawsha1 = br.ReadBytes(20);
            header.sha1 = br.ReadBytes(20);
            header.parentsha1 = br.ReadBytes(20);

            header.hunkcount = (uint)((header.logicalbytes + header.hunkbytes - 1) / header.hunkbytes);
            header.unitcount = (uint)((header.logicalbytes + header.unitbytes - 1) / header.unitbytes);

            header.mapentrybytes = chd_compressed(header) ? (uint)12 : 4;
            header.totalhunks = header.hunkcount;

            decompress_v5_map(br, header);
            byte[] cache = new byte[header.hunkbytes];

            for (uint i = 0; i < header.totalhunks; i++)
            {
                uint mapIndex = i * header.mapentrybytes;
                uint blocklen = header.rawmap.ReadUInt24BE((int)mapIndex + 1);
                ulong blockoffs = header.rawmap.ReadUInt48BE((int)mapIndex + 4);
                ushort blockcrc = header.rawmap.ReadUInt16BE((int)mapIndex + 10);

                switch ((compression_type)header.rawmap[mapIndex])
                {
                    case compression_type.COMPRESSION_TYPE_0:
                    case compression_type.COMPRESSION_TYPE_1:
                    case compression_type.COMPRESSION_TYPE_2:
                    case compression_type.COMPRESSION_TYPE_3:
                        file.Seek((long)blockoffs, SeekOrigin.Begin);

                        uint comp = header.compression[header.rawmap[mapIndex]];
                        if (comp == CHD_CODEC_ZLIB)
                        {
                            //Console.WriteLine("ZLIB");
                            using (var st = new DeflateStream(file, CompressionMode.Decompress, true))
                            {
                                int bytesRead = 0;
                                int blocksize = (int)header.hunkbytes;
                                while (bytesRead < blocksize)
                                {
                                    int bytes = st.Read(cache, bytesRead, (int)blocksize - bytesRead);
                                    if (bytes == 0)
                                        return false;
                                    bytesRead += bytes;
                                }
                            }
                            if (CRC16.calc(cache, header.hunkbytes) != blockcrc)
                                return false;
                        }
                        else if (comp == CHD_CODEC_LZMA)
                        {
                            //Console.WriteLine("LZMA");

                            byte[] properties = new byte[5];

                            int posStateBits = 2;
                            int numLiteralPosStateBits = 0;
                            int numLiteralContextBits = 3;
                            int dictionarySize = (int)header.hunkbytes;
                            properties[0] = (byte)((posStateBits * 5 + numLiteralPosStateBits) * 9 + numLiteralContextBits);
                            for (int j = 0; j < 4; j++)
                                properties[1 + j] = (Byte)((dictionarySize >> (8 * j)) & 0xFF);

                            using (Stream st = new LzmaStream(properties, file))
                            {
                                int bytesRead = 0;
                                int blocksize = (int)header.hunkbytes;
                                while (bytesRead < blocksize)
                                {
                                    int bytes = st.Read(cache, bytesRead, (int)blocksize - bytesRead);
                                    if (bytes == 0)
                                        return false;
                                    bytesRead += bytes;
                                }
                            }
                            if (CRC16.calc(cache, header.hunkbytes) != blockcrc)
                                return false;
                        }
                        else if (comp == CHD_CODEC_HUFFMAN)
                        {
                            //Console.WriteLine("HUFFMAN");

                            byte[] compressed_arr = new byte[blocklen];
                            file.Read(compressed_arr, 0, (int)blocklen);
                            BitStream bitbuf = new BitStream(compressed_arr, blocklen);
                            HuffmanDecoder hd = new HuffmanDecoder(256, 16, bitbuf);

                            if (hd.ImportTreeHuffman() != huffman_error.HUFFERR_NONE)
                                return false;

                            for (int j = 0; j < header.hunkbytes; j++)
                            {
                                cache[j] = hd.DecodeOne();
                            }

                            if (CRC16.calc(cache, header.hunkbytes) != blockcrc)
                                return false;

                        }
                        else if (comp == CHD_CODEC_FLAC)
                        {
                            Console.WriteLine("FLAC");
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
                        break;
                    case compression_type.COMPRESSION_SELF:
                        break;
                    case compression_type.COMPRESSION_PARENT:
                        break;

                }

            }

            return true;
        }

        private static bool chd_compressed(chd_header header)
        {
            return header.compression[0] != CHD_CODEC_NONE;
        }
        private static uint map_size_v5(chd_header header)
        {
            return header.hunkcount * header.mapentrybytes;
        }

        private static chd_error decompress_v5_map(BinaryReader br, chd_header header)
        {
            uint rawmapsize = map_size_v5(header);

            if (!chd_compressed(header))
            {
                header.rawmap = new byte[rawmapsize];
                br.BaseStream.Seek((long)header.mapoffset, SeekOrigin.Begin);
                br.BaseStream.Read(header.rawmap, 0, (int)rawmapsize);
                return chd_error.CHDERR_NONE;
            }

            /* read the reader */
            br.BaseStream.Seek((long)header.mapoffset, SeekOrigin.Begin);
            uint mapbytes = br.ReadUInt32BE(); //0
            ulong firstoffs = br.ReadUInt48BE(); //4
            ushort mapcrc = br.ReadUInt16BE();  //10
            byte lengthbits = br.ReadByte();  //12
            byte selfbits = br.ReadByte();    //13
            byte parentbits=br.ReadByte();    //14
                                              //15 not used

            byte[] compressed_arr = new byte[mapbytes];
            br.BaseStream.Seek((long)header.mapoffset + 16, SeekOrigin.Begin);
            br.BaseStream.Read(compressed_arr, 0, (int)mapbytes);

            BitStream bitbuf = new BitStream(compressed_arr, mapbytes);
            header.rawmap = new byte[rawmapsize];

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
            for (uint hunknum = 0; hunknum < header.hunkcount; hunknum++)
            {
                uint rawmapIndex = (hunknum * 12);
                if (repcount > 0)
                {
                    header.rawmap[rawmapIndex] = lastcomp;
                    repcount--;
                }
                else
                {
                    compression_type val = (compression_type)decoder.DecodeOne();
                    if (val == compression_type.COMPRESSION_RLE_SMALL)
                    {
                        header.rawmap[rawmapIndex] = lastcomp;
                        repcount = 2 + decoder.DecodeOne();
                    }
                    else if (val == compression_type.COMPRESSION_RLE_LARGE)
                    {
                        header.rawmap[rawmapIndex] = lastcomp;
                        repcount = 2 + 16 + (decoder.DecodeOne() << 4);
                        repcount += decoder.DecodeOne();
                    }
                    else
                        header.rawmap[rawmapIndex] = lastcomp = (byte)val;
                }
            }

            /* then iterate through the hunks and extract the needed data */
            uint last_self = 0;
            ulong last_parent = 0;
            ulong curoffset = firstoffs;
            for (uint hunknum = 0; hunknum < header.hunkcount; hunknum++)
            {
                uint rawmapIndex = (hunknum * 12);
                ulong offset = curoffset;
                uint length = 0;
                ushort crc = 0;
                switch ((compression_type)header.rawmap[rawmapIndex])
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
                        curoffset += length = header.hunkbytes;
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
                        header.rawmap[rawmapIndex] = (byte)compression_type.COMPRESSION_SELF;
                        offset = last_self;
                        break;

                    case compression_type.COMPRESSION_PARENT_SELF:
                        header.rawmap[rawmapIndex] = (byte)compression_type.COMPRESSION_PARENT;
                        last_parent = offset = (((ulong)hunknum) * ((ulong)header.hunkbytes)) / header.unitbytes;
                        break;

                    case compression_type.COMPRESSION_PARENT_1:
                        last_parent += header.hunkbytes / header.unitbytes;
                        goto case compression_type.COMPRESSION_PARENT_0;
                    case compression_type.COMPRESSION_PARENT_0:
                        header.rawmap[rawmapIndex] = (byte)compression_type.COMPRESSION_PARENT;
                        offset = last_parent;
                        break;
                }
                /* UINT24 length */
                header.rawmap.PutUInt24BE((int)rawmapIndex + 1, length);

                /* UINT48 offset */
                header.rawmap.PutUInt48BE((int)rawmapIndex + 4, offset);

                /* crc16 */
                header.rawmap.PutUInt16BE((int)rawmapIndex + 10, crc);
            }

            /* verify the final CRC */
            if (CRC16.calc(header.rawmap, header.hunkcount * 12) != mapcrc)
                return chd_error.CHDERR_DECOMPRESSION_ERROR;

            return chd_error.CHDERR_NONE;
        }

        /*
        internal class mapentry
        {
            public ulong offset;
            public uint crc;
            public ulong length;
            public mapFlags flags;
        }
        */
        enum compression_type
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
        enum chd_error
        {
            CHDERR_NONE,
            CHDERR_NO_INTERFACE,
            CHDERR_OUT_OF_MEMORY,
            CHDERR_INVALID_FILE,
            CHDERR_INVALID_PARAMETER,
            CHDERR_INVALID_DATA,
            CHDERR_FILE_NOT_FOUND,
            CHDERR_REQUIRES_PARENT,
            CHDERR_FILE_NOT_WRITEABLE,
            CHDERR_READ_ERROR,
            CHDERR_WRITE_ERROR,
            CHDERR_CODEC_ERROR,
            CHDERR_INVALID_PARENT,
            CHDERR_HUNK_OUT_OF_RANGE,
            CHDERR_DECOMPRESSION_ERROR,
            CHDERR_COMPRESSION_ERROR,
            CHDERR_CANT_CREATE_FILE,
            CHDERR_CANT_VERIFY,
            CHDERR_NOT_SUPPORTED,
            CHDERR_METADATA_NOT_FOUND,
            CHDERR_INVALID_METADATA_SIZE,
            CHDERR_UNSUPPORTED_VERSION,
            CHDERR_VERIFY_INCOMPLETE,
            CHDERR_INVALID_METADATA,
            CHDERR_INVALID_STATE,
            CHDERR_OPERATION_PENDING,
            CHDERR_NO_ASYNC_OPERATION,
            CHDERR_UNSUPPORTED_FORMAT
        };


    }
}