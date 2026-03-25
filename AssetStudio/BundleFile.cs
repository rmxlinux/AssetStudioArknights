using K4os.Compression.LZ4;
using System;
using System.IO;
using System.Linq;

namespace AssetStudio
{
    [Flags]
    public enum ArchiveFlags
    {
        CompressionTypeMask = 0x3f,
        BlocksAndDirectoryInfoCombined = 0x40,
        BlocksInfoAtTheEnd = 0x80,
        OldWebPluginCompatibility = 0x100,
        BlockInfoNeedPaddingAtStart = 0x200
    }

    [Flags]
    public enum StorageBlockFlags
    {
        CompressionTypeMask = 0x3f,
        Streamed = 0x40
    }

    public enum CompressionType
    {
        None,
        Lzma,
        Lz4,
        Lz4HC,
        ArkLz4
    }

    public class BundleFile
    {
        public class Header
        {
            public string signature;
            public uint version;
            public string unityVersion;
            public string unityRevision;
            public long size;
            public uint compressedBlocksInfoSize;
            public uint uncompressedBlocksInfoSize;
            public ArchiveFlags flags;
        }

        public class StorageBlock
        {
            public uint compressedSize;
            public uint uncompressedSize;
            public StorageBlockFlags flags;
        }

        public class Node
        {
            public long offset;
            public long size;
            public uint flags;
            public string path;
        }

        public Header m_Header;
        private StorageBlock[] m_BlocksInfo;
        private Node[] m_DirectoryInfo;

        public StreamFile[] fileList;

        public BundleFile(FileReader reader)
        {
            m_Header = new Header();
            m_Header.signature = reader.ReadStringToNull();
            m_Header.version = reader.ReadUInt32();
            m_Header.unityVersion = reader.ReadStringToNull();
            m_Header.unityRevision = reader.ReadStringToNull();
            switch (m_Header.signature)
            {
                case "UnityArchive":
                    break; //TODO
                case "UnityWeb":
                case "UnityRaw":
                    if (m_Header.version == 6)
                    {
                        goto case "UnityFS";
                    }
                    ReadHeaderAndBlocksInfo(reader);
                    using (var blocksStream = CreateBlocksStream(reader.FullPath))
                    {
                        ReadBlocksAndDirectory(reader, blocksStream);
                        ReadFiles(blocksStream, reader.FullPath);
                    }
                    break;
                case "UnityFS":
                    ReadHeader(reader);
                    ReadBlocksInfoAndDirectory(reader);
                    using (var blocksStream = CreateBlocksStream(reader.FullPath))
                    {
                        ReadBlocks(reader, blocksStream);
                        ReadFiles(blocksStream, reader.FullPath);
                    }
                    break;
            }
        }

        private static (int length, int newPos) ReadLongLength(byte[] data, int pos)
        {
            int num = 0;
            byte b;
            do
            {
                if (pos >= data.Length)
                {
                    throw new EndOfStreamException("Failed to read long length: unexpected end of data.");
                }
                b = data[pos];
                pos++;
                num += b;
            }
            while (b == byte.MaxValue);
            return (num, pos);
        }

