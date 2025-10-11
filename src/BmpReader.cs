using System;
using System.IO;

namespace JpegBmpConverter
{
    /// <summary>
    /// BMP数据结构
    /// </summary>
    public class BmpData
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int BitsPerPixel { get; set; }
        public byte[] PixelData { get; set; } = Array.Empty<byte>();
        public byte[]? Palette { get; set; }
        public bool IsGrayscale { get; set; }
    }

    /// <summary>
    /// BMP文件读取器，用于解析BMP文件头部和像素数据
    /// 基于BMP文件格式规范实现
    /// </summary>
    public class BmpReader
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int BitsPerPixel { get; private set; }
        public byte[] ImageData { get; private set; } = Array.Empty<byte>();
        public bool IsGrayscale { get; private set; }
        
        // BMP文件头结构
        private struct BmpFileHeader
        {
            public ushort Type;          // 文件类型，应为"BM"
            public uint Size;            // 文件大小
            public ushort Reserved1;     // 保留字段
            public ushort Reserved2;     // 保留字段
            public uint OffBits;         // 像素数据偏移量
        }
        
        // BMP信息头结构
        private struct BmpInfoHeader
        {
            public uint Size;            // 信息头大小
            public int Width;            // 图像宽度
            public int Height;           // 图像高度
            public ushort Planes;        // 颜色平面数
            public ushort BitCount;      // 每像素位数
            public uint Compression;     // 压缩类型
            public uint SizeImage;       // 图像数据大小
            public int XPelsPerMeter;    // 水平分辨率
            public int YPelsPerMeter;    // 垂直分辨率
            public uint ClrUsed;         // 使用的颜色数
            public uint ClrImportant;    // 重要颜色数
        }

        /// <summary>
        /// 读取BMP文件
        /// </summary>
        /// <param name="filePath">BMP文件路径</param>
        /// <returns>BMP数据对象，失败时返回null</returns>
        public BmpData? ReadBmp(string filePath)
        {
            try
            {
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(fileStream);
                
                // 读取文件头
                var fileHeader = ReadFileHeader(reader);
                if (fileHeader.Type != 0x4D42) // "BM"
                {
                    Console.WriteLine("错误：不是有效的BMP文件");
                    return null;
                }
                
                // 读取信息头
                var infoHeader = ReadInfoHeader(reader);
                
                // 验证BMP格式
                if (!ValidateBmpFormat(infoHeader))
                {
                    return null;
                }
                
                // 设置图像属性
                Width = Math.Abs(infoHeader.Width);
                Height = Math.Abs(infoHeader.Height);
                BitsPerPixel = infoHeader.BitCount;
                IsGrayscale = (BitsPerPixel == 8);
                
                // 读取调色板（如果需要）
                byte[]? palette = null;
                if (BitsPerPixel <= 8)
                {
                    palette = ReadPalette(reader, infoHeader);
                }
                
                // 跳转到像素数据
                fileStream.Seek(fileHeader.OffBits, SeekOrigin.Begin);
                
                // 读取像素数据
                ImageData = ReadPixelData(reader, infoHeader, palette);
                
                Console.WriteLine($"成功读取BMP文件: {Width}x{Height}, {BitsPerPixel}位");
                
                // 返回BMP数据对象
                return new BmpData
                {
                    Width = Width,
                    Height = Height,
                    BitsPerPixel = BitsPerPixel,
                    PixelData = ImageData,
                    Palette = palette,
                    IsGrayscale = IsGrayscale
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"读取BMP文件时发生错误: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// 读取BMP文件头
        /// </summary>
        private BmpFileHeader ReadFileHeader(BinaryReader reader)
        {
            return new BmpFileHeader
            {
                Type = reader.ReadUInt16(),
                Size = reader.ReadUInt32(),
                Reserved1 = reader.ReadUInt16(),
                Reserved2 = reader.ReadUInt16(),
                OffBits = reader.ReadUInt32()
            };
        }
        
        /// <summary>
        /// 读取BMP信息头
        /// </summary>
        private BmpInfoHeader ReadInfoHeader(BinaryReader reader)
        {
            return new BmpInfoHeader
            {
                Size = reader.ReadUInt32(),
                Width = reader.ReadInt32(),
                Height = reader.ReadInt32(),
                Planes = reader.ReadUInt16(),
                BitCount = reader.ReadUInt16(),
                Compression = reader.ReadUInt32(),
                SizeImage = reader.ReadUInt32(),
                XPelsPerMeter = reader.ReadInt32(),
                YPelsPerMeter = reader.ReadInt32(),
                ClrUsed = reader.ReadUInt32(),
                ClrImportant = reader.ReadUInt32()
            };
        }
        
        /// <summary>
        /// 验证BMP格式是否支持
        /// </summary>
        private bool ValidateBmpFormat(BmpInfoHeader infoHeader)
        {
            // 检查压缩类型（只支持无压缩）
            if (infoHeader.Compression != 0)
            {
                Console.WriteLine("错误：不支持压缩的BMP文件");
                return false;
            }
            
            // 检查位深度
            if (infoHeader.BitCount != 8 && infoHeader.BitCount != 24 && infoHeader.BitCount != 32)
            {
                Console.WriteLine($"错误：不支持{infoHeader.BitCount}位BMP文件，仅支持8位、24位和32位");
                return false;
            }
            
            // 检查颜色平面数
            if (infoHeader.Planes != 1)
            {
                Console.WriteLine("错误：颜色平面数必须为1");
                return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// 读取调色板
        /// </summary>
        private byte[]? ReadPalette(BinaryReader reader, BmpInfoHeader infoHeader)
        {
            if (infoHeader.BitCount > 8)
                return null;
                
            int paletteSize = (int)(infoHeader.ClrUsed > 0 ? infoHeader.ClrUsed : (1u << infoHeader.BitCount));
            byte[] palette = new byte[paletteSize * 4]; // BGRA格式
            
            reader.Read(palette, 0, palette.Length);
            return palette;
        }
        
        /// <summary>
        /// 读取像素数据
        /// </summary>
        private byte[] ReadPixelData(BinaryReader reader, BmpInfoHeader infoHeader, byte[]? palette)
        {
            int width = Math.Abs(infoHeader.Width);
            int height = Math.Abs(infoHeader.Height);
            bool topDown = infoHeader.Height < 0;
            
            // 计算每行字节数（4字节对齐）
            int bytesPerPixel = infoHeader.BitCount / 8;
            int stride = ((width * infoHeader.BitCount + 31) / 32) * 4;
            
            // 读取原始像素数据
            byte[] rawData = new byte[stride * height];
            reader.Read(rawData, 0, rawData.Length);
            
            // 转换为RGB格式
            byte[] rgbData;
            
            switch (infoHeader.BitCount)
            {
                case 8:
                    rgbData = Convert8BitToRgb(rawData, width, height, stride, palette, topDown);
                    break;
                case 24:
                    rgbData = Convert24BitToRgb(rawData, width, height, stride, topDown);
                    break;
                case 32:
                    rgbData = Convert32BitToRgb(rawData, width, height, stride, topDown);
                    break;
                default:
                    throw new NotSupportedException($"不支持{infoHeader.BitCount}位BMP格式");
            }
            
            return rgbData;
        }
        
        /// <summary>
        /// 转换8位BMP到RGB
        /// </summary>
        private byte[] Convert8BitToRgb(byte[] rawData, int width, int height, int stride, byte[]? palette, bool topDown)
        {
            byte[] rgbData = new byte[width * height * 3];
            
            for (int y = 0; y < height; y++)
            {
                int srcY = topDown ? y : (height - 1 - y);
                int srcOffset = srcY * stride;
                int dstOffset = y * width * 3;
                
                for (int x = 0; x < width; x++)
                {
                    byte paletteIndex = rawData[srcOffset + x];
                    
                    if (palette != null && paletteIndex * 4 + 2 < palette.Length)
                    {
                        // 从调色板获取颜色（BGRA格式）
                        rgbData[dstOffset + x * 3] = palette[paletteIndex * 4 + 2];     // R
                        rgbData[dstOffset + x * 3 + 1] = palette[paletteIndex * 4 + 1]; // G
                        rgbData[dstOffset + x * 3 + 2] = palette[paletteIndex * 4];     // B
                    }
                    else
                    {
                        // 灰度图像
                        rgbData[dstOffset + x * 3] = paletteIndex;     // R
                        rgbData[dstOffset + x * 3 + 1] = paletteIndex; // G
                        rgbData[dstOffset + x * 3 + 2] = paletteIndex; // B
                    }
                }
            }
            
            return rgbData;
        }
        
        /// <summary>
        /// 转换24位BMP到RGB
        /// </summary>
        private byte[] Convert24BitToRgb(byte[] rawData, int width, int height, int stride, bool topDown)
        {
            byte[] rgbData = new byte[width * height * 3];
            
            for (int y = 0; y < height; y++)
            {
                int srcY = topDown ? y : (height - 1 - y);
                int srcOffset = srcY * stride;
                int dstOffset = y * width * 3;
                
                for (int x = 0; x < width; x++)
                {
                    // BMP是BGR格式，转换为RGB
                    rgbData[dstOffset + x * 3] = rawData[srcOffset + x * 3 + 2];     // R
                    rgbData[dstOffset + x * 3 + 1] = rawData[srcOffset + x * 3 + 1]; // G
                    rgbData[dstOffset + x * 3 + 2] = rawData[srcOffset + x * 3];     // B
                }
            }
            
            return rgbData;
        }
        
        /// <summary>
        /// 转换32位BMP到RGB
        /// </summary>
        private byte[] Convert32BitToRgb(byte[] rawData, int width, int height, int stride, bool topDown)
        {
            byte[] rgbData = new byte[width * height * 3];
            
            for (int y = 0; y < height; y++)
            {
                int srcY = topDown ? y : (height - 1 - y);
                int srcOffset = srcY * stride;
                int dstOffset = y * width * 3;
                
                for (int x = 0; x < width; x++)
                {
                    // BMP是BGRA格式，转换为RGB（忽略Alpha通道）
                    rgbData[dstOffset + x * 3] = rawData[srcOffset + x * 4 + 2];     // R
                    rgbData[dstOffset + x * 3 + 1] = rawData[srcOffset + x * 4 + 1]; // G
                    rgbData[dstOffset + x * 3 + 2] = rawData[srcOffset + x * 4];     // B
                }
            }
            
            return rgbData;
        }
        
        /// <summary>
        /// 获取灰度图像数据
        /// </summary>
        public byte[] GetGrayscaleData()
        {
            if (ImageData.Length == 0)
                return Array.Empty<byte>();
                
            byte[] grayscaleData = new byte[Width * Height];
            
            for (int i = 0; i < Width * Height; i++)
            {
                int rgbOffset = i * 3;
                // 使用标准RGB到灰度转换公式
                byte gray = (byte)(0.299 * ImageData[rgbOffset] + 
                                  0.587 * ImageData[rgbOffset + 1] + 
                                  0.114 * ImageData[rgbOffset + 2]);
                grayscaleData[i] = gray;
            }
            
            return grayscaleData;
        }
        
        /// <summary>
        /// 转换像素数据为灰度格式
        /// </summary>
        public byte[] ConvertToGrayscale(byte[] pixelData, int width, int height, int bitsPerPixel)
        {
            byte[] grayscaleData = new byte[width * height];
            
            if (bitsPerPixel == 8)
            {
                // 已经是灰度格式，直接复制
                Array.Copy(pixelData, grayscaleData, Math.Min(pixelData.Length, grayscaleData.Length));
            }
            else if (bitsPerPixel == 24)
            {
                // RGB转灰度
                for (int i = 0; i < width * height; i++)
                {
                    int rgbOffset = i * 3;
                    if (rgbOffset + 2 < pixelData.Length)
                    {
                        byte gray = (byte)(0.299 * pixelData[rgbOffset] + 
                                          0.587 * pixelData[rgbOffset + 1] + 
                                          0.114 * pixelData[rgbOffset + 2]);
                        grayscaleData[i] = gray;
                    }
                }
            }
            else if (bitsPerPixel == 32)
            {
                // RGBA转灰度
                for (int i = 0; i < width * height; i++)
                {
                    int rgbaOffset = i * 4;
                    if (rgbaOffset + 2 < pixelData.Length)
                    {
                        byte gray = (byte)(0.299 * pixelData[rgbaOffset] + 
                                          0.587 * pixelData[rgbaOffset + 1] + 
                                          0.114 * pixelData[rgbaOffset + 2]);
                        grayscaleData[i] = gray;
                    }
                }
            }
            
            return grayscaleData;
        }
        
        /// <summary>
        /// 转换像素数据为RGB格式
        /// </summary>
        public byte[] ConvertToRgb(byte[] pixelData, int width, int height, int bitsPerPixel, byte[]? palette)
        {
            byte[] rgbData = new byte[width * height * 3];
            
            if (bitsPerPixel == 8)
            {
                // 8位索引色转RGB
                if (palette != null)
                {
                    for (int i = 0; i < width * height; i++)
                    {
                        if (i < pixelData.Length)
                        {
                            int paletteIndex = pixelData[i] * 4; // 每个调色板条目4字节(BGRA)
                            if (paletteIndex + 2 < palette.Length)
                            {
                                rgbData[i * 3] = palette[paletteIndex + 2];     // R
                                rgbData[i * 3 + 1] = palette[paletteIndex + 1]; // G
                                rgbData[i * 3 + 2] = palette[paletteIndex];     // B
                            }
                        }
                    }
                }
                else
                {
                    // 灰度转RGB
                    for (int i = 0; i < width * height; i++)
                    {
                        if (i < pixelData.Length)
                        {
                            byte gray = pixelData[i];
                            rgbData[i * 3] = gray;     // R
                            rgbData[i * 3 + 1] = gray; // G
                            rgbData[i * 3 + 2] = gray; // B
                        }
                    }
                }
            }
            else if (bitsPerPixel == 24)
            {
                // 已经是RGB格式，直接复制
                Array.Copy(pixelData, rgbData, Math.Min(pixelData.Length, rgbData.Length));
            }
            else if (bitsPerPixel == 32)
            {
                // RGBA转RGB
                for (int i = 0; i < width * height; i++)
                {
                    int rgbaOffset = i * 4;
                    int rgbOffset = i * 3;
                    if (rgbaOffset + 2 < pixelData.Length && rgbOffset + 2 < rgbData.Length)
                    {
                        rgbData[rgbOffset] = pixelData[rgbaOffset];         // R
                        rgbData[rgbOffset + 1] = pixelData[rgbaOffset + 1]; // G
                        rgbData[rgbOffset + 2] = pixelData[rgbaOffset + 2]; // B
                    }
                }
            }
            
            return rgbData;
        }
    }
}