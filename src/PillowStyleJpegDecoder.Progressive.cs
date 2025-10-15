using System;

namespace JpegBmpConverter
{
    /// <summary>
    /// Pillow 风格 JPEG 解码器（渐进式部分）
    /// 独立承载渐进式扫描与系数缓冲的管理逻辑。
    /// </summary>
    public partial class PillowStyleJpegDecoder
    {
        /// <summary>
        /// 为渐进JPEG分配并初始化系数缓冲区
        /// - 每个分量按块数量分配 64 系数的缓冲
        /// - 重置每分量的块游标
        /// </summary>
        private void AllocateProgressiveBuffersIfNeeded()
        {
            if (!progressiveMode) return;

            // 重置游标
            for (int i = 0; i < progBlockCursor.Length; i++) progBlockCursor[i] = 0;

            // 分量0（通常为Y）
            if (Components >= 1 && progCoeffBuffers0 == null)
            {
                int totalBlocks = ComputeTotalBlocksForComponent(0);
                progCoeffBuffers0 = new short[totalBlocks][];
                for (int i = 0; i < totalBlocks; i++) progCoeffBuffers0[i] = new short[64];
            }

            // 分量1（通常为Cb）
            if (Components >= 2 && progCoeffBuffers1 == null)
            {
                int totalBlocks = ComputeTotalBlocksForComponent(1);
                progCoeffBuffers1 = new short[totalBlocks][];
                for (int i = 0; i < totalBlocks; i++) progCoeffBuffers1[i] = new short[64];
            }

            // 分量2（通常为Cr）
            if (Components >= 3 && progCoeffBuffers2 == null)
            {
                int totalBlocks = ComputeTotalBlocksForComponent(2);
                progCoeffBuffers2 = new short[totalBlocks][];
                for (int i = 0; i < totalBlocks; i++) progCoeffBuffers2[i] = new short[64];
            }
        }

        /// <summary>
        /// 获取指定分量的渐进式系数缓冲
        /// </summary>
        /// <param name="compIndex">分量索引</param>
        /// <returns>系数缓冲数组或 null</returns>
        private short[][] GetProgressiveBufferForComponent(int compIndex)
        {
            if (compIndex == 0) return progCoeffBuffers0;
            if (compIndex == 1) return progCoeffBuffers1;
            if (compIndex == 2) return progCoeffBuffers2;
            return null;
        }

        /// <summary>
        /// 解码渐进式扫描的AC频带（首扫）
        /// - 读取霍夫曼码字，处理 ZRL 与 EOB
        /// - 将谱选择范围内的系数写入目标块（ZigZag 映射）
        /// </summary>
        private void DecodeACBandProgressive(short[] targetBlock, int componentIndex, int startS, int endS, int Al)
        {
            var htbl = acHuffmanTables[componentInfoExt[componentIndex].ac_tbl_no];
            if (htbl == null)
            {
                throw new InvalidOperationException($"AC Huffman table {componentInfoExt[componentIndex].ac_tbl_no} not initialized");
            }

            int k = 0;
            while (k < 63)
            {
                int rs = HuffmanDecode(htbl);
                if (rs == 0)
                {
                    break; // EOB
                }
                int r = (rs >> 4) & 0x0F;
                int s = rs & 0x0F;
                if (s == 0)
                {
                    if (r == 15)
                    {
                        k += 16; // ZRL
                        continue;
                    }
                    else
                    {
                        break; // 容忍并结束当前带
                    }
                }
                k += r;
                if (k >= 63) break;
                int spectralIndex = k + 1; // 1..63
                int zig = ZigZagOrder[spectralIndex];
                int coeffBits = GetBits(s);
                if (coeffBits < (1 << (s - 1)))
                {
                    coeffBits = coeffBits - (1 << s) + 1;
                }
                short val = (short)(coeffBits << Al);
                if (spectralIndex >= startS && spectralIndex <= endS)
                {
                    targetBlock[zig] = val;
                }
                k++;
            }
        }

        /// <summary>
        /// 解码单个DCT块（渐进式JPEG）
        /// - DC 首扫：解码DC并左移 Al 写入缓冲
        /// - AC 首扫：按谱选择范围写入指定频带
        /// - 将当前合并后的系数复制到输出并推进块游标
        /// </summary>
        private bool DecodeDCTBlockProgressive(short[] coeffs, int componentIndex)
        {
            var ext = componentInfoExt[componentIndex];
            int Ss = ext?.Ss ?? 0;
            int Se = ext?.Se ?? 63;
            int Ah = ext?.Ah ?? 0;
            int Al = ext?.Al ?? 0;

            var compBuf = GetProgressiveBufferForComponent(componentIndex);
            if (compBuf == null)
            {
                SetError($"Progressive buffer not allocated for component {componentIndex}");
                return false;
            }

            int index = progBlockCursor[componentIndex];
            if (index < 0 || index >= compBuf.Length)
            {
                SetError($"Progressive block index out of range for component {componentIndex}: {index}/{compBuf.Length}");
                return false;
            }

            // DC 首扫（Ss==0, Ah==0）
            if (Ss == 0 && Ah == 0)
            {
                short dc = DecodeDCCoeff(componentIndex);
                compBuf[index][0] = (short)(dc << Al);
            }
            // AC 首扫（Ss>=1, Ah==0）
            else if (Ss >= 1 && Ah == 0)
            {
                DecodeACBandProgressive(compBuf[index], componentIndex, Ss, Se, Al);
            }
            else
            {
                // 逐次逼近（Ah>0）暂不支持
                SetError($"Unsupported progressive refinement scan: Ss={Ss}, Se={Se}, Ah={Ah}, Al={Al}");
                return false;
            }

            // 将当前块的合并系数复制到输出缓冲并推进游标
            Array.Copy(compBuf[index], coeffs, 64);
            progBlockCursor[componentIndex]++;
            return true;
        }
    }
}