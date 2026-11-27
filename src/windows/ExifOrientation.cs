// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using System.Buffers.Binary;

namespace NopeView.App;

/// <summary>
/// Reads the EXIF orientation tag of a still image, so the pixels can be turned
/// the way the camera intended before they reach the screen. JPEG, PNG, WebP
/// and TIFF carry the tag in a TIFF block; HEIC and AVIF keep theirs in the
/// container, and libheif and libjxl already apply it while decoding.
/// </summary>
internal static class ExifOrientation
{
    // EXIF orientation values, as the tag defines them.
    internal const int Normal = 1;
    internal const int FlipHorizontal = 2;
    internal const int Rotate180 = 3;
    internal const int FlipVertical = 4;
    internal const int Transpose = 5;
    internal const int Rotate90 = 6;
    internal const int Transverse = 7;
    internal const int Rotate270 = 8;

    // The orientation block sits in the first APP1 segment of a JPEG, which is
    // capped at 64 KB. Reading this much header covers every format here.
    private const int HeaderBytes = 256 * 1024;

    // Every animation frame asks again for the same file, so the last answer
    // stays. Guarded because decodes and prefetches read from two threads.
    private static readonly object Gate = new();
    private static string? _cachedPath;
    private static int _cachedValue = Normal;

    /// <summary>
    /// Orientation of the image at path, or <see cref="Normal"/> when it carries
    /// none or the file cannot be read.
    /// </summary>
    internal static int Read(string path)
    {
        lock (Gate)
        {
            if (_cachedPath is not null &&
                string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedValue;
            }
        }

        var value = ReadFromFile(path);

        lock (Gate)
        {
            _cachedPath = path;
            _cachedValue = value;
        }

        return value;
    }

    /// <summary>
    /// True when the orientation turns the image by a quarter turn, which swaps
    /// its width and height.
    /// </summary>
    internal static bool SwapsAxes(int orientation) =>
        orientation is Transpose or Rotate90 or Transverse or Rotate270;

    private static int ReadFromFile(string path)
    {
        var header = ReadHeader(path);
        if (header is null)
        {
            return Normal;
        }

        if (IsJpeg(header))
        {
            return ReadJpegOrientation(header);
        }
        if (IsPng(header))
        {
            return ReadPngOrientation(header);
        }
        if (IsWebp(header))
        {
            return ReadWebpOrientation(header);
        }

        // TIFF and every other format here start with the TIFF header itself.
        return ReadTiffOrientation(header, 0);
    }

    private static byte[]? ReadHeader(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(stream.Length, HeaderBytes);
            if (length < 8)
            {
                return null;
            }

            var buffer = new byte[length];
            var read = 0;
            while (read < length)
            {
                var count = stream.Read(buffer, read, length - read);
                if (count <= 0)
                {
                    break;
                }
                read += count;
            }

            return read == length ? buffer : buffer[..read];
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsJpeg(byte[] header) =>
        header.Length >= 2 && header[0] == 0xFF && header[1] == 0xD8;

    private static bool IsPng(byte[] header) =>
        header.Length >= 8 &&
        header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47;

    private static bool IsWebp(byte[] header) =>
        header.Length >= 12 &&
        header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
        header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50;

    /// <summary>
    /// Walks the JPEG segments up to the first scan, looking for the APP1
    /// segment that starts with "Exif\0\0".
    /// </summary>
    private static int ReadJpegOrientation(byte[] header)
    {
        var position = 2;
        while (position + 4 <= header.Length)
        {
            if (header[position] != 0xFF)
            {
                return Normal;
            }

            var marker = header[position + 1];
            if (marker == 0xFF)
            {
                // A fill byte, not a marker; the next byte may be the marker.
                position++;
                continue;
            }

            // Standalone markers carry no length.
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                position += 2;
                continue;
            }

            // The first scan starts here; metadata segments are behind us.
            if (marker == 0xDA)
            {
                return Normal;
            }

            if (position + 4 > header.Length)
            {
                return Normal;
            }

            var length = (header[position + 2] << 8) | header[position + 3];
            if (length < 2)
            {
                return Normal;
            }
            var payload = position + 4;
            if (payload + length - 2 > header.Length)
            {
                return Normal;
            }

            if (marker == 0xE1 && length >= 8 &&
                header[payload] == (byte)'E' && header[payload + 1] == (byte)'x' &&
                header[payload + 2] == (byte)'i' && header[payload + 3] == (byte)'f' &&
                header[payload + 4] == 0 && header[payload + 5] == 0)
            {
                return ReadTiffOrientation(header, payload + 6);
            }

            position = payload + length - 2;
        }

        return Normal;
    }

