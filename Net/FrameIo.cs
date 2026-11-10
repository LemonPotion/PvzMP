using System;
using System.IO;

namespace PvzMP.Net
{
    public sealed class FrameException : Exception
    {
        public FrameException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// The [4-byte big-endian body length][body] half of the wire, split out because everything that
    /// is hard about framing - short reads, an impossible length, a stream that ends mid-frame -
    /// can be tested here without a socket or a game.
    /// </summary>
    public static class FrameIo
    {
        public const int HeaderBytes = 4;

        public static void Write(Stream stream, byte[] frame)
        {
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }

        /// <summary>
        /// Returns the decoded frame, or null when the peer closed the connection between frames.
        /// Anything the stream cannot resync from is an error: guessing where the next frame starts
        /// is how a desynchronised connection turns into silently wrong gameplay.
        /// </summary>
        public static DecodedFrame Read(Stream stream)
        {
            byte[] header = ReadExact(stream, HeaderBytes, false);
            if (header == null)
            {
                return null;
            }

            int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            if (length < 0)
            {
                throw new FrameException("frame length " + length + " is not a positive size");
            }

            if (length > Protocol.MaxFrameBytes)
            {
                throw new FrameException("frame length " + length + " exceeds the "
                    + Protocol.MaxFrameBytes + " byte cap");
            }

            if (length < Protocol.BodyOverhead)
            {
                throw new FrameException("frame length " + length + " is too small to hold a message");
            }

            byte[] body = ReadExact(stream, length, true);
            if (body == null)
            {
                throw new FrameException("stream ended with " + length + " body bytes promised");
            }

            return Protocol.Decode(body, 0, body.Length);
        }

        private static byte[] ReadExact(Stream stream, int count, bool partialIsError)
        {
            byte[] buffer = new byte[count];
            int filled = 0;
            while (filled < count)
            {
                int read = stream.Read(buffer, filled, count - filled);
                if (read <= 0)
                {
                    if (filled == 0 && !partialIsError)
                    {
                        return null;
                    }

                    throw new FrameException("stream ended after " + filled + " of " + count + " bytes");
                }

                filled += read;
            }

            return buffer;
        }
    }
}
