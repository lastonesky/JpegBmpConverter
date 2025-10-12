using System;
using System.IO;

namespace JpegBmpConverter
{
    /// <summary>
    /// JPEG编码器，基于Pillow库JpegEncode.c实现
    /// 支持RGB和灰度图像的JPEG压缩
    /// </summary>
    public class JpegEncoder
    {
        // JPEG标记常量
        private const byte JPEG_SOI = 0xD8;  // Start of Image
        private const byte JPEG_EOI = 0xD9;  // End of Image
        private const byte JPEG_APP0 = 0xE0; // Application segment 0 (JFIF)
        private const byte JPEG_DQT = 0xDB;  // Define Quantization Table
        private const byte JPEG_SOF0 = 0xC0; // Start of Frame (baseline)
        private const byte JPEG_DHT = 0xC4;  // Define Huffman Table
        private const byte JPEG_SOS = 0xDA;  // Start of Scan
        private const byte JPEG_COM = 0xFE;  // Comment
        
        // DCT块大小
        private const int DCT_SIZE = 8;
        private const int DCT_SIZE2 = DCT_SIZE * DCT_SIZE;
        // Zigzag扫描顺序（JPEG自然顺序到Zigzag索引映射）
        private static readonly int[] ZigZagOrder = new int[]
        {
            0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63
        };
        
        public int Quality { get; set; } = 75;
        public bool Progressive { get; set; } = false;
        public string Comment { get; set; } = "";
        
        // 量化表
        private readonly byte[] LuminanceQuantTable = new byte[64];
        private readonly byte[] ChrominanceQuantTable = new byte[64];
        
        // 霍夫曼表
        private readonly HuffmanEncoder LuminanceDCHuffman = new();
        private readonly HuffmanEncoder LuminanceACHuffman = new();
        private readonly HuffmanEncoder ChrominanceDCHuffman = new();
        private readonly HuffmanEncoder ChrominanceACHuffman = new();
        
        // 位写入器
        private BitWriter? bitWriter;
        
        public JpegEncoder()
        {
            InitializeQuantizationTables();
            InitializeHuffmanTables();
        }
        
