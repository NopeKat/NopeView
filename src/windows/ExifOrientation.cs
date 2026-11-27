// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using System.Buffers.Binary;

namespace NopeView.App;

/// <summary>
/// Reads the EXIF orientation tag of a still image, so the pixels can be turned
/// the way the camera intended before they reach the screen. JPEG, PNG, WebP
/// and TIFF carry the tag in a TIFF block; HEIC and AVIF keep theirs in the
/// container, and libheif and libjxl already apply it while decoding.
///
/// A JPEG keeps its tag in the header, but a PNG or WebP may store it behind
/// megabytes of image data, so those two chunk formats are walked by seeking
/// instead of by loading a fixed prefix.
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

    // The APP1 segment of a JPEG is capped at 64 KB and a TIFF directory sits
    // at the start of the file, so those formats need only the header.
    private const int HeaderBytes = 256 * 1024;

    // A metadata block larger than this is treated as damage rather than
    // loaded, which keeps a malformed file from asking for a huge allocation.
    private const long MetadataLimit = 1024 * 1024;

    // Chunk walks stop here so a file full of empty chunks cannot stall the
    // open path.
    private const int ChunkLimit = 4096;

    // Every animation frame asks again for the same file, so the last answer
    // stays until the file itself changes. Guarded because decodes and
    // prefetches read from two threads.
    private static readonly object Gate = new();
    private static string? _cachedPath;
    private static long _cachedLength;
    private static long _cachedTicks;
    private static int _cachedValue = Normal;

    /// <summary>
    /// Orientation of the image at path, or <see cref="Normal"/> when it carries
    /// none or the file cannot be read.
    /// </summary>
    internal static int Read(string path)
    {
        var stamp = ReadStamp(path);

        if (stamp is FileStamp current)
        {
            lock (Gate)
            {
                if (_cachedPath is not null &&
                    string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) &&
                    _cachedLength == current.Length &&
                    _cachedTicks == current.Ticks)
                {
                    return _cachedValue;
                }
            }
        }

        var value = ReadFromFile(path);

        if (stamp is FileStamp store)
        {
            lock (Gate)
            {
                _cachedPath = path;
                _cachedLength = store.Length;
                _cachedTicks = store.Ticks;
                _cachedValue = value;
            }
        }

        return value;
    }

    /// <summary>
    /// True when the orientation turns the image by a quarter turn, which swaps
    /// its width and height.
    /// </summary>
    internal static bool SwapsAxes(int orientation) =>
        orientation is Transpose or Rotate90 or Transverse or Rotate270;

    /// <summary>
    /// Length and modification time of the file, which together decide whether
    /// the cached orientation still describes what is on disk. Null when the
    /// file cannot be queried.
    /// </summary>
    private readonly struct FileStamp
    {
        internal FileStamp(long length, long ticks)
        {
            Length = length;
            Ticks = ticks;
        }

        internal long Length { get; }

        internal long Ticks { get; }
    }

    private static FileStamp? ReadStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
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

    private static int ReadFromFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = stream.Length;
            if (length < 8)
            {
                return Normal;
            }

            var head = ReadPrefix(stream, 12);
            if (head is null)
            {
                return Normal;
            }

            if (IsJpeg(head))
            {
                return ReadJpegOrientation(stream);
            }
            if (IsPng(head))
            {
                return ReadPngOrientation(stream, length);
            }
            if (IsWebp(head))
            {
                return ReadWebpOrientation(stream, length);
            }
            if (!IsTiff(head))
            {
                // HEIC, AVIF, JPEG XL and the rest either apply their
                // orientation while decoding or carry none.
                return Normal;
            }

            var block = ReadPrefix(stream, HeaderBytes);
            return block is null ? Normal : ReadTiffOrientation(block, 0);
        }
        catch (IOException)
        {
            return Normal;
        }
        catch (UnauthorizedAccessException)
        {
            return Normal;
        }
        catch (ArgumentOutOfRangeException)
        {
            // Metadata is optional, so a damaged block must never stop the
            // image itself from opening.
            return Normal;
        }
        catch (OverflowException)
        {
            return Normal;
        }
        catch (IndexOutOfRangeException)
        {
            return Normal;
        }
    }

    /// <summary>
    /// Reads up to count bytes from the start of the stream, clamped to its
    /// length. Null when nothing could be read.
    /// </summary>
    private static byte[]? ReadPrefix(FileStream stream, int count)
    {
        var length = (int)Math.Min(stream.Length, count);
        if (length <= 0)
        {
            return null;
        }

        if (stream.Seek(0, SeekOrigin.Begin) != 0L && stream.Position != 0L)
        {
            return null;
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            var step = stream.Read(buffer, read, buffer.Length - read);
            if (step <= 0)
            {
                return null;
            }
            read += step;
        }

        return buffer;
    }

    /// <summary>
    /// Reads count bytes at offset. Null when the range lies outside the file.
    /// </summary>
    private static byte[]? ReadAt(FileStream stream, long offset, long count, long fileLength)
    {
        if (offset < 0 || count <= 0 || count > MetadataLimit ||
            offset + count > fileLength)
        {
            return null;
        }

        var buffer = new byte[(int)count];
        stream.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < buffer.Length)
        {
            var step = stream.Read(buffer, read, buffer.Length - read);
            if (step <= 0)
            {
                return null;
            }
            read += step;
        }

        return buffer;
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

    private static bool IsTiff(byte[] header) =>
        header.Length >= 8 &&
        ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A) ||
         (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00));

    /// <summary>
    /// Walks the JPEG segments up to the first scan, looking for the APP1
    /// segment that begins with "Exif\0\0". Everything it needs sits in the
    /// header, so the bytes are read in one block.
    /// </summary>
    private static int ReadJpegOrientation(FileStream stream)
    {
        var header = ReadPrefix(stream, HeaderBytes);
        if (header is null)
        {
            return Normal;
        }

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
    /// Only chunk headers are read, so an eXIf behind the image data is still
    /// found.
    /// </summary>
    private static int ReadPngOrientation(FileStream stream, long fileLength)
    {
        var position = 8L;
        for (var chunk = 0; chunk < ChunkLimit && position + 8 <= fileLength; chunk++)
        {
            var header = ReadAt(stream, position, 8, fileLength);
            if (header is null)
            {
                return Normal;
            }

            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = header.AsSpan(4, 4);
            if (type[0] == (byte)'I' && type[1] == (byte)'E' &&
                type[2] == (byte)'N' && type[3] == (byte)'D')
            {
                return Normal;
            }

            var payload = position + 8;
            if (type[0] == (byte)'e' && type[1] == (byte)'X' &&
                type[2] == (byte)'I' && type[3] == (byte)'f')
            {
                var data = ReadAt(stream, payload, chunkLength, fileLength);
                return data is null ? Normal : ReadTiffOrientation(data, 0);
            }

            // Length, type, data and the four CRC bytes.
            position = payload + chunkLength + 4;
        }

        return Normal;
    }

    /// <summary>
    /// Walks the WebP chunks looking for EXIF, which may start with the "Exif"
    /// tag when the block was copied from a JPEG. Metadata normally sits after
    /// the image data, so the chunks are skipped by seeking.
    /// </summary>
    private static int ReadWebpOrientation(FileStream stream, long fileLength)
    {
        var head = ReadPrefix(stream, 12);
        if (head is null)
        {
            return Normal;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4, 4)) + 8u;
        var end = Math.Min(fileLength, (long)declared);

        var position = 12L;
        for (var chunk = 0; chunk < ChunkLimit && position + 8 <= end; chunk++)
        {
            var header = ReadAt(stream, position, 8, fileLength);
            if (header is null)
            {
                return Normal;
            }

            var type = header.AsSpan(0, 4);
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            var payload = position + 8;

            if (type[0] == (byte)'E' && type[1] == (byte)'X' &&
                type[2] == (byte)'I' && type[3] == (byte)'F')
            {
                var data = ReadAt(stream, payload, chunkLength, fileLength);
                if (data is null)
                {
                    return Normal;
                }

                // The block may keep the six-byte JPEG marker in front of the
                // TIFF header.
                var start = 0;
                if (data.Length >= 6 &&
                    data[0] == (byte)'E' && data[1] == (byte)'x' && data[2] == (byte)'i' &&
                    data[3] == (byte)'f' && data[4] == 0 && data[5] == 0)
                {
                    start = 6;
                }

                return ReadTiffOrientation(data, start);
            }

            // Chunks are padded to an even length.
            position = payload + chunkLength + (chunkLength & 1u);
        }

        return Normal;
    }

    /// <summary>
    /// Reads tag 0x0112 from the first image file directory of a TIFF block.
    /// The block may start anywhere inside the buffer, and every offset is
    /// bounded before it is used so a damaged directory degrades to
    /// <see cref="Normal"/> instead of throwing.
    /// </summary>
    private static int ReadTiffOrientation(byte[] data, long start)
    {
        if (start < 0 || start + 8 > data.Length)
        {
            return Normal;
        }

        var origin = (int)start;
        bool littleEndian;
        if (data[origin] == 0x49 && data[origin + 1] == 0x49 && data[origin + 2] == 0x2A)
        {
            littleEndian = true;
        }
        else if (data[origin] == 0x4D && data[origin + 1] == 0x4D && data[origin + 2] == 0x00)
        {
            littleEndian = false;
        }
        else
        {
            return Normal;
        }

        if (!TryReadInt32(data, origin + 4L, littleEndian, out var directoryOffset))
        {
            return Normal;
        }
        if (directoryOffset <= 0)
        {
            return Normal;
        }

        var directory = start + directoryOffset;
        if (!TryReadUInt16(data, directory, littleEndian, out var count))
        {
            return Normal;
        }

        var entries = directory + 2L;
        var available = (data.Length - entries) / 12;
        if (available <= 0)
        {
            return Normal;
        }

        var limit = (long)count < available ? count : available;
        for (var index = 0L; index < limit; index++)
        {
            var entry = entries + index * 12;
            if (!TryReadUInt16(data, entry, littleEndian, out var tag))
            {
                return Normal;
            }
            if (tag != 0x0112)
            {
                continue;
            }

            // The value is a SHORT, so it sits in the first two value bytes.
            if (!TryReadUInt16(data, entry + 8, littleEndian, out var value))
            {
                return Normal;
            }
            return value is >= Normal and <= Rotate270 ? value : Normal;
        }

        return Normal;
    }

    private static bool TryReadUInt16(byte[] data, long offset, bool littleEndian, out int value)
    {
        if (offset < 0 || offset + 2 > data.Length)
        {
            value = 0;
            return false;
        }

        var span = data.AsSpan((int)offset, 2);
        value = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(span)
            : BinaryPrimitives.ReadUInt16BigEndian(span);
        return true;
    }

    private static bool TryReadInt32(byte[] data, long offset, bool littleEndian, out int value)
    {
        if (offset < 0 || offset + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        var span = data.AsSpan((int)offset, 4);
        value = littleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(span)
            : BinaryPrimitives.ReadInt32BigEndian(span);
        return true;
    }
}
