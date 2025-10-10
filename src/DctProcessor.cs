using System;

namespace JpegToBmpConverter
{
    /// <summary>
    /// DCT处理器，实现JPEG解码的核心算法
    /// </summary>
    public class DctProcessor
    {
        // DCT系数矩阵大小
        private const int BLOCK_SIZE = 8;
        
        // 预计算的DCT系数
        private static readonly double[,] DCT_COEFFICIENTS = new double[BLOCK_SIZE, BLOCK_SIZE];
        
        // Zigzag扫描顺序
        private static readonly int[] ZIGZAG_ORDER = new int[]
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

        static DctProcessor()
        {
            InitializeDctCoefficients();
        }

        /// <summary>
        /// 初始化DCT系数矩阵
        /// </summary>
        private static void InitializeDctCoefficients()
        {
            for (int i = 0; i < BLOCK_SIZE; i++)
            {
                for (int j = 0; j < BLOCK_SIZE; j++)
                {
                    double ci = (i == 0) ? 1.0 / Math.Sqrt(2.0) : 1.0;
                    double cj = (j == 0) ? 1.0 / Math.Sqrt(2.0) : 1.0;
                    
                    DCT_COEFFICIENTS[i, j] = 0.25 * ci * cj * 
                        Math.Cos((2 * i + 1) * j * Math.PI / 16.0);
                }
            }
        }

        /// <summary>
        /// 执行8x8块的逆DCT变换
        /// </summary>
        /// <param name="dctBlock">DCT系数块</param>
        /// <returns>空间域像素块</returns>
        public static double[,] InverseDct(double[,] dctBlock)
        {
            if (dctBlock.GetLength(0) != BLOCK_SIZE || dctBlock.GetLength(1) != BLOCK_SIZE)
            {
                throw new ArgumentException("DCT块必须是8x8大小");
            }

            double[,] result = new double[BLOCK_SIZE, BLOCK_SIZE];

            for (int x = 0; x < BLOCK_SIZE; x++)
            {
                for (int y = 0; y < BLOCK_SIZE; y++)
                {
                    double sum = 0.0;

                    for (int u = 0; u < BLOCK_SIZE; u++)
                    {
                        for (int v = 0; v < BLOCK_SIZE; v++)
                        {
                            double cu = (u == 0) ? 1.0 / Math.Sqrt(2.0) : 1.0;
                            double cv = (v == 0) ? 1.0 / Math.Sqrt(2.0) : 1.0;

                            sum += cu * cv * dctBlock[u, v] *
                                Math.Cos((2 * x + 1) * u * Math.PI / 16.0) *
                                Math.Cos((2 * y + 1) * v * Math.PI / 16.0);
                        }
                    }

                    result[x, y] = sum * 0.25;
                }
            }

            return result;
        }

        /// <summary>
        /// 快速逆DCT变换（优化版本）
        /// </summary>
        /// <param name="dctBlock">DCT系数块</param>
        /// <returns>空间域像素块</returns>
        public static int[,] FastInverseDct(int[,] dctBlock)
        {
            double[,] temp = new double[BLOCK_SIZE, BLOCK_SIZE];
            double[,] result = new double[BLOCK_SIZE, BLOCK_SIZE];

            // 第一步：对行进行1D IDCT
            for (int i = 0; i < BLOCK_SIZE; i++)
            {
                for (int j = 0; j < BLOCK_SIZE; j++)
                {
                    double sum = 0.0;
                    for (int k = 0; k < BLOCK_SIZE; k++)
                    {
                        double c = (k == 0) ? 1.0 / Math.Sqrt(2.0) : 1.0;
                        sum += c * dctBlock[i, k] * Math.Cos((2 * j + 1) * k * Math.PI / 16.0);
                    }
                    temp[i, j] = sum * 0.5;
                }
            }

            // 第二步：对列进行1D IDCT
            for (int j = 0; j < BLOCK_SIZE; j++)
            {
                for (int i = 0; i < BLOCK_SIZE; i++)
                {
                    double sum = 0.0;
                    for (int k = 0; k < BLOCK_SIZE; k++)
                    {
                        double c = (k == 0) ? 1.0 / Math.Sqrt(2.0) : 1.0;
                        sum += c * temp[k, j] * Math.Cos((2 * i + 1) * k * Math.PI / 16.0);
                    }
                    result[i, j] = sum * 0.5;
                }
            }

            // 转换为整数并限制范围
            int[,] intResult = new int[BLOCK_SIZE, BLOCK_SIZE];
            for (int i = 0; i < BLOCK_SIZE; i++)
            {
                for (int j = 0; j < BLOCK_SIZE; j++)
                {
                    int value = (int)Math.Round(result[i, j] + 128); // 加128是因为JPEG使用-128到127的范围
                    intResult[i, j] = Math.Max(0, Math.Min(255, value));
                }
            }

            return intResult;
        }

