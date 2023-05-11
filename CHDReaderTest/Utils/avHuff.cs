using CHDReaderTest;
using CHDReaderTest.Utils;
using System;

internal static class avHuff
{
    /*
     Source input buffer structure:

     Header:
     00     =  Size of the Meta Data to be put into the output buffer right after the header.
     01     =  Number of Audio Channel.
     02,03  =  Number of Audio sampled values per chunk.
     04,05  =  width in pixels of image.
     06,07  =  height in pixels of image.
     08,09  =  Size of the source data for the audio channels huffman trees. (set to 0xffff is using FLAC.)

     10,11  =  size of compressed audio channel 1
     12,13  =  size of compressed audio channel 2
     .
     .         (Max audio channels coded to 16)
     Total Header size = 10 + 2 * Number of Audio Channels.


     Meta Data: (Size from header 00)
     
     Audio Huffman Tree: (Size from header 08,09)

     Audio Compressed Data Channels: (Repeated for each Audio Channel, Size from Header starting at 10,11)

     Video Compressed Data:   Rest of Input Chuck.

    */

    internal static chd_error DecodeData(byte[] source, uint complen, byte[] dest)
    {
        // extract info from the header
        if (complen < 8)
            return chd_error.CHDERR_INVALID_DATA;
        uint metaDataLength = source[0];
        uint audioChannels = source[1];
        uint audioSamplesPerHunk = source.ReadUInt16BE(2);
        uint videoWidth = source.ReadUInt16BE(4);
        uint videoHeight = source.ReadUInt16BE(6);

        uint sourceTotalSize = 10 + 2 * audioChannels;
        // validate that the sizes make sense
        if (complen < sourceTotalSize)
            return chd_error.CHDERR_INVALID_DATA;

        sourceTotalSize += metaDataLength;

        uint audioHuffmanTreeSize = source.ReadUInt16BE(8);
        if (audioHuffmanTreeSize != 0xffff)
            sourceTotalSize += audioHuffmanTreeSize;

        uint?[] audioChannelCompressedSize = new uint?[16];
        for (int chnum = 0; chnum < audioChannels; chnum++)
        {
            audioChannelCompressedSize[chnum] = source.ReadUInt16BE(10 + 2 * chnum);
            sourceTotalSize += (uint)audioChannelCompressedSize[chnum];
        }

        if (sourceTotalSize >= complen)
            return chd_error.CHDERR_INVALID_DATA;

        // starting offsets of source data
        uint srcOffset = 10 + 2 * audioChannels;


        uint destOffset = 0;
        // create a header
        dest[0] = (byte)'c';
        dest[1] = (byte)'h';
        dest[2] = (byte)'a';
        dest[3] = (byte)'v';
        dest[4] = (byte)metaDataLength;
        dest[5] = (byte)audioChannels;
        dest[6] = (byte)(audioSamplesPerHunk >> 8);
        dest[7] = (byte)audioSamplesPerHunk;
        dest[8] = (byte)(videoWidth >> 8);
        dest[9] = (byte)videoWidth;
        dest[10] = (byte)(videoHeight >> 8);
        dest[11] = (byte)videoHeight;
        destOffset += 12;



        uint metaDestStart = destOffset;
        if (metaDataLength > 0)
        {
            Buffer.BlockCopy(source, (int)srcOffset, dest, (int)metaDestStart, (int)metaDataLength);
            srcOffset += metaDataLength;
            destOffset += metaDataLength;
        }

        uint?[] audioChannelDestStart = new uint?[16];
        for (int chnum = 0; chnum < audioChannels; chnum++)
        {
            audioChannelDestStart[chnum] = destOffset;
            destOffset += 2 * audioSamplesPerHunk;
        }
        uint videoDestStart = destOffset;


        // decode the audio channels
        if (audioChannels > 0)
        {
            // decode the audio
            chd_error err = DecodeAudio(audioChannels, audioSamplesPerHunk, source, srcOffset, audioHuffmanTreeSize, audioChannelCompressedSize, dest, audioChannelDestStart);
            if (err != chd_error.CHDERR_NONE)
                return err;

            // advance the pointers past the data
            if (audioHuffmanTreeSize != 0xffff)
                srcOffset += audioHuffmanTreeSize;
            for (int chnum = 0; chnum < audioChannels; chnum++)
                srcOffset += (uint)audioChannelCompressedSize[chnum];
        }

        // decode the video data
        if (videoWidth > 0 && videoHeight > 0)
        {
            uint videostride = 2 * videoWidth;
            // decode the video
            chd_error err = decodeVideo(videoWidth, videoHeight, source, srcOffset, complen - srcOffset, dest, videoDestStart, videostride);
            if (err != chd_error.CHDERR_NONE)
                return err;
        }

        uint videoEnd = videoDestStart + videoWidth * videoHeight * 2;
        for (uint index = videoEnd; index < dest.Length; index++)
            dest[index] = 0;

        return chd_error.CHDERR_NONE;
    }


