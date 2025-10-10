using System;
using System.IO;

namespace JpegToBmpConverter
{
    /// <summary>
    /// BMP转JPEG转换器辅助类
    /// 基于Pillow库JpegEncode.c实现
    /// </summary>
    public class BmpToJpegProgram
    {
        public static void ConvertBmpToJpeg(string[] args)
        {
            Console.WriteLine("=== BMP转JPEG转换器 ===");
            Console.WriteLine("基于Pillow库JpegEncode.c实现");
            Console.WriteLine();
            
            
            if (args.Length < 2)
            {
                ShowUsage();
                return;
            }
            
            string inputFile = args[0];
            string outputFile = args[1];
            int quality = 75; // 默认质量
            
            // 解析质量参数
            if (args.Length >= 3)
            {
                if (!int.TryParse(args[2], out quality) || quality < 1 || quality > 100)
                {
                    Console.WriteLine("错误：质量参数必须是1-100之间的整数");
                    return;
                }
            }
            
            // 验证文件扩展名
            if (!inputFile.ToLower().EndsWith(".bmp"))
            {
                Console.WriteLine("错误：输入文件必须是BMP格式");
                return;
            }
            
            if (!outputFile.ToLower().EndsWith(".jpg") && !outputFile.ToLower().EndsWith(".jpeg"))
            {
                Console.WriteLine("错误：输出文件必须是JPEG格式");
                return;
            }
            
            // 检查输入文件是否存在
            if (!File.Exists(inputFile))
            {
                Console.WriteLine($"错误：输入文件不存在: {inputFile}");
                return;
            }
            
            try
            {
                Console.WriteLine($"正在转换: {inputFile} -> {outputFile}");
                Console.WriteLine($"质量设置: {quality}");
                
                // 读取BMP文件
                var bmpReader = new BmpReader();
                var bmpData = bmpReader.ReadBmp(inputFile);
                
                if (bmpData == null)
                {
                    Console.WriteLine("错误：无法读取BMP文件");
                    return;
                }
                
                Console.WriteLine($"BMP信息: {bmpData.Width}x{bmpData.Height}, {bmpData.BitsPerPixel}位");
                
                // 创建JPEG编码器
                var jpegEncoder = new JpegEncoder
                {
                    Quality = quality
                };
                
                bool success = false;
                
                // 根据BMP格式选择编码方式
                if (bmpData.BitsPerPixel == 8) // 灰度图像
                {
                    var grayscaleData = bmpReader.ConvertToGrayscale(bmpData.PixelData, bmpData.Width, bmpData.Height, bmpData.BitsPerPixel);
                    success = jpegEncoder.EncodeGrayscale(grayscaleData, bmpData.Width, bmpData.Height, outputFile);
                }
                else // 彩色图像
                {
                    var rgbData = bmpReader.ConvertToRgb(bmpData.PixelData, bmpData.Width, bmpData.Height, bmpData.BitsPerPixel, bmpData.Palette);
                    success = jpegEncoder.EncodeRgb(rgbData, bmpData.Width, bmpData.Height, outputFile);
                }
                
                if (success)
                {
                    var inputSize = new FileInfo(inputFile).Length;
                    var outputSize = new FileInfo(outputFile).Length;
                    double compressionRatio = (double)inputSize / outputSize;
                    
                    Console.WriteLine($"转换成功！");
                    Console.WriteLine($"输入文件大小: {inputSize:N0} 字节");
                    Console.WriteLine($"输出文件大小: {outputSize:N0} 字节");
                    Console.WriteLine($"压缩比: {compressionRatio:F2}:1");
                }
                else
                {
                    Console.WriteLine("转换失败！");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"转换过程中发生错误: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"详细错误: {ex.InnerException.Message}");
                }
            }
        }
        
        /// <summary>
        /// 显示使用说明
        /// </summary>
        private static void ShowUsage()
        {
            Console.WriteLine("用法:");
            Console.WriteLine("  BmpToJpegProgram <输入BMP文件> <输出JPEG文件> [质量]");
            Console.WriteLine();
            Console.WriteLine("参数:");
            Console.WriteLine("  输入BMP文件    - 要转换的BMP图像文件路径");
            Console.WriteLine("  输出JPEG文件   - 输出的JPEG图像文件路径");
            Console.WriteLine("  质量          - JPEG质量 (1-100, 默认75)");
            Console.WriteLine();
            Console.WriteLine("示例:");
            Console.WriteLine("  BmpToJpegProgram input.bmp output.jpg");
            Console.WriteLine("  BmpToJpegProgram input.bmp output.jpg 90");
            Console.WriteLine();
            Console.WriteLine("支持的BMP格式:");
            Console.WriteLine("  - 8位灰度BMP");
            Console.WriteLine("  - 24位RGB BMP");
            Console.WriteLine("  - 32位RGBA BMP");
        }
        
    }
}