        /// <summary>
        /// 反量化DCT系数
        /// </summary>
        /// <param name="quantizedBlock">量化后的DCT系数</param>
        /// <param name="quantTable">量化表</param>
        /// <returns>反量化后的DCT系数</returns>
        public static int[,] Dequantize(int[,] quantizedBlock, int[,] quantTable)
        {
            int[,] result = new int[BLOCK_SIZE, BLOCK_SIZE];

            for (int i = 0; i < BLOCK_SIZE; i++)
            {
                for (int j = 0; j < BLOCK_SIZE; j++)
                {
                    result[i, j] = quantizedBlock[i, j] * quantTable[i, j];
                }
            }

            return result;
        }

        /// <summary>
        /// Zigzag反扫描，将一维数组转换为8x8块
        /// </summary>
        /// <param name="zigzagData">Zigzag扫描的一维数据</param>
        /// <returns>8x8块数据</returns>
        public static int[,] ZigzagToBlock(int[] zigzagData)
        {
            if (zigzagData.Length != 64)
            {
                throw new ArgumentException("Zigzag数据必须包含64个元素");
            }

            int[,] block = new int[BLOCK_SIZE, BLOCK_SIZE];

            for (int i = 0; i < 64; i++)
            {
                int pos = ZIGZAG_ORDER[i];
                int row = pos / BLOCK_SIZE;
                int col = pos % BLOCK_SIZE;
                block[row, col] = zigzagData[i];
            }

            return block;
        }

        /// <summary>
        /// 将8x8块转换为Zigzag扫描的一维数组
        /// </summary>
        /// <param name="block">8x8块数据</param>
        /// <returns>Zigzag扫描的一维数据</returns>
        public static int[] BlockToZigzag(int[,] block)
        {
            int[] zigzagData = new int[64];

            for (int i = 0; i < 64; i++)
            {
                int pos = ZIGZAG_ORDER[i];
                int row = pos / BLOCK_SIZE;
                int col = pos % BLOCK_SIZE;
                zigzagData[i] = block[row, col];
            }

            return zigzagData;
        }

        /// <summary>
        /// YCbCr到RGB颜色空间转换
        /// </summary>
        /// <param name="y">亮度分量</param>
        /// <param name="cb">蓝色色度分量</param>
        /// <param name="cr">红色色度分量</param>
        /// <returns>RGB值</returns>
        public static (byte r, byte g, byte b) YCbCrToRgb(int y, int cb, int cr)
        {
            // JPEG标准的YCbCr到RGB转换公式
            double r = y + 1.402 * (cr - 128);
            double g = y - 0.344136 * (cb - 128) - 0.714136 * (cr - 128);
            double b = y + 1.772 * (cb - 128);

            // 限制到0-255范围
            byte rByte = (byte)Math.Max(0, Math.Min(255, Math.Round(r)));
            byte gByte = (byte)Math.Max(0, Math.Min(255, Math.Round(g)));
            byte bByte = (byte)Math.Max(0, Math.Min(255, Math.Round(b)));

            return (rByte, gByte, bByte);
        }

