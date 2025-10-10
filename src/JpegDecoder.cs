using System;
using System.IO;
using Console = JpegToBmpConverter.SilentConsole;

namespace JpegToBmpConverter
{
    /// <summary>
    /// JPEG解码器，基于JPEG标准实现，参考Pillow库的JpegDecode.c
    /// </summary>
    public class JpegDecoder
    {
        // JPEG标记常量
        private const byte MARKER_PREFIX = 0xFF;
        private const byte SOI = 0xD8;  // Start of Image
        private const byte EOI = 0xD9;  // End of Image
        private const byte SOF0 = 0xC0; // Start of Frame (Baseline DCT)
        private const byte SOF1 = 0xC1; // Start of Frame (Extended Sequential DCT)
        private const byte SOF2 = 0xC2; // Start of Frame (Progressive DCT)
        private const byte DHT = 0xC4;  // Define Huffman Table
        private const byte DQT = 0xDB;  // Define Quantization Table
        private const byte DRI = 0xDD;  // Define Restart Interval
        private const byte SOS = 0xDA;  // Start of Scan
        private const byte APP0 = 0xE0; // Application Segment 0 (JFIF)
        private const byte COM = 0xFE;  // Comment

        // 图像信息
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Components { get; private set; }
        public byte[,] ImageData { get; private set; }

        // 量化表 (最多4个)
        private int[,] quantizationTables = new int[4, 64];
        private bool[] quantTableDefined = new bool[4];

        // 霍夫曼表
        private HuffmanTable[] dcHuffmanTables = new HuffmanTable[4];
        private HuffmanTable[] acHuffmanTables = new HuffmanTable[4];

        // 帧信息
        private ComponentInfo[] componentInfos;
        private int precision;

