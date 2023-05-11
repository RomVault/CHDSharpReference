//using CUETools.Codecs;
//using CUETools.Codecs.Flake;
//using System;
//using System.Collections.Generic;
//using System.Diagnostics;
//using System.IO;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;
//using System.Xml.Schema;

//namespace CHDReaderTest
//{
//    internal class Flac
//    {
//        internal static  byte[] _header = new byte[]
//        {
//			0x66, 0x4C, 0x61, 0x43,                         /* +00: 'fLaC' stream header */
//			0x80,                                           /* +04: metadata block type 0 (STREAMINFO), */
//															/*      flagged as last block */
//			0x00, 0x00, 0x22,                               /* +05: metadata block length = 0x22 */
//			0x00, 0x00,                                     /* +08: minimum block size */
//			0x00, 0x00,                                     /* +0A: maximum block size */
//			0x00, 0x00, 0x00,                               /* +0C: minimum frame size (0 == unknown) */
//			0x00, 0x00, 0x00,                               /* +0F: maximum frame size (0 == unknown) */
//			0x0A, 0xC4, 0x42, 0xF0, 0x00, 0x00, 0x00, 0x00, /* +12: sample rate (0x0ac44 == 44100), */
//															/*      numchannels (2), sample bits (16), */
//															/*      samples in stream (0 == unknown) */
//			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, /* +1A: MD5 signature (0 == none) */
//			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00  /* +2A: start of stream data */
//        };

//		public static void Decode(Stream stream, int length, int hunksize, ref byte[] cache)
//		{
//            byte endianType = (byte)stream.ReadByte();
//            length--;
//            //not sure if this is required. It's from libchdr. DR_Flac needed to know if the endian needed to be swapped.
//            //This single byte indicates the status. It's part of the CHD format flac header. It's not used by the flac decoder - it's skipped
//            bool swapEndian = ((endianType == 'L' && !BitConverter.IsLittleEndian) || (endianType == 'B' && BitConverter.IsLittleEndian)); //'L'ittle / 'B'ig
//            if (swapEndian)
//                throw new Exception("Found an image that needs the Endian swapping. Test this is accurate then remove this exception");

//            byte[] compbytes = new byte[_header.Length + length];
//            stream.Read(compbytes, _header.Length, length);
//            Array.Copy(_header, compbytes, _header.Length);

//            int bites_per_sample = 16;
//			int sample_rate = 44100;
//			int num_channels = 2;
//			int block_size = 2352 / 4; //determine FLAC block size, which must be 16-65535 - clamp to 2k since that's supposed to be the sweet spot
//            if (block_size > 2048)
//				block_size /= 2;

//			int samples = hunksize / (bites_per_sample * num_channels);

//            compbytes[0x08] = compbytes[0x0a] = (byte)((block_size * num_channels) >> 8);
//            compbytes[0x09] = compbytes[0x0b] = (byte)((block_size * num_channels) & 0xff);
//            compbytes[0x12] = (byte)(sample_rate >> 12);
//            compbytes[0x13] = (byte)(sample_rate >> 4);
//            compbytes[0x14] = (byte)((sample_rate << 4) | ((num_channels - 1) << 1));
//			compbytes[0x15] |= (byte)(bites_per_sample - 1);
//            compbytes[0x16] = (byte)(samples >> 24);
//            compbytes[0x17] = (byte)(samples >> 16);
//            compbytes[0x18] = (byte)(samples >> 8);
//            compbytes[0x19] = (byte)samples;


//            cache = new byte[0x8000];

//            AudioPCMConfig settings = new AudioPCMConfig(bites_per_sample, num_channels, sample_rate);
//            AudioBuffer buff = new AudioBuffer(settings, hunksize);
//            DecoderSettings s = new DecoderSettings();
//			int read;

//            using (MemoryStream ms = new MemoryStream(compbytes))
//			{
//				AudioDecoder dec2 = new AudioDecoder(s, null, ms);

//				int pos = _header.Length;
//				while (pos < compbytes.Length && (read = dec2.DecodeFrame(compbytes, pos, compbytes.Length)) != 0)
//				{
//                    pos += read;
//                    Trace.WriteLine($"Read: {read} - Total: {pos}");
//				}
//			}
//        }

//        public static void Decode2(Stream stream, int length, int hunksize, ref byte[] cache)
//        {
//            byte endianType = (byte)stream.ReadByte();
//            length--; //remove the endianType char

//            //not sure if this is required. It's from libchdr. DR_Flac needed to know if the endian needed to be swapped.
//            //This single byte indicates the status. It's part of the CHD format flac header. It's not used by the flac decoder - it's skipped
//            bool swapEndian = ((endianType == 'L' && !BitConverter.IsLittleEndian) || (endianType == 'B' && BitConverter.IsLittleEndian)); //'L'ittle / 'B'ig
//            if (swapEndian)
//                throw new Exception("Found an image that needs the Endian swapping. Test this is accurate then remove this exception");


//            byte[] data = new byte[length];
//            stream.Read(data, 0, length);

//            int bites_per_sample = 16;
//            int sample_rate = 44100;
//            int num_channels = 2;
//            int block_size = 2352 / 4; //determine FLAC block size, which must be 16-65535 - clamp to 2k since that's supposed to be the sweet spot
//            if (block_size > 2048)
//                block_size /= 2;

//            int samples = hunksize / (bites_per_sample * num_channels);

//            cache = new byte[0x8000];

//            AudioPCMConfig settings = new AudioPCMConfig(bites_per_sample, num_channels, sample_rate);
//            int read;

//            AudioDecoder dec2 = new AudioDecoder(settings);
//            AudioBuffer buff = new AudioBuffer(settings, 0x1000);
//            int pos = 0;
//            while (pos < data.Length && (read = dec2.DecodeFrame(data, pos, data.Length)) != 0)
//            {
//                dec2.Read(buff, (int)dec2.Remaining + 1);
//                pos += read;
//                Trace.WriteLine($"Read: {read} - Total: {pos}");

//            }
//        }
//    }
//}
