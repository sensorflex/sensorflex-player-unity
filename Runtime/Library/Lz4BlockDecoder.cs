// LZ4 raw block decoder.
// Compatible with Apple Compression framework COMPRESSION_LZ4_RAW and
// K4os.Compression.LZ4 LZ4Codec raw block format — both produce the same
// standard LZ4 block bitstream (no frame header, no content checksum).
//
// LZ4 block format (each sequence):
//   [token: 1 byte][extra literal length bytes*][literal bytes][2-byte LE offset][extra match length bytes*]
//   * extra length bytes present only when nibble == 15; each adds 255, last byte adds remainder
//   * The final sequence ends after the literal copy (no offset/match follows)

using System;

namespace SensorFlex.Player.Library
{
    internal static class Lz4BlockDecoder
    {
        // Decodes one LZ4 raw block.
        // Returns the number of bytes written to dst, or -1 on error.
        public static int Decode(
            byte[] src, int srcOffset, int srcLength,
            byte[] dst, int dstOffset, int dstCapacity)
        {
            int sEnd = srcOffset + srcLength;
            int dEnd = dstOffset + dstCapacity;
            int sPos = srcOffset;
            int dPos = dstOffset;

            while (sPos < sEnd)
            {
                if (sPos >= sEnd) break;
                int token = src[sPos++];

                // ── Literal run ───────────────────────────────────────────────
                int litLen = (token >> 4) & 0xF;
                if (litLen == 15)
                {
                    int x;
                    do { x = src[sPos++]; litLen += x; } while (x == 255 && sPos < sEnd);
                }

                if (sPos + litLen > sEnd || dPos + litLen > dEnd) return -1;
                Buffer.BlockCopy(src, sPos, dst, dPos, litLen);
                sPos += litLen;
                dPos += litLen;

                // Last sequence: no match section follows the final literal run.
                if (sPos >= sEnd) break;

                // ── Match copy ────────────────────────────────────────────────
                if (sPos + 2 > sEnd) return -1;
                int offset = src[sPos] | (src[sPos + 1] << 8);
                sPos += 2;
                if (offset == 0) return -1;

                int matchLen = (token & 0xF) + 4;   // minimum match length = 4
                if ((token & 0xF) == 15)
                {
                    int x;
                    do { x = src[sPos++]; matchLen += x; } while (x == 255 && sPos < sEnd);
                }

                int matchSrc = dPos - offset;
                if (matchSrc < dstOffset || dPos + matchLen > dEnd) return -1;

                // Byte-by-byte required: overlap is valid (encodes run-length repeats).
                for (int i = 0; i < matchLen; i++)
                    dst[dPos + i] = dst[matchSrc + i];
                dPos += matchLen;
            }

            return dPos - dstOffset;
        }
    }
}
