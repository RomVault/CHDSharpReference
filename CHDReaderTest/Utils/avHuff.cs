using CHDReaderTest;
using CHDReaderTest.Utils;
using System;

internal static class avHuff
{
   
    internal static chd_error DecodeData(byte[] source, uint complength, ref byte[] dest)
    {
        // extract info from the header
        if (complength < 8)
            return chd_error.CHDERR_INVALID_DATA;
        uint metasize = source[0];
        uint channels = source[1];
        uint samples = (uint)(source[2] << 8) + source[3];
        uint width = (uint)(source[4] << 8) + source[5];
        uint height = (uint)(source[6] << 8) + source[7];

        // validate that the sizes make sense
        if (complength < 10 + 2 * channels)
            return chd_error.CHDERR_INVALID_DATA;
        uint totalsize = 10 + 2 * channels;
        uint treesize = (uint)(source[8] << 8) | source[9];
        if (treesize != 0xffff)
            totalsize += treesize;
        for (int chnum = 0; chnum < channels; chnum++)
            totalsize += (uint)(source[10 + 2 * chnum] << 8) | source[11 + 2 * chnum];
        if (totalsize >= complength)
            return chd_error.CHDERR_INVALID_DATA;
        // starting offsets
        uint srcoffs = 10 + 2 * channels;


        uint destOffset = 0;
        // create a header
        dest[0] = (byte)'c';
        dest[1] = (byte)'h';
        dest[2] = (byte)'a';
        dest[3] = (byte)'v';
        dest[4] = (byte)metasize;
        dest[5] = (byte)channels;
        dest[6] = (byte)(samples >> 8);
        dest[7] = (byte)samples;
        dest[8] = (byte)(width >> 8);
        dest[9] = (byte)width;
        dest[10] = (byte)(height >> 8);
        dest[11] = (byte)height;
        destOffset += 12;



        // determine the start of each piece of data
        uint metastart = destOffset;
        destOffset += metasize;

        uint?[] audiostart = new uint?[16];
        for (int chnum = 0; chnum < channels; chnum++)
        {
            audiostart[chnum] = destOffset;
            destOffset += 2 * samples;
        }
        uint videostart = destOffset;



        if (metasize > 0)
        {
            if (metastart != null)
                Buffer.BlockCopy(source, (int)srcoffs, dest, (int)metastart, (int)metasize);
            srcoffs += metasize;
        }

        // decode the audio channels
        if (channels > 0)
        {
            // decode the audio
            chd_error err = DecodeAudio(channels, samples, source, srcoffs, dest, audiostart, 8);
            if (err != chd_error.CHDERR_NONE)
                return err;

            // advance the pointers past the data
            treesize = (uint)(source[8] << 8) + source[9];
            if (treesize != 0xffff)
                srcoffs += treesize;
            for (int chnum = 0; chnum < channels; chnum++)
                srcoffs += (uint)(source[10 + 2 * chnum] << 8) + source[11 + 2 * chnum];
        }

        // decode the video data
        if (width > 0 && height > 0 && videostart != null)
        {
            uint videostride = 2 * width;
            // decode the video
            chd_error err = decodeVideo(width, height, source, srcoffs, complength - srcoffs, dest, videostart, videostride);
            if (err != chd_error.CHDERR_NONE)
                return err;
        }
        return chd_error.CHDERR_NONE;
    }

  
    private static chd_error DecodeAudio(uint channels, uint samples, byte[] source, uint srcOffs, byte[] dest, uint?[] destIndex, uint sizes)
    {
        // extract the huffman trees
        ushort treesize = (ushort)((source[sizes + 0] << 8) | source[sizes + 1]);

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
            // extract the size of this channel
            uint size = (uint)(source[sizes + chnum * 2 + 2] << 8) | source[sizes + chnum * 2 + 3];

            // only process if the data is requested
            uint? curdest = destIndex[chnum];
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
            srcOffs += size;
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