        /// <summary>
        /// 编码RGB图像为JPEG
        /// </summary>
        public bool EncodeRgb(byte[] rgbData, int width, int height, string outputPath)
        {
            try
            {
                using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                bitWriter = new BitWriter(fileStream);
                
                // 写入JPEG头部
                WriteJpegHeaders(width, height, 3); // 3个颜色分量
                
                // 转换RGB到YCbCr并编码
                EncodeImageData(rgbData, width, height, false);
                
                // 写入结束标记
                WriteMarker(0xFF, JPEG_EOI);
                
                bitWriter.Flush();
                Console.WriteLine($"成功编码RGB图像为JPEG: {width}x{height}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"编码JPEG时发生错误: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// 编码灰度图像为JPEG
        /// </summary>
        public bool EncodeGrayscale(byte[] grayscaleData, int width, int height, string outputPath)
        {
            try
            {
                using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                bitWriter = new BitWriter(fileStream);
                
                // 写入JPEG头部
                WriteJpegHeaders(width, height, 1); // 1个颜色分量
                
                // 编码灰度数据
                EncodeImageData(grayscaleData, width, height, true);
                
                // 写入结束标记
                WriteMarker(0xFF, JPEG_EOI);
                
                bitWriter.Flush();
                Console.WriteLine($"成功编码灰度图像为JPEG: {width}x{height}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"编码JPEG时发生错误: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// 写入JPEG文件头部
        /// </summary>
        private void WriteJpegHeaders(int width, int height, int components)
        {
            // SOI标记
            WriteMarker(0xFF, JPEG_SOI);
            
            // APP0段（JFIF）
            WriteApp0Segment();
            
            // 量化表
            WriteQuantizationTables(components);
            
            // 帧头（SOF0）
            WriteFrameHeader(width, height, components);
            
            // 霍夫曼表
            WriteHuffmanTables(components);
            
            // 扫描头（SOS）
            WriteScanHeader(components);
        }
        
        /// <summary>
        /// 写入APP0段（JFIF头部）
        /// </summary>
        private void WriteApp0Segment()
        {
            WriteMarker(0xFF, JPEG_APP0);
            bitWriter!.WriteUInt16(16); // 段长度
            bitWriter.WriteBytes(new byte[] { 0x4A, 0x46, 0x49, 0x46, 0x00 }); // "JFIF\0"
            bitWriter.WriteUInt16(0x0101); // 版本1.1
            bitWriter.WriteByte(1); // 密度单位（DPI）
            bitWriter.WriteUInt16(72); // X密度
            bitWriter.WriteUInt16(72); // Y密度
            bitWriter.WriteByte(0); // 缩略图宽度
            bitWriter.WriteByte(0); // 缩略图高度
        }
        
        /// <summary>
        /// 写入量化表
        /// </summary>
        private void WriteQuantizationTables(int components)
        {
            // 亮度量化表
            WriteQuantizationTable(0, LuminanceQuantTable);
            
            // 色度量化表（仅彩色图像需要）
            if (components > 1)
            {
                WriteQuantizationTable(1, ChrominanceQuantTable);
            }
        }
        
        /// <summary>
        /// 写入单个量化表
        /// </summary>
        private void WriteQuantizationTable(int tableId, byte[] table)
        {
            WriteMarker(0xFF, JPEG_DQT);
            bitWriter!.WriteUInt16(67); // 段长度
            bitWriter.WriteByte((byte)tableId); // 表ID和精度
            // 以Zigzag顺序写出量化表
            byte[] zigzagged = new byte[64];
            for (int i = 0; i < 64; i++)
            {
                int pos = ZigZagOrder[i];
                zigzagged[i] = table[pos];
            }
            bitWriter.WriteBytes(zigzagged);
        }
        
        /// <summary>
        /// 写入帧头
        /// </summary>
        private void WriteFrameHeader(int width, int height, int components)
        {
            WriteMarker(0xFF, JPEG_SOF0);
            bitWriter!.WriteUInt16((ushort)(8 + components * 3)); // 段长度
            bitWriter.WriteByte(8); // 精度
            bitWriter.WriteUInt16((ushort)height);
            bitWriter.WriteUInt16((ushort)width);
            bitWriter.WriteByte((byte)components);
            
            for (int i = 0; i < components; i++)
            {
                bitWriter.WriteByte((byte)(i + 1)); // 分量ID
                if (i == 0) // 亮度分量
                {
                    bitWriter.WriteByte(0x11); // 采样因子 1x1（简化为4:4:4编码）
                    bitWriter.WriteByte(0); // 量化表ID
                }
                else // 色度分量
                {
                    bitWriter.WriteByte(0x11); // 采样因子 1x1
                    bitWriter.WriteByte(1); // 量化表ID
                }
            }
        }
        
        /// <summary>
        /// 写入霍夫曼表
        /// </summary>
        private void WriteHuffmanTables(int components)
        {
            // 亮度DC霍夫曼表
            WriteHuffmanTable(0x00, LuminanceDCHuffman);
            // 亮度AC霍夫曼表
            WriteHuffmanTable(0x10, LuminanceACHuffman);
            
            if (components > 1)
            {
                // 色度DC霍夫曼表
                WriteHuffmanTable(0x01, ChrominanceDCHuffman);
                // 色度AC霍夫曼表
                WriteHuffmanTable(0x11, ChrominanceACHuffman);
            }
        }
        
        /// <summary>
        /// 写入单个霍夫曼表
        /// </summary>
        private void WriteHuffmanTable(byte tableInfo, HuffmanEncoder huffman)
        {
            WriteMarker(0xFF, JPEG_DHT);
            
            var (lengths, symbols) = huffman.GetTableData();
            bitWriter!.WriteUInt16((ushort)(3 + 16 + symbols.Length)); // 段长度
            bitWriter.WriteByte(tableInfo);
            bitWriter.WriteBytes(lengths);
            bitWriter.WriteBytes(symbols);
        }
        
        /// <summary>
        /// 写入扫描头
        /// </summary>
        private void WriteScanHeader(int components)
        {
            WriteMarker(0xFF, JPEG_SOS);
            bitWriter!.WriteUInt16((ushort)(6 + components * 2)); // 段长度
            bitWriter.WriteByte((byte)components);
            
            for (int i = 0; i < components; i++)
            {
                bitWriter.WriteByte((byte)(i + 1)); // 分量ID
                if (i == 0) // 亮度分量
                {
                    bitWriter.WriteByte(0x00); // DC表0, AC表0
                }
                else // 色度分量
                {
                    bitWriter.WriteByte(0x11); // DC表1, AC表1
                }
            }
            
            bitWriter.WriteByte(0); // 谱选择开始
            bitWriter.WriteByte(63); // 谱选择结束
            bitWriter.WriteByte(0); // 逐次逼近
        }
        
        /// <summary>
        /// 编码图像数据
        /// </summary>
        private void EncodeImageData(byte[] imageData, int width, int height, bool isGrayscale)
        {
            // 基线JPEG编码（简化实现）：4:4:4，无重启标记
            // 步骤：按8x8块 -> 正向DCT -> 量化 -> Zigzag -> 霍夫曼编码

            // 构建量化表（自然顺序）
            int[,] QY = new int[8, 8];
            int[,] QC = new int[8, 8];
            for (int i = 0; i < 64; i++)
            {
                QY[i / 8, i % 8] = LuminanceQuantTable[i];
                QC[i / 8, i % 8] = ChrominanceQuantTable[i];
            }

            // --- 👇 AAN DCT 缩放修正（加在这里） ---
            double[] AAN_SCALE = new double[8]
            {
                1.0,
                1.387039845,
                1.306562965,
                1.175875602,
                1.0,
                0.785694958,
                0.541196100,
                0.275899379
            };

            for (int u = 0; u < 8; u++)
            {
                for (int v = 0; v < 8; v++)
                {
                    double scale = AAN_SCALE[u] * AAN_SCALE[v];
                    QY[u, v] = (int)Math.Round(QY[u, v] * scale);
                    QC[u, v] = (int)Math.Round(QC[u, v] * scale);
                    // 防止过小变成0
                    if (QY[u, v] == 0) QY[u, v] = 1;
                    if (QC[u, v] == 0) QC[u, v] = 1;
                }
            }
            
            
            // 构建霍夫曼码映射
            LuminanceDCHuffman.BuildCodes();
            LuminanceACHuffman.BuildCodes();
            ChrominanceDCHuffman.BuildCodes();
            ChrominanceACHuffman.BuildCodes();

            // 准备组件数据
            byte[] Y; byte[] Cb = Array.Empty<byte>(); byte[] Cr = Array.Empty<byte>();
            if (isGrayscale)
            {
                Y = imageData;
            }
            else
            {
                Y = new byte[width * height];
                Cb = new byte[width * height];
                Cr = new byte[width * height];
                for (int i = 0, p = 0; i < width * height; i++, p += 3)
                {
                    byte r = imageData[p + 0];
                    byte g = imageData[p + 1];
                    byte b = imageData[p + 2];
                    var (yy, cb, cr) = DctProcessor.RgbToYCbCr(r, g, b);
                    Y[i] = yy; Cb[i] = cb; Cr[i] = cr;
                }
            }

            int blocksX = (width + 7) / 8;
            int blocksY = (height + 7) / 8;

            int prevDC_Y = 0, prevDC_Cb = 0, prevDC_Cr = 0;

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    // 亮度块
                    EncodeSingleBlock(Y, width, height, bx, by, QY, LuminanceDCHuffman, LuminanceACHuffman, ref prevDC_Y);
                    if (!isGrayscale)
                    {
                        // 色度块（与亮度同采样）
                        EncodeSingleBlock(Cb, width, height, bx, by, QC, ChrominanceDCHuffman, ChrominanceACHuffman, ref prevDC_Cb);
                        EncodeSingleBlock(Cr, width, height, bx, by, QC, ChrominanceDCHuffman, ChrominanceACHuffman, ref prevDC_Cr);
                    }
                }
            }
        }

        private void EncodeSingleBlock(byte[] plane, int width, int height, int bx, int by,
                                       int[,] quant, HuffmanEncoder dcTbl, HuffmanEncoder acTbl, ref int prevDC)
        {
            // 提取8x8采样块（边界外使用边缘像素）
            int[,] samples = new int[8, 8];
            for (int y = 0; y < 8; y++)
            {
                int py = Math.Min(by * 8 + y, height - 1);
                for (int x = 0; x < 8; x++)
                {
                    int px = Math.Min(bx * 8 + x, width - 1);
                    int v = plane[py * width + px];
                    samples[y, x] = v - 128; // 居中
                }
            }

            // 正向DCT
            double[,] dct = ForwardDctAAN(samples);

            // 量化并按Zigzag顺序重排
            int[] flat = new int[64];
            for (int u = 0; u < 8; u++)
            {
                for (int v = 0; v < 8; v++)
                {
                    // 自然顺序约定为[row=v, col=u]，量化表同样按[row,col]
                    int q = (int)Math.Round(dct[u, v] / quant[v, u]);
                    flat[v * 8 + u] = q;
                }
            }
            int[] zz = new int[64];
            for (int i = 0; i < 64; i++)
            {
                int naturalIdx = ZigZagOrder[i]; // ZigZag索引 -> 自然索引
                zz[i] = flat[naturalIdx];
            }

            // DC差分编码
            int diff = zz[0] - prevDC;
            prevDC = zz[0];
            int size = MagnitudeSize(diff);
            dcTbl.EncodeSymbol(bitWriter!, (byte)size);
            if (size > 0)
            {
                uint bits = EncodeMagnitudeBits(diff, size);
                bitWriter!.WriteBits(bits, size);
            }

            // AC游程编码
            int run = 0;
            for (int i = 1; i < 64; i++)
            {
                int val = zz[i];
                if (val == 0)
                {
                    run++;
                    if (run == 16)
                    {
                        acTbl.EncodeSymbol(bitWriter!, 0xF0);
                        run = 0;
                    }
                    continue;
                }

                while (run >= 16)
                {
                    acTbl.EncodeSymbol(bitWriter!, 0xF0);
                    run -= 16;
                }

                int sz = MagnitudeSize(val);
                byte symbol = (byte)((run << 4) | sz);
                acTbl.EncodeSymbol(bitWriter!, symbol);
                uint abits = EncodeMagnitudeBits(val, sz);
                bitWriter!.WriteBits(abits, sz);
                run = 0;
            }

            if (run > 0)
            {
                // 结尾EOB
                acTbl.EncodeSymbol(bitWriter!, 0x00);
            }
        }

        private static readonly double[,] CosTable = PrecomputeCosTable();
        private static readonly double[] C = Enumerable.Range(0, 8)
            .Select(i => i == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0)
            .ToArray();

        private static double[,] PrecomputeCosTable()
        {
            double[,] table = new double[8, 8];
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    table[i, j] = Math.Cos((2 * j + 1) * i * Math.PI / 16.0);
                }
            }
            return table;
        }

        private static double[,] ForwardDct(int[,] samples)
        {
            double[,] temp = new double[8, 8];
            double[,] coeffs = new double[8, 8];

            // --- 行变换 ---
            for (int y = 0; y < 8; y++)
            {
                for (int u = 0; u < 8; u++)
                {
                    double sum = 0.0;
                    for (int x = 0; x < 8; x++)
                    {
                        sum += samples[y, x] * CosTable[u, x];
                    }
                    temp[y, u] = sum;
                }
            }

            // --- 列变换 ---
            for (int u = 0; u < 8; u++)
            {
                for (int v = 0; v < 8; v++)
                {
                    double sum = 0.0;
                    for (int y = 0; y < 8; y++)
                    {
                        sum += temp[y, u] * CosTable[v, y];
                    }
                    // ✅ 正确的缩放系数只在此处乘上
                    coeffs[u, v] = 0.25 * C[u] * C[v] * sum;
                }
            }

            return coeffs;
        }
        // AAN 8x8 快速DCT实现（与JPEG标准一致）
        // 输入: int[8,8] 样本（通常为 -128 ~ +127）
        // 输出: double[8,8] DCT系数（未量化）

        private static double[,] ForwardDctAAN(int[,] data)
        {
            double[,] tmp = new double[8, 8];
            double[,] outp = new double[8, 8];

            // 常量（AAN系数）
            const double C1 = 0.98078528;
            const double C2 = 0.92387953;
            const double C3 = 0.83146961;
            const double C5 = 0.55557023;
            const double C6 = 0.38268343;
            const double C7 = 0.19509032;

            // --- 行DCT ---
            for (int y = 0; y < 8; y++)
            {
                double d0 = data[y, 0];
                double d1 = data[y, 1];
                double d2 = data[y, 2];
                double d3 = data[y, 3];
                double d4 = data[y, 4];
                double d5 = data[y, 5];
                double d6 = data[y, 6];
                double d7 = data[y, 7];

                double tmp0 = d0 + d7;
                double tmp7 = d0 - d7;
                double tmp1 = d1 + d6;
                double tmp6 = d1 - d6;
                double tmp2 = d2 + d5;
                double tmp5 = d2 - d5;
                double tmp3 = d3 + d4;
                double tmp4 = d3 - d4;

                double tmp10 = tmp0 + tmp3;
                double tmp13 = tmp0 - tmp3;
                double tmp11 = tmp1 + tmp2;
                double tmp12 = tmp1 - tmp2;

                tmp[0, y] = tmp10 + tmp11;
                tmp[4, y] = tmp10 - tmp11;

                double z1 = (tmp12 + tmp13) * 0.70710678;
                tmp[2, y] = tmp13 + z1;
                tmp[6, y] = tmp13 - z1;

                tmp10 = tmp4 + tmp5;
                tmp11 = tmp5 + tmp6;
                tmp12 = tmp6 + tmp7;

                double z5 = (tmp10 - tmp12) * 0.38268343;
                double z2 = 0.54119610 * tmp10 + z5;
                double z4 = 1.30656296 * tmp12 + z5;
                double z3 = tmp11 * 0.70710678;

                double z11 = tmp7 + z3;
                double z13 = tmp7 - z3;

                tmp[5, y] = z13 + z2;
                tmp[3, y] = z13 - z2;
                tmp[1, y] = z11 + z4;
                tmp[7, y] = z11 - z4;
            }

            // --- 列DCT ---
            for (int x = 0; x < 8; x++)
            {
                double d0 = tmp[x, 0];
                double d1 = tmp[x, 1];
                double d2 = tmp[x, 2];
                double d3 = tmp[x, 3];
                double d4 = tmp[x, 4];
                double d5 = tmp[x, 5];
                double d6 = tmp[x, 6];
                double d7 = tmp[x, 7];

                double tmp0 = d0 + d7;
                double tmp7 = d0 - d7;
                double tmp1 = d1 + d6;
                double tmp6 = d1 - d6;
                double tmp2 = d2 + d5;
                double tmp5 = d2 - d5;
                double tmp3 = d3 + d4;
                double tmp4 = d3 - d4;

                double tmp10 = tmp0 + tmp3;
                double tmp13 = tmp0 - tmp3;
                double tmp11 = tmp1 + tmp2;
                double tmp12 = tmp1 - tmp2;

                outp[x, 0] = (tmp10 + tmp11) * 0.125;
                outp[x, 4] = (tmp10 - tmp11) * 0.125;

                double z1 = (tmp12 + tmp13) * 0.70710678;
                outp[x, 2] = (tmp13 + z1) * 0.125;
                outp[x, 6] = (tmp13 - z1) * 0.125;

                tmp10 = tmp4 + tmp5;
                tmp11 = tmp5 + tmp6;
                tmp12 = tmp6 + tmp7;

                double z5 = (tmp10 - tmp12) * 0.38268343;
                double z2 = 0.54119610 * tmp10 + z5;
                double z4 = 1.30656296 * tmp12 + z5;
                double z3 = tmp11 * 0.70710678;

                double z11 = tmp7 + z3;
                double z13 = tmp7 - z3;

                outp[x, 5] = (z13 + z2) * 0.125;
                outp[x, 3] = (z13 - z2) * 0.125;
                outp[x, 1] = (z11 + z4) * 0.125;
                outp[x, 7] = (z11 - z4) * 0.125;
            }

            return outp;
        }


        private static int MagnitudeSize(int v)
        {
            int a = Math.Abs(v);
            if (a == 0) return 0;
            int size = 0;
            while (a > 0) { size++; a >>= 1; }
            return size;
        }

        private static uint EncodeMagnitudeBits(int v, int size)
        {
            if (size == 0) return 0;
            if (v >= 0) return (uint)v;
            return (uint)((1 << size) - 1 + v); // 负数为补码样式（取正数位反）
        }
        
        /// <summary>
        /// 写入标记
        /// </summary>
        private void WriteMarker(byte prefix, byte marker)
        {
            bitWriter!.WriteByte(prefix);
            bitWriter.WriteByte(marker);
        }
        
        /// <summary>
        /// 初始化量化表
        /// </summary>
        private void InitializeQuantizationTables()
        {
            // 标准JPEG亮度量化表
            byte[] stdLuminanceTable = {
                16, 11, 10, 16, 24, 40, 51, 61,
                12, 12, 14, 19, 26, 58, 60, 55,
                14, 13, 16, 24, 40, 57, 69, 56,
                14, 17, 22, 29, 51, 87, 80, 62,
                18, 22, 37, 56, 68, 109, 103, 77,
                24, 35, 55, 64, 81, 104, 113, 92,
                49, 64, 78, 87, 103, 121, 120, 101,
                72, 92, 95, 98, 112, 100, 103, 99
            };
            
            // 标准JPEG色度量化表
            byte[] stdChrominanceTable = {
                17, 18, 24, 47, 99, 99, 99, 99,
                18, 21, 26, 66, 99, 99, 99, 99,
                24, 26, 56, 99, 99, 99, 99, 99,
                47, 66, 99, 99, 99, 99, 99, 99,
                99, 99, 99, 99, 99, 99, 99, 99,
                99, 99, 99, 99, 99, 99, 99, 99,
                99, 99, 99, 99, 99, 99, 99, 99,
                99, 99, 99, 99, 99, 99, 99, 99
            };
            
            // 根据质量因子调整量化表
            ScaleQuantizationTable(stdLuminanceTable, LuminanceQuantTable, Quality);
            ScaleQuantizationTable(stdChrominanceTable, ChrominanceQuantTable, Quality);
        }
        
        /// <summary>
        /// 根据质量因子缩放量化表
        /// </summary>
        private void ScaleQuantizationTable(byte[] source, byte[] dest, int quality)
        {
            int scaleFactor;
            if (quality < 50)
            {
                scaleFactor = 5000 / quality;
            }
            else
            {
                scaleFactor = 200 - quality * 2;
            }
            
            for (int i = 0; i < 64; i++)
            {
                int value = (source[i] * scaleFactor + 50) / 100;
                dest[i] = (byte)Math.Max(1, Math.Min(255, value));
            }
        }
        
        /// <summary>
        /// 初始化霍夫曼表
        /// </summary>
        private void InitializeHuffmanTables()
        {
            // 标准JPEG霍夫曼表
            LuminanceDCHuffman.InitializeStandardDC(true);
            LuminanceACHuffman.InitializeStandardAC(true);
            ChrominanceDCHuffman.InitializeStandardDC(false);
            ChrominanceACHuffman.InitializeStandardAC(false);
        }
    }
    
    /// <summary>
    /// 霍夫曼编码器
    /// </summary>
    public class HuffmanEncoder
    {
        private byte[] lengths = new byte[16];
        private byte[] symbols = Array.Empty<byte>();
        private ushort[] codesBySymbol = new ushort[256];
        private byte[] codeLengthsBySymbol = new byte[256];
        
        public void InitializeStandardDC(bool isLuminance)
        {
            if (isLuminance)
            {
                lengths = new byte[] { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
                symbols = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
            }
            else
            {
                lengths = new byte[] { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
                symbols = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
            }
        }
        
        public void InitializeStandardAC(bool isLuminance)
        {
            if (isLuminance)
            {
                lengths = new byte[] { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 125 };
                symbols = new byte[] {
                    0x01,0x02,0x03,0x00,0x04,0x11,0x05,0x12,0x21,0x31,0x41,0x06,0x13,0x51,0x61,0x07,
                    0x22,0x71,0x14,0x32,0x81,0x91,0xA1,0x08,0x23,0x42,0xB1,0xC1,0x15,0x52,0xD1,0xF0,
                    0x24,0x33,0x62,0x72,0x82,0x09,0x0A,0x16,0x17,0x18,0x19,0x1A,0x25,0x26,0x27,0x28,
                    0x29,0x2A,0x34,0x35,0x36,0x37,0x38,0x39,0x3A,0x43,0x44,0x45,0x46,0x47,0x48,0x49,
                    0x4A,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5A,0x63,0x64,0x65,0x66,0x67,0x68,0x69,
                    0x6A,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7A,0x83,0x84,0x85,0x86,0x87,0x88,0x89,
                    0x8A,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9A,0xA2,0xA3,0xA4,0xA5,0xA6,0xA7,
                    0xA8,0xA9,0xAA,0xB2,0xB3,0xB4,0xB5,0xB6,0xB7,0xB8,0xB9,0xBA,0xC2,0xC3,0xC4,0xC5,
                    0xC6,0xC7,0xC8,0xC9,0xCA,0xD2,0xD3,0xD4,0xD5,0xD6,0xD7,0xD8,0xD9,0xDA,0xE1,0xE2,
                    0xE3,0xE4,0xE5,0xE6,0xE7,0xE8,0xE9,0xEA,0xF1,0xF2,0xF3,0xF4,0xF5,0xF6,0xF7,0xF8,
                    0xF9,0xFA
                };
            }
            else
            {
                lengths = new byte[] { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 119 };
                symbols = new byte[] {
                    0x00,0x01,0x02,0x03,0x11,0x04,0x05,0x21,0x31,0x06,0x12,0x41,0x51,0x07,0x61,0x71,
                    0x13,0x22,0x32,0x81,0x08,0x14,0x42,0x91,0xA1,0xB1,0xC1,0x09,0x23,0x33,0x52,0xF0,
                    0x15,0x62,0x72,0xD1,0x0A,0x16,0x24,0x34,0xE1,0x25,0xF1,0x17,0x18,0x19,0x1A,0x26,
                    0x27,0x28,0x29,0x2A,0x35,0x36,0x37,0x38,0x39,0x3A,0x43,0x44,0x45,0x46,0x47,0x48,
                    0x49,0x4A,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5A,0x63,0x64,0x65,0x66,0x67,0x68,
                    0x69,0x6A,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7A,0x82,0x83,0x84,0x85,0x86,0x87,
                    0x88,0x89,0x8A,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9A,0xA2,0xA3,0xA4,0xA5,
                    0xA6,0xA7,0xA8,0xA9,0xAA,0xB2,0xB3,0xB4,0xB5,0xB6,0xB7,0xB8,0xB9,0xBA,0xC2,0xC3,
                    0xC4,0xC5,0xC6,0xC7,0xC8,0xC9,0xCA,0xD2,0xD3,0xD4,0xD5,0xD6,0xD7,0xD8,0xD9,0xDA,
                    0xE2,0xE3,0xE4,0xE5,0xE6,0xE7,0xE8,0xE9,0xEA,0xF2,0xF3,0xF4,0xF5,0xF6,0xF7,0xF8,
                    0xF9,0xFA
                };
            }
        }
        
        public (byte[] lengths, byte[] symbols) GetTableData()
        {
            return (lengths, symbols);
        }

        public void BuildCodes()
        {
            Array.Clear(codesBySymbol, 0, codesBySymbol.Length);
            Array.Clear(codeLengthsBySymbol, 0, codeLengthsBySymbol.Length);
            int k = 0;
            int code = 0;
            for (int len = 1; len <= 16; len++)
            {
                code <<= 1;
                int cnt = lengths[len - 1];
                for (int i = 0; i < cnt; i++)
                {
                    if (k >= symbols.Length) break;
                    byte sym = symbols[k++];
                    codesBySymbol[sym] = (ushort)code;
                    codeLengthsBySymbol[sym] = (byte)len;
                    code++;
                }
            }
        }

        public void EncodeSymbol(BitWriter bw, byte symbol)
        {
            byte len = codeLengthsBySymbol[symbol];
            ushort code = codesBySymbol[symbol];
            bw.WriteBits(code, len);
        }
    }
    
    /// <summary>
    /// 位写入器
    /// </summary>
    public class BitWriter
    {
        private readonly Stream stream;
        private byte currentByte = 0;
        private int bitPosition = 0;
        
        public BitWriter(Stream stream)
        {
            this.stream = stream;
        }
        
        public void WriteByte(byte value)
        {
            Flush();
            stream.WriteByte(value);
        }
        
        public void WriteUInt16(ushort value)
        {
            WriteByte((byte)(value >> 8));
            WriteByte((byte)(value & 0xFF));
        }
        
        public void WriteBytes(byte[] bytes)
        {
            Flush();
            stream.Write(bytes, 0, bytes.Length);
        }
        
        public void WriteBits(uint value, int bitCount)
        {
            for (int i = bitCount - 1; i >= 0; i--)
            {
                bool bit = ((value >> i) & 1) == 1;
                currentByte = (byte)((currentByte << 1) | (bit ? 1 : 0));
                bitPosition++;
                
                if (bitPosition == 8)
                {
                    stream.WriteByte(currentByte);
                    if (currentByte == 0xFF)
                    {
                        stream.WriteByte(0x00); // 字节填充
                    }
                    currentByte = 0;
                    bitPosition = 0;
                }
            }
        }
        
        public void Flush()
        {
            if (bitPosition > 0)
            {
                currentByte <<= (8 - bitPosition);
                stream.WriteByte(currentByte);
                if (currentByte == 0xFF)
                {
                    stream.WriteByte(0x00); // 字节填充
                }
                currentByte = 0;
                bitPosition = 0;
            }
        }
    }

    
}