        private static void DecompressArkLz4(byte[] compressedBytes, int compressedSize, byte[] uncompressedBytes, int uncompressedSize)
        {
            byte[] array = BigArrayPool<byte>.Shared.Rent(compressedSize);
            try
            {
                Buffer.BlockCopy(compressedBytes, 0, array, 0, compressedSize);
                int num = 0;
                long num2 = 0L;
                while (num < compressedSize)
                {
                    byte num3 = array[num];
                    int num4 = num3 & 0xF;
                    int num5 = (num3 & 0xF0) >> 4;
                    array[num] = (byte)((num4 << 4) | num5);
                    num++;
                    if (num4 == 15)
                    {
                        var tuple = ReadLongLength(array, num);
                        num4 += tuple.length;
                        num = tuple.newPos;
                    }
                    num2 += num4;
                    num += num4;
                    if (uncompressedSize - num2 < 12 || num + 1 >= compressedSize)
                    {
                        break;
                    }
                    byte b = array[num];
                    ushort num6 = (ushort)(array[num + 1] | (b << 8));
                    array[num] = (byte)(num6 & 0xFF);
                    array[num + 1] = (byte)(num6 >> 8);
                    num += 2;
                    if (num5 == 15)
                    {
                        if (num >= compressedSize)
                        {
                            break;
                        }
                        var tuple2 = ReadLongLength(array, num);
                        num5 += tuple2.length;
                        num = tuple2.newPos;
                    }
                    num5 += 4;
                    num2 += num5;
                }
                int num7 = LZ4Codec.Decode(array, 0, compressedSize, uncompressedBytes, 0, uncompressedSize);
                if (num7 != uncompressedSize)
                {
                    throw new IOException($"Custom Lz4 decompression error, wrote {num7} bytes but expected {uncompressedSize} bytes after pre-processing");
                }
            }
            finally
            {
                BigArrayPool<byte>.Shared.Return(array);
            }
        }

        private void ReadHeaderAndBlocksInfo(EndianBinaryReader reader)
        {
            if (m_Header.version >= 4)
            {
                var hash = reader.ReadBytes(16);
                var crc = reader.ReadUInt32();
            }
            var minimumStreamedBytes = reader.ReadUInt32();
            m_Header.size = reader.ReadUInt32();
            var numberOfLevelsToDownloadBeforeStreaming = reader.ReadUInt32();
            var levelCount = reader.ReadInt32();
            m_BlocksInfo = new StorageBlock[1];
            for (int i = 0; i < levelCount; i++)
            {
                var storageBlock = new StorageBlock()
                {
                    compressedSize = reader.ReadUInt32(),
                    uncompressedSize = reader.ReadUInt32(),
                };
                if (i == levelCount - 1)
                {
                    m_BlocksInfo[0] = storageBlock;
                }
            }
            if (m_Header.version >= 2)
            {
                var completeFileSize = reader.ReadUInt32();
            }
            if (m_Header.version >= 3)
            {
                var fileInfoHeaderSize = reader.ReadUInt32();
            }
            reader.Position = m_Header.size;
        }

