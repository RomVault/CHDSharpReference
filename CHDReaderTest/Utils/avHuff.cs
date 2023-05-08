using CHDReaderTest;
using CHDReaderTest.Utils;
using System;
using System.Runtime.CompilerServices;


//**************************************************************************
//  AVHUFF DECODER
//**************************************************************************

/**
 * @fn  avhuff_decoder::avhuff_decoder()
 *
 * @brief   -------------------------------------------------
 *            avhuff_decoder - constructor
 *          -------------------------------------------------.
 */

public static class avHuff
{
    /**
     * @fn  avhuff_error avhuff_decoder::decode_data(const uint8_t *source, uint32_t complength, uint8_t *dest)
     *
     * @brief   -------------------------------------------------
     *            decode_data - decode both audio and video from a raw data stream
     *          -------------------------------------------------.
     *
     * @param   source          Source for the.
     * @param   complength      The complength.
     * @param [in,out]  dest    If non-null, destination for the.
     *
     * @return  An avhuff_error.
     */
    internal unsafe static chd_error decode_data(byte[] sourceIn, uint complength, ref byte[] destout)
    {
        fixed (byte* pIn = sourceIn)
        fixed (byte* pOut = destout)
        {
            byte* source = pIn;
            byte* dest = pOut;

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
            // if we are decoding raw, set up the output parameters
            //uint8_t* metastart, *videostart, *audiostart[16];
            byte* metastart = null;
            byte* videostart = null;
            byte*[] audiostart = new byte*[16];


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
            dest += 12;

            // determine the start of each piece of data
            metastart = dest;
            dest += metasize;
            for (int chnum = 0; chnum < channels; chnum++)
            {
                audiostart[chnum] = dest;
                dest += 2 * samples;
            }
            videostart = dest;

            uint videostride = 2 * width;
            if (metasize > 0)
            {
                if (metastart != null)
                    memcpy(metastart, source + srcoffs, metasize);
                srcoffs += metasize;
            }

            // decode the audio channels
            if (channels > 0)
            {
                // decode the audio
                chd_error err = decode_audio(channels, samples, source + srcoffs, audiostart, source + 8);
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
                // decode the video
                chd_error err = decode_video(width, height, source + srcoffs, complength - srcoffs, videostart, videostride);
                if (err != chd_error.CHDERR_NONE)
                    return err;
            }
            return chd_error.CHDERR_NONE;
        }
    }

    private static unsafe void memcpy(byte* bOut, byte* bIn, uint Len)
    {
        for (int i = 0; i < Len; i++)
            bOut[i] = bIn[i];
    }

    /**
     * @fn  avhuff_error avhuff_decoder::decode_audio(int channels, int samples, const uint8_t *source, uint8_t **dest, uint32_t dxor, const uint8_t *sizes)
     *
     * @brief   -------------------------------------------------
     *            decode_audio - decode audio from a compressed data stream
     *          -------------------------------------------------.
     *
     * @exception   CHDERR_DECOMPRESSION_ERROR  Thrown when a chderr decompression error error
     *                                          condition occurs.
     *
     * @param   channels        The channels.
     * @param   samples         The samples.
     * @param   source          Source for the.
     * @param [in,out]  dest    If non-null, destination for the.
     * @param   dxor            The dxor.
     * @param   sizes           The sizes.
     *
     * @return  An avhuff_error.
     */

    private static unsafe chd_error decode_audio(uint channels, uint samples, byte* source, byte*[] dest, byte* sizes)
    {
        // extract the huffman trees
        ushort treesize = (ushort)((sizes[0] << 8) | sizes[1]);

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
        BitStream bitbuf = new BitStream(source, (int)treesize);
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
            source += treesize;
        }

