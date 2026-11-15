using System;
using System.Buffers.Binary;
using CUE4Parse.Compression;
using CUE4Parse.UE4.Readers;

namespace CUE4Parse.UE4.Objects.Core.Compression;

public class FCompressedBuffer
{
    public FCompressedBufferHeader Header;
    public byte[] Data;

    public FCompressedBuffer()
    {
        Header = new FCompressedBufferHeader();
        Data = [];
    }

    public FCompressedBuffer(FArchive Ar)
    {
        Header = new FCompressedBufferHeader(Ar);

        const ulong MaxCompressedSize = (ulong)1 << 48;
        ulong headerSize = 64; // hardcode for now
        if (Header.Magic == FCompressedBufferHeader.ExpectedMagic &&
            Header.TotalCompressedSize >= headerSize &&
            Header.TotalCompressedSize <= MaxCompressedSize)
        {
            Data = Ar.ReadArray<byte>((int)(Header.TotalCompressedSize - headerSize));
        }
        else
        {
            Data = [];
        }
    }

    public byte[] Decompress()
    {
        const ulong MaxRawSize = 1UL << 30;
        if (Header.Magic != FCompressedBufferHeader.ExpectedMagic || Header.TotalRawSize > MaxRawSize)
            return [];
        if (Header.Method == FCompressedBufferHeader.EMethod.None)
            return Header.TotalRawSize <= (ulong) Data.Length ? Data[..(int) Header.TotalRawSize] : [];
        if (Header.BlockSizeExponent > 30 || Header.BlockCount == 0 ||
            (ulong) Header.BlockCount * 4 > (ulong) Data.Length ||
            Header.TotalRawSize > (ulong) Header.BlockCount << Header.BlockSizeExponent)
            return [];
        var raw = new byte[Header.TotalRawSize];

        var method = Header.Method switch
        {
            FCompressedBufferHeader.EMethod.Oodle => CompressionMethod.Oodle,
            FCompressedBufferHeader.EMethod.LZ4 => CompressionMethod.LZ4,
            _ => throw new NotSupportedException($"FCompressedBuffer method {Header.Method}"),
        };
        var blocks = (int) Header.BlockCount;
        var blockSize = 1L << Header.BlockSizeExponent;
        var position = 4 * blocks;
        long written = 0;
        for (var i = 0; i < blocks; i++)
        {
            var compressed = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(4 * i));
            var size = (int) System.Math.Min(blockSize, raw.LongLength - written);
            if (size <= 0 || compressed > (uint) (Data.Length - position))
                return [];
            if (compressed == size)
                Data.AsSpan(position, size).CopyTo(raw.AsSpan((int) written));
            else
                CUE4Parse.Compression.Compression.Decompress(Data.AsSpan(position, (int) compressed), raw.AsSpan((int) written, size), method);
            position += (int) compressed;
            written += size;
        }

        return raw;
    }
}