    private static chd_error DecodeAudio(uint channels, uint samples, byte[] source, uint srcOffs, uint treesize, uint?[] audioChannelCompressedSize, byte[] dest, uint?[] audioChannelDestStart)
    {

#if AVHUFF_USE_FLAC

	// if the tree size is 0xffff, the streams are FLAC-encoded
	if (treesize == 0xffff)
	{
		// output data is big-endian; determine our platform endianness
		uint16_t be_test = 0;
		*(uint8_t *)&be_test = 1;
		bool swap_endian = (be_test == 1);
		if (dxor != 0)
			swap_endian = !swap_endian;

		// loop over channels
		for (int chnum = 0; chnum < channels; chnum++)
		{
			// extract the size of this channel
			uint16_t size = (sizes[chnum * 2 + 2] << 8) | sizes[chnum * 2 + 3];

			// only process if the data is requested
			uint8_t *curdest = dest[chnum];
			if (curdest != nullptr)
			{
				// reset and decode
				if (!m_flac_decoder.reset(48000, 1, samples, source, size))
					throw std::error_condition(chd_file::error::DECOMPRESSION_ERROR);
				if (!m_flac_decoder.decode_interleaved(reinterpret_cast<int16_t *>(curdest), samples, swap_endian))
					throw std::error_condition(chd_file::error::DECOMPRESSION_ERROR);

				// finish up
				m_flac_decoder.finish();
			}

			// advance to the next channel's data
			source += size;
		}
		return AVHERR_NONE;
	}

#endif
        BitStream bitbuf = new BitStream(source, (int)srcOffs);
        HuffmanDecoder m_audiohi_decoder = new HuffmanDecoder(256, 16, bitbuf);
        HuffmanDecoder m_audiolo_decoder = new HuffmanDecoder(256, 16, bitbuf);

        // if we have a non-zero tree size, extract the trees
        if (treesize != 0)
        {
            huffman_error hufferr = m_audiohi_decoder.ImportTreeRLE();
            if (hufferr != huffman_error.HUFFERR_NONE)
                return chd_error.CHDERR_INVALID_DATA;
            bitbuf.flush();
            hufferr = m_audiolo_decoder.ImportTreeRLE();
            if (hufferr != huffman_error.HUFFERR_NONE)
                return chd_error.CHDERR_INVALID_DATA;
            if (bitbuf.flush() != treesize)
                return chd_error.CHDERR_INVALID_DATA;
            srcOffs += treesize;
        }

        // loop over channels
        for (int chnum = 0; chnum < channels; chnum++)
        {
            // only process if the data is requested
            uint? curdest = audioChannelDestStart[chnum];
            if (curdest != null)
            {
                int prevsample = 0;

                // if no huffman length, just copy the data
                if (treesize == 0)
                {
                    uint cursource = srcOffs;
                    for (int sampnum = 0; sampnum < samples; sampnum++)
                    {
                        int delta = (source[cursource + 0] << 8) | source[cursource + 1];
                        cursource += 2;

                        int newsample = prevsample + delta;
                        prevsample = newsample;

                        dest[(uint)curdest + 0] = (byte)(newsample >> 8);
                        dest[(uint)curdest + 1] = (byte)newsample;
                        curdest += 2;
                    }
                }

                // otherwise, Huffman-decode the data
                else
                {
                    bitbuf = new BitStream(source, (int)srcOffs);
                    m_audiohi_decoder.AssignBitStream(bitbuf);
                    m_audiolo_decoder.AssignBitStream(bitbuf);
                    for (int sampnum = 0; sampnum < samples; sampnum++)
                    {
                        short delta = (short)(m_audiohi_decoder.DecodeOne() << 8);
                        delta |= (short)m_audiolo_decoder.DecodeOne();

                        int newsample = prevsample + delta;
                        prevsample = newsample;

                        dest[(uint)curdest + 0] = (byte)(newsample >> 8);
                        dest[(uint)curdest + 1] = (byte)newsample;
                        curdest += 2;
                    }
                    if (bitbuf.overflow())
                        return chd_error.CHDERR_INVALID_DATA;
                }
            }

            // advance to the next channel's data
            srcOffs += (uint)audioChannelCompressedSize[chnum];
        }
        return chd_error.CHDERR_NONE;
    }