    /// <summary>
    /// Walks the PNG chunks looking for eXIf, whose payload is a TIFF block.
    /// </summary>
    private static int ReadPngOrientation(byte[] header)
    {
        var position = 8;
        while (position + 8 <= header.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(position, 4));
            if (length < 0)
            {
                return Normal;
            }
            var type = header.AsSpan(position + 4, 4);
            if (type[0] == (byte)'I' && type[1] == (byte)'E' && type[2] == (byte)'N' &&
                type[3] == (byte)'D')
            {
                return Normal;
            }

            if (type[0] == (byte)'e' && type[1] == (byte)'X' && type[2] == (byte)'I' &&
                type[3] == (byte)'f')
            {
                return ReadTiffOrientation(header, position + 8);
            }

            // Length, type, data and the four CRC bytes.
            position += 12 + length;
        }

        return Normal;
    }

    /// <summary>
    /// Walks the WebP chunks looking for EXIF, which starts with the "Exif" tag
    /// when the block was copied from a JPEG.
    /// </summary>
    private static int ReadWebpOrientation(byte[] header)
    {
        var position = 12;
        while (position + 8 <= header.Length)
        {
            var type = header.AsSpan(position, 4);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(position + 4, 4));
            if (length < 0)
            {
                return Normal;
            }

            var payload = position + 8;
            if (type[0] == (byte)'E' && type[1] == (byte)'X' && type[2] == (byte)'I' &&
                type[3] == (byte)'F')
            {
                var start = payload;
                if (length >= 6 && payload + 6 <= header.Length &&
                    header[payload] == (byte)'E' && header[payload + 1] == (byte)'x' &&
                    header[payload + 2] == (byte)'i' && header[payload + 3] == (byte)'f' &&
                    header[payload + 4] == 0 && header[payload + 5] == 0)
                {
                    start = payload + 6;
                }
                return ReadTiffOrientation(header, start);
            }

            // Chunks are padded to an even length.
            position += 8 + length + (length & 1);
        }

        return Normal;
    }

    /// <summary>
    /// Reads tag 0x0112 from the first image file directory of a TIFF block.
    /// The block may start anywhere inside the header buffer.
    /// </summary>
    private static int ReadTiffOrientation(byte[] data, int start)
    {
        if (start < 0 || start + 8 > data.Length)
        {
            return Normal;
        }

        bool littleEndian;
        if (data[start] == 0x49 && data[start + 1] == 0x49 && data[start + 2] == 0x2A)
        {
            littleEndian = true;
        }
        else if (data[start] == 0x4D && data[start + 1] == 0x4D && data[start + 2] == 0x00)
        {
            littleEndian = false;
        }
        else
        {
            return Normal;
        }

        var directory = ReadInt32(data, start + 4, littleEndian);
        if (directory <= 0 || start + directory + 2 > data.Length)
        {
            return Normal;
        }

        var count = ReadUInt16(data, start + directory, littleEndian);
        for (var index = 0; index < count; index++)
        {
            var entry = start + directory + 2 + index * 12;
            if (entry + 12 > data.Length)
            {
                return Normal;
            }

            if (ReadUInt16(data, entry, littleEndian) != 0x0112)
            {
                continue;
            }

            // The value is a SHORT, so it sits in the first two value bytes.
            var value = ReadUInt16(data, entry + 8, littleEndian);
            return value is >= Normal and <= Rotate270 ? value : Normal;
        }

        return Normal;
    }

    private static int ReadUInt16(byte[] data, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    private static int ReadInt32(byte[] data, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
}
