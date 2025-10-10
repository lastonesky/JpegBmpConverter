using System;
using System.IO;

namespace JpegToBmpConverter
{
    /// <summary>
    /// BMP文件写入器
    /// </summary>
    public class BmpWriter
    {
        /// <summary>
        /// 将图像数据写入BMP文件
        /// </summary>
        /// <param name="imageData">图像数据（RGB格式）</param>
        /// <param name="width">图像宽度</param>
        /// <param name="height">图像高度</param>
        /// <param name="outputPath">输出文件路径</param>
        /// <returns>写入是否成功</returns>
        public static bool WriteBmp(byte[,] imageData, int width, int height, string outputPath)
        {
            using (var fileStream = new FileStream(outputPath, FileMode.Create))
            using (var writer = new BinaryWriter(fileStream))
            {
                // 计算行填充
                int rowPadding = (4 - (width * 3) % 4) % 4;
                int rowSize = width * 3 + rowPadding;
                int imageSize = rowSize * height;
                int fileSize = 54 + imageSize; // 54字节头部 + 图像数据

                // BMP文件头（14字节）
                writer.Write((byte)'B');
                writer.Write((byte)'M');
                writer.Write(fileSize);        // 文件大小
                writer.Write((short)0);       // 保留字段1
                writer.Write((short)0);       // 保留字段2
                writer.Write(54);             // 数据偏移量

                // BMP信息头（40字节）
                writer.Write(40);             // 信息头大小
                writer.Write(width);          // 图像宽度
                writer.Write(height);         // 图像高度
                writer.Write((short)1);       // 颜色平面数
                writer.Write((short)24);      // 每像素位数
                writer.Write(0);              // 压缩方式（0=不压缩）
                writer.Write(imageSize);      // 图像数据大小
                writer.Write(2835);           // 水平分辨率（像素/米）
                writer.Write(2835);           // 垂直分辨率（像素/米）
                writer.Write(0);              // 颜色表中颜色数
                writer.Write(0);              // 重要颜色数

                // 写入图像数据（BMP格式是从下到上，从左到右，BGR顺序）
                for (int y = height - 1; y >= 0; y--)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // RGB转BGR
                        byte r = imageData[y, x * 3];
                        byte g = imageData[y, x * 3 + 1];
                        byte b = imageData[y, x * 3 + 2];

                        writer.Write(b); // B
                        writer.Write(g); // G
                        writer.Write(r); // R
                    }

                    // 写入行填充
                    for (int i = 0; i < rowPadding; i++)
                    {
                        writer.Write((byte)0);
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// 将灰度图像数据写入BMP文件
        /// </summary>
        /// <param name="imageData">灰度图像数据</param>
        /// <param name="width">图像宽度</param>
        /// <param name="height">图像高度</param>
        /// <param name="outputPath">输出文件路径</param>
        /// <returns>写入是否成功</returns>
        public static bool WriteGrayscaleBmp(byte[,] imageData, int width, int height, string outputPath)
        {
            using (var fileStream = new FileStream(outputPath, FileMode.Create))
            using (var writer = new BinaryWriter(fileStream))
            {
                // 计算行填充
                int rowPadding = (4 - width % 4) % 4;
                int rowSize = width + rowPadding;
                int imageSize = rowSize * height;
                int paletteSize = 256 * 4; // 256色调色板，每色4字节
                int fileSize = 54 + paletteSize + imageSize;

                // BMP文件头（14字节）
                writer.Write((byte)'B');
                writer.Write((byte)'M');
                writer.Write(fileSize);        // 文件大小
                writer.Write((short)0);       // 保留字段1
                writer.Write((short)0);       // 保留字段2
                writer.Write(54 + paletteSize); // 数据偏移量

                // BMP信息头（40字节）
                writer.Write(40);             // 信息头大小
                writer.Write(width);          // 图像宽度
                writer.Write(height);         // 图像高度
                writer.Write((short)1);       // 颜色平面数
                writer.Write((short)8);       // 每像素位数
                writer.Write(0);              // 压缩方式（0=不压缩）
                writer.Write(imageSize);      // 图像数据大小
                writer.Write(2835);           // 水平分辨率（像素/米）
                writer.Write(2835);           // 垂直分辨率（像素/米）
                writer.Write(256);            // 颜色表中颜色数
                writer.Write(0);              // 重要颜色数

                // 写入灰度调色板
                for (int i = 0; i < 256; i++)
                {
                    writer.Write((byte)i); // B
                    writer.Write((byte)i); // G
                    writer.Write((byte)i); // R
                    writer.Write((byte)0); // 保留
                }

                // 写入图像数据（BMP格式是从下到上）
                for (int y = height - 1; y >= 0; y--)
                {
                    for (int x = 0; x < width; x++)
                    {
                        writer.Write(imageData[y, x]);
                    }

                    // 写入行填充
                    for (int i = 0; i < rowPadding; i++)
                    {
                        writer.Write((byte)0);
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// 创建测试用的彩色BMP图像
        /// </summary>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="outputPath">输出路径</param>
        public static void CreateTestColorBmp(int width, int height, string outputPath)
        {
            var imageData = new byte[height, width * 3];

            // 创建彩色渐变图像
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // 创建彩虹渐变效果
                    float hue = (float)x / width * 360f;
                    var (r, g, b) = HsvToRgb(hue, 1.0f, 1.0f);

                    imageData[y, x * 3] = (byte)(r * 255);     // R
                    imageData[y, x * 3 + 1] = (byte)(g * 255); // G
                    imageData[y, x * 3 + 2] = (byte)(b * 255); // B
                }
            }

            WriteBmp(imageData, width, height, outputPath);
        }

        /// <summary>
        /// HSV转RGB颜色空间转换
        /// </summary>
        private static (float r, float g, float b) HsvToRgb(float h, float s, float v)
        {
            float c = v * s;
            float x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            float m = v - c;

            float r, g, b;

            if (h >= 0 && h < 60)
            {
                r = c; g = x; b = 0;
            }
            else if (h >= 60 && h < 120)
            {
                r = x; g = c; b = 0;
            }
            else if (h >= 120 && h < 180)
            {
                r = 0; g = c; b = x;
            }
            else if (h >= 180 && h < 240)
            {
                r = 0; g = x; b = c;
            }
            else if (h >= 240 && h < 300)
            {
                r = x; g = 0; b = c;
            }
            else
            {
                r = c; g = 0; b = x;
            }

            return (r + m, g + m, b + m);
        }

        /// <summary>
        /// 创建测试用的灰度BMP图像
        /// </summary>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="outputPath">输出路径</param>
        public static void CreateTestGrayscaleBmp(int width, int height, string outputPath)
        {
            var imageData = new byte[height, width];

            // 创建灰度渐变图像
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // 创建对角线渐变效果
                    imageData[y, x] = (byte)((x + y) * 255 / (width + height - 2));
                }
            }

            WriteGrayscaleBmp(imageData, width, height, outputPath);
        }
    }
}