    private static chd_error decodeVideo(uint width, uint height, byte[] source, uint sourceOffset, uint complength, byte[] dest, uint destOffset, uint dstride)
    {
        // if the high bit of the first byte is set, we decode losslessly
        if ((source[sourceOffset] & 0x80) != 0)
            return DecodeVideoLossless(width, height, source, sourceOffset, complength, dest, destOffset, dstride);
        else
            return chd_error.CHDERR_INVALID_DATA;
    }



    private static chd_error DecodeVideoLossless(uint width, uint height, byte[] source, uint sourceOffset, uint complength, byte[] dest, uint destOffset, uint dstride)
    {
        // skip the first byte
        BitStream bitbuf = new BitStream(source, (int)sourceOffset);
        bitbuf.read(8);

        HuffmanDecoderRLE m_ycontext = new HuffmanDecoderRLE(256 + 16, 16, bitbuf);
        HuffmanDecoderRLE m_cbcontext = new HuffmanDecoderRLE(256 + 16, 16, bitbuf);
        HuffmanDecoderRLE m_crcontext = new HuffmanDecoderRLE(256 + 16, 16, bitbuf);

        // import the tables
        huffman_error hufferr = m_ycontext.ImportTreeRLE();
        if (hufferr != huffman_error.HUFFERR_NONE)
            return chd_error.CHDERR_INVALID_DATA;
        bitbuf.flush();
        hufferr = m_cbcontext.ImportTreeRLE();
        if (hufferr != huffman_error.HUFFERR_NONE)
            return chd_error.CHDERR_INVALID_DATA;
        bitbuf.flush();
        hufferr = m_crcontext.ImportTreeRLE();
        if (hufferr != huffman_error.HUFFERR_NONE)
            return chd_error.CHDERR_INVALID_DATA;
        bitbuf.flush();

        // decode to the destination
        m_ycontext.Reset();
        m_cbcontext.Reset();
        m_crcontext.Reset();

        for (int dy = 0; dy < height; dy++)
        {
            uint row = destOffset + (uint)dy * dstride;
            for (int dx = 0; dx < width / 2; dx++)
            {
                dest[row + 0] = (byte)m_ycontext.DecodeOne();
                dest[row + 1] = (byte)m_cbcontext.DecodeOne();
                dest[row + 2] = (byte)m_ycontext.DecodeOne();
                dest[row + 3] = (byte)m_crcontext.DecodeOne();
                row += 4;
            }
            m_ycontext.FlushRLE();
            m_cbcontext.FlushRLE();
            m_crcontext.FlushRLE();
        }

        // check for errors if we overflowed or decoded too little data
        if (bitbuf.overflow() || bitbuf.flush() != complength)
            return chd_error.CHDERR_INVALID_DATA;
        return chd_error.CHDERR_NONE;
    }



}