        // loop over channels
        for (int chnum = 0; chnum < channels; chnum++)
        {
            // extract the size of this channel
            uint size = (uint)(sizes[chnum * 2 + 2] << 8) | sizes[chnum * 2 + 3];

            // only process if the data is requested
            byte* curdest = dest[chnum];
            if (curdest != null)
            {
                int prevsample = 0;

                // if no huffman length, just copy the data
                if (treesize == 0)
                {
                    byte* cursource = source;
                    for (int sampnum = 0; sampnum < samples; sampnum++)
                    {
                        int delta = (cursource[0] << 8) | cursource[1];
                        cursource += 2;

                        int newsample = prevsample + delta;
                        prevsample = newsample;

                        curdest[0] = (byte)(newsample >> 8);
                        curdest[1] = (byte)newsample;
                        curdest += 2;
                    }
                }

                // otherwise, Huffman-decode the data
                else
                {
                    bitbuf = new BitStream(source, (int)size);
                    m_audiohi_decoder.AssignBitStream(bitbuf);
                    m_audiolo_decoder.AssignBitStream(bitbuf);
                    for (int sampnum = 0; sampnum < samples; sampnum++)
                    {
                        short delta = (short)(m_audiohi_decoder.DecodeOne() << 8);
                        delta |= (short)m_audiolo_decoder.DecodeOne();

                        int newsample = prevsample + delta;
                        prevsample = newsample;

                        curdest[0] = (byte)(newsample >> 8);
                        curdest[1] = (byte)newsample;
                        curdest += 2;
                    }
                    if (bitbuf.overflow())
                        return chd_error.CHDERR_INVALID_DATA;
                }
            }

            // advance to the next channel's data
            source += size;
        }
        return chd_error.CHDERR_NONE;
    }

    /**
     * @fn  avhuff_error avhuff_decoder::decode_video(int width, int height, const uint8_t *source, uint32_t complength, uint8_t *dest, uint32_t dstride, uint32_t dxor)
     *
     * @brief   -------------------------------------------------
     *            decode_video - decode video from a compressed data stream
     *          -------------------------------------------------.
     *
     * @param   width           The width.
     * @param   height          The height.
     * @param   source          Source for the.
     * @param   complength      The complength.
     * @param [in,out]  dest    If non-null, destination for the.
     * @param   dstride         The dstride.
     * @param   dxor            The dxor.
     *
     * @return  An avhuff_error.
     */

    private static unsafe chd_error decode_video(uint width, uint height, byte* source, uint complength, byte* dest, uint dstride)
    {
        // if the high bit of the first byte is set, we decode losslessly
        if ((source[0] & 0x80) != 0)
            return decode_video_lossless(width, height, source, complength, dest, dstride);
        else
            return chd_error.CHDERR_INVALID_DATA;
    }

    /**
     * @fn  avhuff_error avhuff_decoder::decode_video_lossless(int width, int height, const uint8_t *source, uint32_t complength, uint8_t *dest, uint32_t dstride, uint32_t dxor)
     *
     * @brief   -------------------------------------------------
     *            decode_video_lossless - do a lossless video decoding using deltas and huffman
     *            encoding
     *          -------------------------------------------------.
     *
     * @param   width           The width.
     * @param   height          The height.
     * @param   source          Source for the.
     * @param   complength      The complength.
     * @param [in,out]  dest    If non-null, destination for the.
     * @param   dstride         The dstride.
     * @param   dxor            The dxor.
     *
     * @return  An avhuff_error.
     */

    private static unsafe chd_error decode_video_lossless(uint width, uint height, byte* source, uint complength, byte* dest, uint dstride)
    {
        // skip the first byte
        BitStream bitbuf = new BitStream(source, (int)complength);
        bitbuf.read(8);

        AVHuffmanDecoder m_ycontext = new AVHuffmanDecoder(256 + 16, 16, bitbuf);
        AVHuffmanDecoder m_cbcontext = new AVHuffmanDecoder(256 + 16, 16, bitbuf);
        AVHuffmanDecoder m_crcontext = new AVHuffmanDecoder(256 + 16, 16, bitbuf);

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
        m_ycontext.reset();
        m_cbcontext.reset();
        m_crcontext.reset();

        for (int dy = 0; dy < height; dy++)
        {
            byte* row = dest + dy * dstride;
            for (int dx = 0; dx < width / 2; dx++)
            {
                row[0] = (byte)m_ycontext.DecodeOne();
                row[1] = (byte)m_cbcontext.DecodeOne();
                row[2] = (byte)m_ycontext.DecodeOne();
                row[3] = (byte)m_crcontext.DecodeOne();
                row += 4;
            }
            m_ycontext.flush_rle();
            m_cbcontext.flush_rle();
            m_crcontext.flush_rle();
        }

        // check for errors if we overflowed or decoded too little data
        if (bitbuf.overflow() || bitbuf.flush() != complength)
            return chd_error.CHDERR_INVALID_DATA;
        return chd_error.CHDERR_NONE;
    }

    private class AVHuffmanDecoder : HuffmanDecoder
    {
        private int rlecount = 0;
        private uint prevdata = 0;

        public AVHuffmanDecoder(uint numcodes, byte maxbits, BitStream bitbuf) : base(numcodes, maxbits, bitbuf)
        { }

        public void reset()
        {
            rlecount = 0;
            prevdata = 0;
        }
        public void flush_rle()
        {
            rlecount = 0;
        }

        public uint DecodeOne()
        {
            // return RLE data if we still have some
            if (rlecount != 0)
            {
                rlecount--;
                return prevdata;
            }

            // fetch the data and process
            uint data = base.DecodeOne();
            if (data < 0x100)
            {
                prevdata += data;
                return prevdata;
            }
            else
            {
                rlecount = code_to_rlecount((int)data);
                rlecount--;
                return prevdata;
            }
        }

        public int code_to_rlecount(int code)
        {
            if (code == 0x00)
                return 1;
            if (code <= 0x107)
                return 8 + (code - 0x100);
            return 16 << (code - 0x108);
        }
    }

}


