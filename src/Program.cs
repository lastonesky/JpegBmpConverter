using System;
using System.IO;

namespace JpegBmpConverter
{
    /// <summary>
    /// 图像转换器主程序
    /// 支持JPEG转BMP和BMP转JPEG两种模式
    /// 基于Pillow库JpegDecode.c和JpegEncode.c实现
    /// </summary>
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== 图像转换器 ===");
            Console.WriteLine("支持JPEG ↔ BMP双向转换");
            Console.WriteLine("基于Pillow库实现");
            Console.WriteLine();

            // 调试/测试命令已移除，保留核心转换功能
            
            if (args.Length < 2)
            {
                ShowUsage();
                return;
            }
            
            string inputFile = args[0];
            string outputFile = args[1];
            
            // 检查输入文件是否存在
            if (!File.Exists(inputFile))
            {
                Console.WriteLine($"错误：输入文件不存在: {inputFile}");
                return;
            }
            
            // 根据文件扩展名确定转换模式
            string inputExt = Path.GetExtension(inputFile).ToLower();
            string outputExt = Path.GetExtension(outputFile).ToLower();
            
            try
            {
                if ((inputExt == ".jpg" || inputExt == ".jpeg") && outputExt == ".bmp")
                {
                    // JPEG转BMP模式
                    ConvertJpegToBmp(inputFile, outputFile);
                }
                else if (inputExt == ".bmp" && (outputExt == ".jpg" || outputExt == ".jpeg"))
                {
                    // BMP转JPEG模式
                    int quality = 75; // 默认质量
                    if (args.Length >= 3)
                    {
                        if (!int.TryParse(args[2], out quality) || quality < 1 || quality > 100)
                        {
                            Console.WriteLine("错误：质量参数必须是1-100之间的整数");
                            return;
                        }
                    }
                    ConvertBmpToJpeg(inputFile, outputFile, quality);
                }
                else
                {
                    Console.WriteLine("错误：不支持的文件格式组合");
                    Console.WriteLine("支持的转换:");
                    Console.WriteLine("  JPEG -> BMP: .jpg/.jpeg -> .bmp");
                    Console.WriteLine("  BMP -> JPEG: .bmp -> .jpg/.jpeg");
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
        /// JPEG转BMP转换
        /// </summary>
        static void ConvertJpegToBmp(string inputFile, string outputFile)
        {
            Console.WriteLine($"JPEG转BMP模式: {inputFile} -> {outputFile}");
            
            // 读取JPEG文件
            byte[] jpegData = File.ReadAllBytes(inputFile);
            
            // 使用Pillow风格JPEG解码器
            Console.WriteLine("\n使用Pillow风格JPEG解码器:");
            var pillowDecoder = new PillowStyleJpegDecoder();
            bool success = pillowDecoder.Decode(jpegData);
            
            if (success)
            {
                Console.WriteLine($"JPEG解码成功: {pillowDecoder.Width}x{pillowDecoder.Height}");
                
                // 根据图像类型选择写入方式
                bool writeSuccess = false;
                if (pillowDecoder.Components == 1) // 灰度图像
                {
                    writeSuccess = BmpWriter.WriteGrayscaleBmp(pillowDecoder.ImageData, pillowDecoder.Width, pillowDecoder.Height, outputFile);
                }
                else // 彩色图像
                {
                    writeSuccess = BmpWriter.WriteBmp(pillowDecoder.ImageData, pillowDecoder.Width, pillowDecoder.Height, outputFile);
                }
                
                if (writeSuccess)
                {
                    Console.WriteLine($"BMP文件写入成功: {outputFile}");
                    ShowFileSizeInfo(inputFile, outputFile);
                }
                else
                {
                    Console.WriteLine("BMP文件写入失败");
                }
            }
            else
            {
                Console.WriteLine("JPEG解码失败");
            }
        }
        
        /// <summary>
        /// BMP转JPEG转换
        /// </summary>
        static void ConvertBmpToJpeg(string inputFile, string outputFile, int quality)
        {
            Console.WriteLine($"BMP转JPEG模式: {inputFile} -> {outputFile}");
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
                Console.WriteLine($"JPEG文件编码成功: {outputFile}");
                ShowFileSizeInfo(inputFile, outputFile);
            }
            else
            {
                Console.WriteLine("JPEG编码失败");
            }
        }
        
        /// <summary>
        /// 显示文件大小信息
        /// </summary>
        static void ShowFileSizeInfo(string inputFile, string outputFile)
        {
            var inputSize = new FileInfo(inputFile).Length;
            var outputSize = new FileInfo(outputFile).Length;
            double ratio = (double)inputSize / outputSize;
            
            Console.WriteLine($"输入文件大小: {inputSize:N0} 字节");
            Console.WriteLine($"输出文件大小: {outputSize:N0} 字节");
            Console.WriteLine($"大小比例: {ratio:F2}:1");
        }

        /// <summary>
        /// 显示使用说明
        /// </summary>
        static void ShowUsage()
        {
            Console.WriteLine("用法:");
            Console.WriteLine("  Program <输入文件> <输出文件> [质量]");
            Console.WriteLine();
            Console.WriteLine("支持的转换模式:");
            Console.WriteLine("  1. JPEG转BMP:");
            Console.WriteLine("     Program input.jpg output.bmp");
            Console.WriteLine("     Program photo.jpeg image.bmp");
            Console.WriteLine();
            Console.WriteLine("  2. BMP转JPEG:");
            Console.WriteLine("     Program input.bmp output.jpg [质量]");
            Console.WriteLine("     Program image.bmp photo.jpeg 90");
            Console.WriteLine();
            Console.WriteLine("参数说明:");
            Console.WriteLine("  质量 - JPEG质量因子 (1-100, 默认75, 仅BMP转JPEG时使用)");
            Console.WriteLine();
            Console.WriteLine("支持的格式:");
            Console.WriteLine("  JPEG: .jpg, .jpeg (基线JPEG, 灰度和彩色)");
            Console.WriteLine("  BMP:  .bmp (8位灰度, 24位RGB, 32位RGBA)");
        }
        






    }
}