        /// <summary>
        /// 解码JPEG文件
        /// </summary>
        /// <param name="jpegData">JPEG文件数据</param>
        /// <returns>解码是否成功</returns>
        public bool Decode(byte[] jpegData)
        {
            try
            {
                using (var stream = new MemoryStream(jpegData))
                using (var reader = new BinaryReader(stream))
                {
                    // 检查JPEG文件头
                    if (!CheckJpegHeader(reader))
                    {
                        throw new InvalidDataException("不是有效的JPEG文件");
                    }

                    // 解析JPEG段
                    while (stream.Position < stream.Length)
                    {
                        if (!ParseSegment(reader))
                        {
                            break;
                        }
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"JPEG解码错误: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检查JPEG文件头
        /// </summary>
        private bool CheckJpegHeader(BinaryReader reader)
        {
            if (reader.ReadByte() != MARKER_PREFIX || reader.ReadByte() != SOI)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// 解析JPEG段
        /// </summary>
        private bool ParseSegment(BinaryReader reader)
        {
            // 查找标记
            byte marker = FindNextMarker(reader);
            if (marker == 0)
            {
                return false;
            }

            switch (marker)
            {
                case APP0:
                    ParseApp0Segment(reader);
                    break;
                case DQT:
                    ParseQuantizationTable(reader);
                    break;
                case SOF0:
                case SOF1:
                case SOF2:
                    ParseStartOfFrame(reader);
                    break;
                case DHT:
                    ParseHuffmanTable(reader);
                    break;
                case SOS:
                    ParseStartOfScan(reader);
                    return false; // 开始扫描后停止解析段
                case EOI:
                    return false; // 图像结束
                case COM:
                    SkipSegment(reader);
                    break;
                default:
                    if (marker >= 0xE0 && marker <= 0xEF)
                    {
                        // 应用程序段
                        SkipSegment(reader);
                    }
                    else
                    {
                        Console.WriteLine($"未知标记: 0x{marker:X2}");
                        SkipSegment(reader);
                    }
                    break;
            }

            return true;
        }

        /// <summary>
        /// 查找下一个标记
        /// </summary>
        private byte FindNextMarker(BinaryReader reader)
        {
            byte b;
            do
            {
                b = reader.ReadByte();
            } while (b != MARKER_PREFIX);

            do
            {
                b = reader.ReadByte();
            } while (b == MARKER_PREFIX);

            return b;
        }

        /// <summary>
        /// 解析APP0段 (JFIF)
        /// </summary>
        private void ParseApp0Segment(BinaryReader reader)
        {
            int length = ReadBigEndianUInt16(reader);
            byte[] data = reader.ReadBytes(length - 2);
            
            // 检查JFIF标识
            if (data.Length >= 5 && 
                data[0] == 0x4A && data[1] == 0x46 && data[2] == 0x49 && 
                data[3] == 0x46 && data[4] == 0x00)
            {
                Console.WriteLine("检测到JFIF格式");
            }
        }

        /// <summary>
        /// 解析量化表
        /// </summary>
        private void ParseQuantizationTable(BinaryReader reader)
        {
            int length = ReadBigEndianUInt16(reader);
            int bytesRead = 2;

            while (bytesRead < length)
            {
                byte info = reader.ReadByte();
                bytesRead++;

                int precision = (info >> 4) & 0x0F;
                int tableId = info & 0x0F;

                if (tableId >= 4)
                {
                    throw new InvalidDataException($"无效的量化表ID: {tableId}");
                }

                // 读取量化表数据
                for (int i = 0; i < 64; i++)
                {
                    if (precision == 0)
                    {
                        quantizationTables[tableId, i] = reader.ReadByte();
                        bytesRead++;
                    }
                    else
                    {
                        quantizationTables[tableId, i] = ReadBigEndianUInt16(reader);
                        bytesRead += 2;
                    }
                }

                quantTableDefined[tableId] = true;
            }
        }

        /// <summary>
        /// 解析帧开始段
        /// </summary>
        private void ParseStartOfFrame(BinaryReader reader)
        {
            int length = ReadBigEndianUInt16(reader);
            precision = reader.ReadByte();
            Height = ReadBigEndianUInt16(reader);
            Width = ReadBigEndianUInt16(reader);
            Components = reader.ReadByte();

            componentInfos = new ComponentInfo[Components];

            for (int i = 0; i < Components; i++)
            {
                componentInfos[i] = new ComponentInfo
                {
                    Id = reader.ReadByte(),
                    SamplingFactor = reader.ReadByte(),
                    QuantTableId = reader.ReadByte()
                };

                componentInfos[i].HorizontalSampling = (componentInfos[i].SamplingFactor >> 4) & 0x0F;
                componentInfos[i].VerticalSampling = componentInfos[i].SamplingFactor & 0x0F;
            }

            Console.WriteLine($"图像尺寸: {Width}x{Height}, 组件数: {Components}");
        }

        /// <summary>
        /// 解析霍夫曼表
        /// </summary>
        private void ParseHuffmanTable(BinaryReader reader)
        {
            int length = ReadBigEndianUInt16(reader);
            int bytesRead = 2;

            while (bytesRead < length)
            {
                byte info = reader.ReadByte();
                bytesRead++;

                int tableClass = (info >> 4) & 0x01; // 0=DC, 1=AC
                int tableId = info & 0x0F;

                if (tableId >= 4)
                {
                    throw new InvalidDataException($"无效的霍夫曼表ID: {tableId}");
                }

                // 读取码长统计
                byte[] codeLengths = reader.ReadBytes(16);
                bytesRead += 16;

                // 计算符号总数
                int totalSymbols = 0;
                for (int i = 0; i < 16; i++)
                {
                    totalSymbols += codeLengths[i];
                }

                // 读取符号
                byte[] symbols = reader.ReadBytes(totalSymbols);
                bytesRead += totalSymbols;

                // 创建霍夫曼表
                var huffmanTable = new HuffmanTable(codeLengths, symbols);

                if (tableClass == 0)
                {
                    dcHuffmanTables[tableId] = huffmanTable;
                }
                else
                {
                    acHuffmanTables[tableId] = huffmanTable;
                }
            }
        }

        /// <summary>
        /// 解析扫描开始段
        /// </summary>
        private void ParseStartOfScan(BinaryReader reader)
        {
            int length = ReadBigEndianUInt16(reader);
            int scanComponents = reader.ReadByte();

            var scanComponentInfos = new ScanComponentInfo[scanComponents];

            for (int i = 0; i < scanComponents; i++)
            {
                scanComponentInfos[i] = new ScanComponentInfo
                {
                    ComponentId = reader.ReadByte(),
                    HuffmanTableIds = reader.ReadByte()
                };

                scanComponentInfos[i].DcHuffmanTableId = (scanComponentInfos[i].HuffmanTableIds >> 4) & 0x0F;
                scanComponentInfos[i].AcHuffmanTableId = scanComponentInfos[i].HuffmanTableIds & 0x0F;
            }

            // 跳过谱选择参数
            reader.ReadBytes(3);

            // 使用已忠实翻译自 libjpeg-turbo 的解码实现进行真实解码
            var ms = reader.BaseStream as MemoryStream;
            if (ms == null)
            {
                throw new InvalidDataException("无法访问完整JPEG数据");
            }

            var jpegBytes = ms.ToArray();
            var turboDecoder = new PillowStyleJpegDecoder();
            bool ok = turboDecoder.Decode(jpegBytes);
            if (ok && turboDecoder.ImageData != null)
            {
                // 将真实解码结果回填到当前解码器
                Width = turboDecoder.Width;
                Height = turboDecoder.Height;
                Components = turboDecoder.Components;
                ImageData = turboDecoder.ImageData;
            }
            else
            {
                throw new InvalidDataException($"libjpeg-turbo解码失败: {turboDecoder.ErrorMessage ?? "未知错误"}");
            }
        }

        /// <summary>
        /// 跳过段
        /// </summary>
        private void SkipSegment(BinaryReader reader)
        {
            int length = ReadBigEndianUInt16(reader);
            reader.ReadBytes(length - 2);
        }

        /// <summary>
        /// 读取大端序16位整数
        /// </summary>
        private int ReadBigEndianUInt16(BinaryReader reader)
        {
            byte high = reader.ReadByte();
            byte low = reader.ReadByte();
            return (high << 8) | low;
        }
    }

    /// <summary>
    /// 组件信息
    /// </summary>
    public class ComponentInfo
    {
        public byte Id { get; set; }
        public byte SamplingFactor { get; set; }
        public int HorizontalSampling { get; set; }
        public int VerticalSampling { get; set; }
        public byte QuantTableId { get; set; }
    }

    /// <summary>
    /// 扫描组件信息
    /// </summary>
    public class ScanComponentInfo
    {
        public byte ComponentId { get; set; }
        public byte HuffmanTableIds { get; set; }
        public int DcHuffmanTableId { get; set; }
        public int AcHuffmanTableId { get; set; }
    }
}