        private Stream CreateBlocksStream(string path)
        {
            Stream blocksStream;
            var uncompressedSizeSum = m_BlocksInfo.Sum(x => x.uncompressedSize);
            if (uncompressedSizeSum >= int.MaxValue)
            {
                /*var memoryMappedFile = MemoryMappedFile.CreateNew(null, uncompressedSizeSum);
                assetsDataStream = memoryMappedFile.CreateViewStream();*/
                blocksStream = new FileStream(path + ".temp", FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            }
            else
            {
                blocksStream = new MemoryStream((int)uncompressedSizeSum);
            }
            return blocksStream;
        }

        private void ReadBlocksAndDirectory(EndianBinaryReader reader, Stream blocksStream)
        {
            var isCompressed = m_Header.signature == "UnityWeb";
            foreach (var blockInfo in m_BlocksInfo)
            {
                var uncompressedBytes = reader.ReadBytes((int)blockInfo.compressedSize);
                if (isCompressed)
                {
                    using (var memoryStream = new MemoryStream(uncompressedBytes))
                    {
                        using (var decompressStream = SevenZipHelper.StreamDecompress(memoryStream))
                        {
                            uncompressedBytes = decompressStream.ToArray();
                        }
                    }
                }
                blocksStream.Write(uncompressedBytes, 0, uncompressedBytes.Length);
            }
            blocksStream.Position = 0;
            var blocksReader = new EndianBinaryReader(blocksStream);
            var nodesCount = blocksReader.ReadInt32();
            m_DirectoryInfo = new Node[nodesCount];
            for (int i = 0; i < nodesCount; i++)
            {
                m_DirectoryInfo[i] = new Node
                {
                    path = blocksReader.ReadStringToNull(),
                    offset = blocksReader.ReadUInt32(),
                    size = blocksReader.ReadUInt32()
                };
            }
        }

        public void ReadFiles(Stream blocksStream, string path)
        {
            fileList = new StreamFile[m_DirectoryInfo.Length];
            for (int i = 0; i < m_DirectoryInfo.Length; i++)
            {
                var node = m_DirectoryInfo[i];
                var file = new StreamFile();
                fileList[i] = file;
                file.path = node.path;
                file.fileName = Path.GetFileName(node.path);
                if (node.size >= int.MaxValue)
                {
                    /*var memoryMappedFile = MemoryMappedFile.CreateNew(null, entryinfo_size);
                    file.stream = memoryMappedFile.CreateViewStream();*/
                    var extractPath = path + "_unpacked" + Path.DirectorySeparatorChar;
                    Directory.CreateDirectory(extractPath);
                    file.stream = new FileStream(extractPath + file.fileName, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                }
                else
                {
                    file.stream = new MemoryStream((int)node.size);
                }
                blocksStream.Position = node.offset;
                blocksStream.CopyTo(file.stream, node.size);
                file.stream.Position = 0;
            }
        }

        private void ReadHeader(EndianBinaryReader reader)
        {
            m_Header.size = reader.ReadInt64();
            m_Header.compressedBlocksInfoSize = reader.ReadUInt32();
            m_Header.uncompressedBlocksInfoSize = reader.ReadUInt32();
            m_Header.flags = (ArchiveFlags)reader.ReadUInt32();
            if (m_Header.signature != "UnityFS")
            {
                reader.ReadByte();
            }
        }

        private void ReadBlocksInfoAndDirectory(EndianBinaryReader reader)
        {
            byte[] blocksInfoBytes;
            if (m_Header.version >= 7)
            {
                reader.AlignStream(16);
            }
            if ((m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0)
            {
                var position = reader.Position;
                reader.Position = reader.BaseStream.Length - m_Header.compressedBlocksInfoSize;
                blocksInfoBytes = reader.ReadBytes((int)m_Header.compressedBlocksInfoSize);
                reader.Position = position;
            }
            else //0x40 BlocksAndDirectoryInfoCombined
            {
                blocksInfoBytes = reader.ReadBytes((int)m_Header.compressedBlocksInfoSize);
            }
            MemoryStream blocksInfoUncompresseddStream;
            var uncompressedSize = m_Header.uncompressedBlocksInfoSize;
            var compressionType = (CompressionType)(m_Header.flags & ArchiveFlags.CompressionTypeMask);
            switch (compressionType)
            {
                case CompressionType.None:
                    {
                        blocksInfoUncompresseddStream = new MemoryStream(blocksInfoBytes);
                        break;
                    }
                case CompressionType.Lzma:
                    {
                        blocksInfoUncompresseddStream = new MemoryStream((int)(uncompressedSize));
                        using (var blocksInfoCompressedStream = new MemoryStream(blocksInfoBytes))
                        {
                            SevenZipHelper.StreamDecompress(blocksInfoCompressedStream, blocksInfoUncompresseddStream, m_Header.compressedBlocksInfoSize, m_Header.uncompressedBlocksInfoSize);
                        }
                        blocksInfoUncompresseddStream.Position = 0;
                        break;
                    }
                case CompressionType.Lz4:
                case CompressionType.Lz4HC:
                case CompressionType.ArkLz4:
                    {
                        var uncompressedBytes = new byte[uncompressedSize];
                        if (compressionType == CompressionType.ArkLz4 || compressionType == CompressionType.Lz4)
                        {
                            try
                            {
                                DecompressArkLz4(blocksInfoBytes, blocksInfoBytes.Length, uncompressedBytes, (int)uncompressedSize);
                            }
                            catch
                            {
                                if (compressionType == CompressionType.Lz4)
                                {
                                    var numWrite = LZ4Codec.Decode(blocksInfoBytes, uncompressedBytes);
                                    if (numWrite != uncompressedSize)
                                    {
                                        throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                                    }
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                        else
                        {
                            var numWrite = LZ4Codec.Decode(blocksInfoBytes, uncompressedBytes);
                            if (numWrite != uncompressedSize)
                            {
                                throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                            }
                        }
                        blocksInfoUncompresseddStream = new MemoryStream(uncompressedBytes);
                        break;
                    }
                default:
                    throw new IOException($"Unsupported compression type {compressionType}");
            }
            using (var blocksInfoReader = new EndianBinaryReader(blocksInfoUncompresseddStream))
            {
                var uncompressedDataHash = blocksInfoReader.ReadBytes(16);
                var blocksInfoCount = blocksInfoReader.ReadInt32();
                m_BlocksInfo = new StorageBlock[blocksInfoCount];
                for (int i = 0; i < blocksInfoCount; i++)
                {
                    m_BlocksInfo[i] = new StorageBlock
                    {
                        uncompressedSize = blocksInfoReader.ReadUInt32(),
                        compressedSize = blocksInfoReader.ReadUInt32(),
                        flags = (StorageBlockFlags)blocksInfoReader.ReadUInt16()
                    };
                }

                var nodesCount = blocksInfoReader.ReadInt32();
                m_DirectoryInfo = new Node[nodesCount];
                for (int i = 0; i < nodesCount; i++)
                {
                    m_DirectoryInfo[i] = new Node
                    {
                        offset = blocksInfoReader.ReadInt64(),
                        size = blocksInfoReader.ReadInt64(),
                        flags = blocksInfoReader.ReadUInt32(),
                        path = blocksInfoReader.ReadStringToNull(),
                    };
                }
            }
            if ((m_Header.flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
            {
                reader.AlignStream(16);
            }
        }

        private void ReadBlocks(EndianBinaryReader reader, Stream blocksStream)
        {
            foreach (var blockInfo in m_BlocksInfo)
            {
                var compressionType = (CompressionType)(blockInfo.flags & StorageBlockFlags.CompressionTypeMask);
                switch (compressionType)
                {
                    case CompressionType.None:
                        {
                            reader.BaseStream.CopyTo(blocksStream, blockInfo.compressedSize);
                            break;
                        }
                    case CompressionType.Lzma:
                        {
                            SevenZipHelper.StreamDecompress(reader.BaseStream, blocksStream, blockInfo.compressedSize, blockInfo.uncompressedSize);
                            break;
                        }
                    case CompressionType.Lz4:
                    case CompressionType.Lz4HC:
                    case CompressionType.ArkLz4:
                        {
                            var compressedSize = (int)blockInfo.compressedSize;
                            var compressedBytes = BigArrayPool<byte>.Shared.Rent(compressedSize);
                            reader.Read(compressedBytes, 0, compressedSize);
                            var uncompressedSize = (int)blockInfo.uncompressedSize;
                            var uncompressedBytes = BigArrayPool<byte>.Shared.Rent(uncompressedSize);
                            
                            if (compressionType == CompressionType.ArkLz4 || compressionType == CompressionType.Lz4)
                            {
                                try
                                {
                                    DecompressArkLz4(compressedBytes, compressedSize, uncompressedBytes, uncompressedSize);
                                }
                                catch
                                {
                                    if (compressionType == CompressionType.Lz4)
                                    {
                                        var numWrite = LZ4Codec.Decode(compressedBytes, 0, compressedSize, uncompressedBytes, 0, uncompressedSize);
                                        if (numWrite != uncompressedSize)
                                        {
                                            throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                                        }
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            else 
                            {
                                var numWrite = LZ4Codec.Decode(compressedBytes, 0, compressedSize, uncompressedBytes, 0, uncompressedSize);
                                if (numWrite != uncompressedSize)
                                {
                                    throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                                }
                            }
                            
                            blocksStream.Write(uncompressedBytes, 0, uncompressedSize);
                            BigArrayPool<byte>.Shared.Return(compressedBytes);
                            BigArrayPool<byte>.Shared.Return(uncompressedBytes);
                            break;
                        }
                    default:
                        throw new IOException($"Unsupported compression type {compressionType}");
                }
            }
            blocksStream.Position = 0;
        }
    }
}