        /// <summary>
        /// RGB到YCbCr颜色空间转换
        /// </summary>
        /// <param name="r">红色分量</param>
        /// <param name="g">绿色分量</param>
        /// <param name="b">蓝色分量</param>
        /// <returns>YCbCr值</returns>
        public static (byte y, byte cb, byte cr) RgbToYCbCr(byte r, byte g, byte b)
        {
            // JPEG标准的RGB到YCbCr转换公式
            double y = 0.299 * r + 0.587 * g + 0.114 * b;
            double cb = -0.168736 * r - 0.331264 * g + 0.5 * b + 128;
            double cr = 0.5 * r - 0.418688 * g - 0.081312 * b + 128;

            // 限制到0-255范围
            byte yByte = (byte)Math.Max(0, Math.Min(255, Math.Round(y)));
            byte cbByte = (byte)Math.Max(0, Math.Min(255, Math.Round(cb)));
            byte crByte = (byte)Math.Max(0, Math.Min(255, Math.Round(cr)));

            return (yByte, cbByte, crByte);
        }

        /// <summary>
        /// 上采样色度分量（从4:2:0到4:4:4）
        /// </summary>
        /// <param name="chromaBlock">色度块</param>
        /// <param name="targetWidth">目标宽度</param>
        /// <param name="targetHeight">目标高度</param>
        /// <returns>上采样后的色度数据</returns>
        public static byte[,] UpsampleChroma(byte[,] chromaBlock, int targetWidth, int targetHeight)
        {
            int sourceWidth = chromaBlock.GetLength(1);
            int sourceHeight = chromaBlock.GetLength(0);
            
            byte[,] result = new byte[targetHeight, targetWidth];

            for (int y = 0; y < targetHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    // 简单的最近邻插值
                    int sourceX = Math.Min(x / 2, sourceWidth - 1);
                    int sourceY = Math.Min(y / 2, sourceHeight - 1);
                    result[y, x] = chromaBlock[sourceY, sourceX];
                }
            }

            return result;
        }

        /// <summary>
        /// 创建标准JPEG量化表
        /// </summary>
        /// <param name="quality">质量因子（1-100）</param>
        /// <param name="isLuminance">是否为亮度量化表</param>
        /// <returns>量化表</returns>
        public static int[,] CreateQuantizationTable(int quality, bool isLuminance)
        {
            // 标准JPEG亮度量化表
            int[] standardLuminance = new int[]
            {
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
            int[] standardChrominance = new int[]
            {
                17, 18, 24, 47, 99, 99, 99, 99,
                18, 21, 26, 66, 99, 99, 99, 99,
                24, 26, 56, 99, 99, 99, 99, 99,
                47, 66, 99, 99, 99, 99, 99, 99,
                99, 99, 99, 99, 99, 99, 99, 99,
                99, 99, 99, 99, 99, 99, 99, 99,
                99, 99, 99, 99, 99, 99, 99, 99,
                99, 99, 99, 99, 99, 99, 99, 99
            };

            int[] baseTable = isLuminance ? standardLuminance : standardChrominance;
            int[,] quantTable = new int[BLOCK_SIZE, BLOCK_SIZE];

            // 根据质量因子调整量化表
            double scaleFactor;
            if (quality < 50)
            {
                scaleFactor = 5000.0 / quality;
            }
            else
            {
                scaleFactor = 200.0 - 2.0 * quality;
            }

            for (int i = 0; i < 64; i++)
            {
                int row = i / BLOCK_SIZE;
                int col = i % BLOCK_SIZE;
                
                int value = (int)((baseTable[i] * scaleFactor + 50) / 100);
                quantTable[row, col] = Math.Max(1, Math.Min(255, value));
            }

            return quantTable;
        }
    }
}