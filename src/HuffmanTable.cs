using System;
using System.Collections.Generic;

namespace JpegBmpConverter
{
    /// <summary>
    /// 霍夫曼表实现，用于JPEG解码
    /// </summary>
    public class HuffmanTable
    {
        private readonly Dictionary<int, byte> codeToSymbol;
        private readonly int[] minCode;
        private readonly int[] maxCode;
        private readonly int[] symbolIndex;
        private readonly byte[] symbols;

        /// <summary>
        /// 构造霍夫曼表
        /// </summary>
        /// <param name="codeLengths">每个码长的符号数量（16个元素）</param>
        /// <param name="symbols">符号数组</param>
        public HuffmanTable(byte[] codeLengths, byte[] symbols)
        {
            if (codeLengths.Length != 16)
            {
                throw new ArgumentException("码长数组必须包含16个元素");
            }

            this.symbols = symbols;
            codeToSymbol = new Dictionary<int, byte>();
            minCode = new int[17];
            maxCode = new int[17];
            symbolIndex = new int[17];

            BuildHuffmanTable(codeLengths);
        }

        /// <summary>
        /// 构建霍夫曼表
        /// </summary>
        private void BuildHuffmanTable(byte[] codeLengths)
        {
            int code = 0;
            int symbolIdx = 0;

            for (int length = 1; length <= 16; length++)
            {
                minCode[length] = code;
                symbolIndex[length] = symbolIdx;

                for (int i = 0; i < codeLengths[length - 1]; i++)
                {
                    if (symbolIdx < symbols.Length)
                    {
                        codeToSymbol[code] = symbols[symbolIdx];
                        code++;
                        symbolIdx++;
                    }
                }

                maxCode[length] = code - 1;
                code <<= 1;
            }
        }

        /// <summary>
        /// 解码霍夫曼符号
        /// </summary>
        /// <param name="bitReader">位读取器</param>
        /// <returns>解码的符号</returns>
        public byte DecodeSymbol(BitReader bitReader)
        {
            int code = 0;

            for (int length = 1; length <= 16; length++)
            {
                code = (code << 1) | bitReader.ReadBit();

                if (code <= maxCode[length] && code >= minCode[length])
                {
                    int index = symbolIndex[length] + (code - minCode[length]);
                    if (index < symbols.Length)
                    {
                        return symbols[index];
                    }
                }
            }

            throw new InvalidOperationException("无法解码霍夫曼符号");
        }

        /// <summary>
        /// 检查是否可以解码指定长度的码
        /// </summary>
        public bool CanDecode(int code, int length)
        {
            if (length < 1 || length > 16)
                return false;

            return code >= minCode[length] && code <= maxCode[length];
        }

        /// <summary>
        /// 获取指定码和长度的符号
        /// </summary>
        public byte GetSymbol(int code, int length)
        {
            if (!CanDecode(code, length))
            {
                throw new ArgumentException("无效的霍夫曼码");
            }

            int index = symbolIndex[length] + (code - minCode[length]);
            return symbols[index];
        }
    }

    /// <summary>
    /// 位读取器，用于从字节流中读取位
    /// </summary>
    public class BitReader
    {
        private readonly byte[] data;
        private int bytePosition;
        private int bitPosition;
        private byte currentByte;

        public BitReader(byte[] data)
        {
            this.data = data;
            bytePosition = 0;
            bitPosition = 0;
            currentByte = 0;
        }

        /// <summary>
        /// 读取一个位
        /// </summary>
        /// <returns>位值（0或1）</returns>
        public int ReadBit()
        {
            if (bitPosition == 0)
            {
                if (bytePosition >= data.Length)
                {
                    return 0; // 数据结束
                }

                currentByte = data[bytePosition++];
                
                // 处理填充字节（0xFF后跟0x00）
                if (currentByte == 0xFF && bytePosition < data.Length)
                {
                    byte nextByte = data[bytePosition];
                    if (nextByte == 0x00)
                    {
                        bytePosition++; // 跳过填充字节
                    }
                    else if (nextByte >= 0xD0 && nextByte <= 0xD7)
                    {
                        // 重启标记，重置DC预测值
                        bytePosition++;
                        return ReadBit(); // 递归读取下一位
                    }
                }

                bitPosition = 8;
            }

            bitPosition--;
            return (currentByte >> bitPosition) & 1;
        }

        /// <summary>
        /// 读取指定数量的位
        /// </summary>
        /// <param name="count">位数</param>
        /// <returns>位值</returns>
        public int ReadBits(int count)
        {
            int result = 0;
            for (int i = 0; i < count; i++)
            {
                result = (result << 1) | ReadBit();
            }
            return result;
        }

        /// <summary>
        /// 跳过到字节边界
        /// </summary>
        public void AlignToByte()
        {
            bitPosition = 0;
        }

        /// <summary>
        /// 检查是否还有数据
        /// </summary>
        public bool HasData()
        {
            return bytePosition < data.Length || bitPosition > 0;
        }

        /// <summary>
        /// 获取当前位置
        /// </summary>
        public int Position => bytePosition * 8 + (8 - bitPosition);
    }
}