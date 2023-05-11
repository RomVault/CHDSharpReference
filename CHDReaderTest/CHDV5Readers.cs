using CHDReaderTest.Utils;
using Compress.Support.Compression.LZMA;
using CUETools.Codecs.Flake;
using CUETools.Codecs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace CHDReaderTest
{
    internal static class CHDV5Readers
    {
        internal static chd_error zlib(Stream file, int compsize, int hunksize, byte[] cache)
        {
            using (var st = new DeflateStream(file, CompressionMode.Decompress, true))
            {
                int bytesRead = 0;
                while (bytesRead < hunksize)
                {
                    int bytes = st.Read(cache, bytesRead, hunksize - bytesRead);
                    if (bytes == 0)
                        return chd_error.CHDERR_INVALID_DATA;
                    bytesRead += bytes;
                }
            }
            return chd_error.CHDERR_NONE;
        }

        internal static chd_error lzma(Stream file, int compsize, int hunksize, byte[] cache)
        {
            //hacky header creator
            byte[] properties = new byte[5];
            int posStateBits = 2;
            int numLiteralPosStateBits = 0;
            int numLiteralContextBits = 3;
            int dictionarySize = hunksize;
            properties[0] = (byte)((posStateBits * 5 + numLiteralPosStateBits) * 9 + numLiteralContextBits);
            for (int j = 0; j < 4; j++)
                properties[1 + j] = (Byte)((dictionarySize >> (8 * j)) & 0xFF);

            using (Stream st = new LzmaStream(properties, file))
            {
                int bytesRead = 0;
                while (bytesRead < hunksize)
                {
                    int bytes = st.Read(cache, bytesRead, hunksize - bytesRead);
                    if (bytes == 0)
                        return chd_error.CHDERR_INVALID_DATA;
                    bytesRead += bytes;
                }
            }
            return chd_error.CHDERR_NONE;
        }

        internal static chd_error huffman(Stream file, int compsize, int hunksize, byte[] cache)
        {
            byte[] compbytes = new byte[compsize];
            file.Read(compbytes, 0, compsize);
            BitStream bitbuf = new BitStream(compbytes);
            HuffmanDecoder hd = new HuffmanDecoder(256, 16, bitbuf);

            if (hd.ImportTreeHuffman() != huffman_error.HUFFERR_NONE)
                return chd_error.CHDERR_INVALID_DATA;

            for (int j = 0; j < hunksize; j++)
            {
                cache[j] = (byte)hd.DecodeOne();
            }
            return chd_error.CHDERR_NONE;
        }


        internal static chd_error flac(Stream file, int compsize, int hunksize, byte[] cache)
        {
            byte endianType = (byte)file.ReadByte();
            compsize--; //remove the endianType char

            //CHD adds a leading char to indicate endian. Not part of the flac format. Flac doesn't seem to support byteflipping
            bool swapEndian = ((endianType == 'L' && !BitConverter.IsLittleEndian) || (endianType == 'B' && BitConverter.IsLittleEndian)); //'L'ittle / 'B'ig

            byte[] buff = new byte[compsize];
            file.Read(buff, 0, compsize);

            //hard coded in libchdr, not sure if they always apply or can be variable
            int sampleBits = 16;
            int sampleRate = 44100;
            int channels = 2;
            //not used, but libChdr had this calc - might be important
            //int blockSize = 2352 / 4; //determine FLAC block size, which must be 16-65535 - clamp to 2k since that's supposed to be the sweet spot
            //if (blockSize > 2048)
            //    blockSize /= 2;
            //int samples = hunksize / (sampleBits * channels);

            //this is all guess work as CueTools abstracts it away in to factory classes. Investigation may reveal a better approach. 
            AudioPCMConfig settings = new AudioPCMConfig(sampleBits, channels, sampleRate);
            AudioDecoder audioDecoder = new AudioDecoder(settings); //read the data and decode it in to a 1D array of samples - the buffer seems to want 2D :S
            AudioBuffer audioBuffer = new AudioBuffer(settings, hunksize); //audio buffer to take decoded samples and read them to bytes.
            int read;
            int srcPos = 0;
            int dstPos = 0;
            //this may require some error handling. Hopefully the while condition is reliable
            while (srcPos < compsize)
            {
                if ((read = audioDecoder.DecodeFrame(buff, srcPos, buff.Length)) == 0)
                    break;
                if (audioDecoder.Remaining != 0)
                {
                    audioDecoder.Read(audioBuffer, (int)audioDecoder.Remaining);
                    Array.Copy(audioBuffer.Bytes, 0, cache, dstPos, audioBuffer.ByteLength);
                    dstPos += audioBuffer.ByteLength;
                }
                srcPos += read;
                //Debug.WriteLine($"Read: 0x{read:X} - TotalRead: 0x{srcPos:X} Written: 0x{audioBuffer.ByteLength:X} TotalWritten: 0x{dstPos:X} of HunkSize: 0x{hunksize:X}");
            }

            //Nanook - hack to support 16bit byte flipping - tested passes hunk CRC test
            if (swapEndian)
            {
                byte tmp;
                for (int i = 0; i < hunksize; i += 2)
                {
                    tmp = cache[i];
                    cache[i] = cache[i + 1];
                    cache[i + 1] = tmp;
                }
            }

            return chd_error.CHDERR_NONE;
        }



        /******************* CD decoders **************************/



        private const int CD_MAX_SECTOR_DATA = 2352;
        private const int CD_MAX_SUBCODE_DATA = 96;
        private static readonly int CD_FRAME_SIZE = CD_MAX_SECTOR_DATA + CD_MAX_SUBCODE_DATA;

        private static readonly byte[] s_cd_sync_header = new byte[] { 0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00 };

        internal static chd_error cdzlib(Stream file, int complen, int destlen, byte[] dest)
        {
            /* determine header bytes */
            int frames = destlen / CD_FRAME_SIZE;
            int complen_bytes = (destlen < 65536) ? 2 : 3;
            int ecc_bytes = (frames + 7) / 8;
            int header_bytes = ecc_bytes + complen_bytes;

            byte[] header = new byte[header_bytes];
            file.Read(header, 0, (int)header_bytes);

            /* extract compressed length of base */
            int complen_base = (header[ecc_bytes + 0] << 8) | header[ecc_bytes + 1];
            if (complen_bytes > 2)
                complen_base = (complen_base << 8) | header[ecc_bytes + 2];

            byte[] bSector = new byte[frames * CD_MAX_SECTOR_DATA];
            byte[] bSubcode = new byte[frames * CD_MAX_SUBCODE_DATA];

            long filePos = file.Position;
            chd_error err = zlib(file, complen_base, frames * CD_MAX_SECTOR_DATA, bSector);
            if (err != chd_error.CHDERR_NONE)
                return err;

            file.Seek(filePos + complen_base, SeekOrigin.Begin);
            err = zlib(file, complen - complen_base - header_bytes, frames * CD_MAX_SUBCODE_DATA, bSubcode);
            if (err != chd_error.CHDERR_NONE)
                return err;

            /* reassemble the data */
            for (int framenum = 0; framenum < frames; framenum++)
            {
                Buffer.BlockCopy(bSector, framenum * CD_MAX_SECTOR_DATA, dest, framenum * CD_FRAME_SIZE, CD_MAX_SECTOR_DATA);
                Buffer.BlockCopy(bSubcode, framenum * CD_MAX_SUBCODE_DATA, dest, framenum * CD_FRAME_SIZE + CD_MAX_SECTOR_DATA, CD_MAX_SUBCODE_DATA);

                // reconstitute the ECC data and sync header 
                int sectorStart = framenum * CD_FRAME_SIZE;
                if ((header[framenum / 8] & (1 << (framenum % 8))) != 0)
                {
                    Buffer.BlockCopy(s_cd_sync_header, 0, dest, sectorStart, s_cd_sync_header.Length);
                    cdRom.ecc_generate(dest, sectorStart);
                }
            }
            return chd_error.CHDERR_NONE;
        }


        internal static chd_error cdlzma(Stream file, int complen, int destlen, byte[] dest)
        {
            /* determine header bytes */
            int frames = destlen / CD_FRAME_SIZE;
            int complen_bytes = (destlen < 65536) ? 2 : 3;
            int ecc_bytes = (frames + 7) / 8;
            int header_bytes = ecc_bytes + complen_bytes;

            byte[] header = new byte[header_bytes];
            file.Read(header, 0, header_bytes);

            /* extract compressed length of base */
            int complen_base = ((header[ecc_bytes + 0] << 8) | header[ecc_bytes + 1]);
            if (complen_bytes > 2)
                complen_base = (complen_base << 8) | header[ecc_bytes + 2];

            byte[] bSector = new byte[frames * CD_MAX_SECTOR_DATA];
            byte[] bSubcode = new byte[frames * CD_MAX_SUBCODE_DATA];

            long filePos = file.Position;
            chd_error err = lzma(file, complen_base, frames * CD_MAX_SECTOR_DATA, bSector);
            if (err != chd_error.CHDERR_NONE)
                return err;

            file.Seek(filePos + complen_base, SeekOrigin.Begin);
            err = zlib(file, complen - complen_base - header_bytes, frames * CD_MAX_SUBCODE_DATA, bSubcode);
            if (err != chd_error.CHDERR_NONE)
                return err;

            /* reassemble the data */
            for (int framenum = 0; framenum < frames; framenum++)
            {
                Buffer.BlockCopy(bSector, framenum * CD_MAX_SECTOR_DATA, dest, framenum * CD_FRAME_SIZE, CD_MAX_SECTOR_DATA);
                Buffer.BlockCopy(bSubcode, framenum * CD_MAX_SUBCODE_DATA, dest, framenum * CD_FRAME_SIZE + CD_MAX_SECTOR_DATA, CD_MAX_SUBCODE_DATA);

                // reconstitute the ECC data and sync header 
                int sectorStart = framenum * CD_FRAME_SIZE;
                if ((header[framenum / 8] & (1 << (framenum % 8))) != 0)
                {
                    Buffer.BlockCopy(s_cd_sync_header, 0, dest, sectorStart, s_cd_sync_header.Length);
                    cdRom.ecc_generate(dest, sectorStart);
                }
            }
            return chd_error.CHDERR_NONE;
        }


        internal static chd_error cdflac(Stream file, int complen, int destlen, byte[] dest)
        {
            /* determine header bytes */
            int frames = destlen / CD_FRAME_SIZE;
            int complen_bytes = (destlen < 65536) ? 2 : 3;
            int ecc_bytes = (frames + 7) / 8;
            int header_bytes = ecc_bytes + complen_bytes;

            byte[] header = new byte[header_bytes];
            file.Read(header, 0, header_bytes);

            /* extract compressed length of base */
            int complen_base = ((header[ecc_bytes + 0] << 8) | header[ecc_bytes + 1]);
            if (complen_bytes > 2)
                complen_base = (complen_base << 8) | header[ecc_bytes + 2];

            byte[] bSector = new byte[frames * CD_MAX_SECTOR_DATA];
            byte[] bSubcode = new byte[frames * CD_MAX_SUBCODE_DATA];

            long filePos = file.Position;
            chd_error err = flac(file, complen_base, frames * CD_MAX_SECTOR_DATA, bSector);
            if (err != chd_error.CHDERR_NONE)
                return err;

            file.Seek(filePos + complen_base, SeekOrigin.Begin);
            err = zlib(file, complen - complen_base - header_bytes, frames * CD_MAX_SUBCODE_DATA, bSubcode);
            if (err != chd_error.CHDERR_NONE)
                return err;

            /* reassemble the data */
            for (int framenum = 0; framenum < frames; framenum++)
            {
                Buffer.BlockCopy(bSector, framenum * CD_MAX_SECTOR_DATA, dest, framenum * CD_FRAME_SIZE, CD_MAX_SECTOR_DATA);
                Buffer.BlockCopy(bSubcode, framenum * CD_MAX_SUBCODE_DATA, dest, framenum * CD_FRAME_SIZE + CD_MAX_SECTOR_DATA, CD_MAX_SUBCODE_DATA);

                // reconstitute the ECC data and sync header 
                int sectorStart = framenum * CD_FRAME_SIZE;
                if ((header[framenum / 8] & (1 << (framenum % 8))) != 0)
                {
                    Buffer.BlockCopy(s_cd_sync_header, 0, dest, sectorStart, s_cd_sync_header.Length);
                    cdRom.ecc_generate(dest, sectorStart);
                }
            }
            return chd_error.CHDERR_NONE;
        }
    }

}