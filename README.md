# JpegBmp Codec

> ⚠️ 重要说明：本项目完全由 AI 生成  
> 本项目的代码与文档由 AI 自动生成，供学习与研究使用。

**平台与技术：C# / .NET 8，跨平台（Windows / Linux / macOS）**

纯C#实现的 JPEG 解码与 BMP→JPEG 编码工具，支持在不依赖 System.Drawing 等图像库的情况下完成 JPEG↔BMP 双向转换。解码部分参考了 Pillow 项目的 `JpegDecode.c`，编码部分实现了标准的 DCT、量化、ZigZag 和霍夫曼编码流程。

## 特性

- 纯C#实现：不使用 System.Drawing、ImageSharp、Skia 等第三方图像库
- .NET 8 跨平台：Windows/Linux/macOS 下均可运行
- 基于标准：参考 Pillow 的 `JpegDecode.c`，遵循 JPEG 基线标准（SOF0）
- 完整解码：JPEG 审阅、霍夫曼解码、反量化、IDCT、颜色空间转换（YCbCr→RGB）
- 基本编码：BMP→JPEG 编码（DCT、量化、ZigZag、霍夫曼编码），可调质量（1–100，默认 75）
- 多种格式：支持灰度和彩色图像
- 格式输出：输出标准 BMP 或 JPEG 文件

## 项目结构

```
JpegBmpCodec/
├── src/
│   ├── Program.cs                 # 主程序入口与命令行解析
│   ├── JpegDecoder.cs             # JPEG 解码器核心（解析/反量化/IDCT/颜色转换）
│   ├── JpegEncoder.cs             # JPEG 编码器核心（DCT/量化/ZigZag/霍夫曼编码）
│   ├── HuffmanTable.cs            # 霍夫曼表构建与码字信息
│   ├── HuffmanDecoder.cs          # 霍夫曼位流解码器
│   ├── DctProcessor.cs            # DCT/IDCT 与颜色空间相关处理
│   ├── BmpReader.cs               # BMP 文件读取（用于 BMP→JPEG）
│   ├── BmpWriter.cs               # BMP 文件写入（用于 JPEG→BMP）
│   ├── LibjpegTurboStyleDecoder.cs# 另一解码风格（学习/参考用途）
│   ├── PillowStyleJpegDecoder.cs  # Pillow 风格解码器（学习/参考用途）
│   ├── SilentConsole.cs           # 静默/标准输出封装
│   └── JpegToBmpConverter.csproj  # 项目文件
├── README.md
├── README.en.md
└── .gitignore
```

## 核心组件

### 1. 解码核心（JPEG→BMP）
- JPEG 文件格式解析（SOI、APP0、DQT、SOF、DHT、SOS 等）
- 量化表与霍夫曼表提取
- 霍夫曼解码与反量化
- 8×8 块 IDCT 与反 ZigZag 排列
- YCbCr 到 RGB 转换与色度上采样
- BMP 文件写出（24bpp/8bpp）

### 2. 编码核心（BMP→JPEG）
- BMP 读取与像素格式处理
- 8×8 块 DCT 与量化（质量可调）
- ZigZag 扫描与熵编码（霍夫曼编码）
- JPEG 基线文件写出（JFIF/量化表/霍夫曼表/帧/扫描段）

## 编译和运行

### 前提条件
- .NET SDK 8.0 或更高版本（推荐使用 .NET CLI 或 Visual Studio 2022）

### 编译
```bash
cd src
dotnet build -c Release
```

### 运行
```bash
# 显示帮助（无参数时打印用法）
dotnet run --project src/JpegToBmpConverter.csproj --

# JPEG → BMP
dotnet run --project src/JpegToBmpConverter.csproj -- input.jpg output.bmp

# BMP → JPEG（可选质量，默认 75）
dotnet run --project src/JpegToBmpConverter.csproj -- input.bmp output.jpg 75
```

## 使用方法

### 命令行语法
```
# JPEG → BMP
JpegBmpCodec.exe <输入JPEG文件> <输出BMP文件>

# BMP → JPEG（可选质量）
JpegBmpCodec.exe <输入BMP文件> <输出JPEG文件> [质量]
```

### 示例（Windows/PowerShell）
```powershell
# 单文件转换
dotnet run --project src/JpegToBmpConverter.csproj -- photo.jpg photo.bmp
dotnet run --project src/JpegToBmpConverter.csproj -- sample.bmp sample.jpg 80

# 批量 JPEG → BMP 转换
Get-ChildItem -Filter *.jpg | ForEach-Object {
  $dst = [System.IO.Path]::ChangeExtension($_.FullName, 'bmp')
  dotnet run --project src/JpegToBmpConverter.csproj -- $_.FullName $dst
}

# 批量 BMP → JPEG 转换（质量 75）
Get-ChildItem -Filter *.bmp | ForEach-Object {
  $dst = [System.IO.Path]::ChangeExtension($_.FullName, 'jpg')
  dotnet run --project src/JpegToBmpConverter.csproj -- $_.FullName $dst 75
}
```

## 技术实现

### JPEG 解码流程（JPEG→BMP）
1. 文件头验证：检查 SOI 标记
2. 段解析：解析 APP0、DQT、SOF、DHT、SOS 等段
3. 量化/霍夫曼表提取：读取亮度与色度表
4. 图像数据解码：霍夫曼解码与反量化
5. 逆变换与重排：8×8 IDCT 与 ZigZag 反序
6. 颜色转换：YCbCr → RGB，上采样色度
7. BMP 输出：写出标准 BMP 文件

### JPEG 编码流程（BMP→JPEG）
1. 像素读取：读取 BMP 并规范化像素格式
2. 块处理：分块为 8×8，进行 DCT
3. 量化：按质量参数选择量化强度
4. ZigZag：将 8×8 系数按 ZigZag 顺序线性化
5. 熵编码：DC/AC 系数进行霍夫曼编码
6. 文件生成：写出 JFIF、DQT、DHT、SOF、SOS 与扫描数据

### 关键算法
- 霍夫曼解码/编码：码表生成与位流处理
- DCT/IDCT：8×8 块的变换与逆变换
- 量化与反量化：基于 JPEG 标准量化表（随质量缩放）
- 颜色空间：YCbCr 与 RGB 转换

## 限制和注意事项

1. 简化实现：以教学示例为主，部分 JPEG 特性未覆盖（如渐进式）
2. 性能：以正确性为主，尚未做深入优化
3. 兼容性：支持基线 JPEG（SOF0）；对渐进式/多扫描支持有限
4. 编码能力：当前为基本编码流程，质量可调；子采样与高级特性支持有限
5. 错误处理：覆盖常见路径，复杂错误场景有待增强

## 参考资料

- [JPEG标准 (ITU-T T.81)](https://www.itu.int/rec/T-REC-T.81)
- [Pillow库 JpegDecode.c](https://github.com/python-pillow/Pillow/blob/main/src/libImaging/JpegDecode.c)
- [BMP文件格式规范](https://en.wikipedia.org/wiki/BMP_file_format)

## 许可证

本项目仅用于学习和研究目的。参考了Pillow库的实现思路，遵循相应的开源许可证要求。

## 贡献

欢迎提交问题报告和改进建议。如需扩展功能，请确保：
1. 不引入第三方图像处理库
2. 保持代码的教育性和可读性
3. 添加适当的注释和文档