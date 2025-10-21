using System;

namespace JpegBmpConverter
{
    /// <summary>
    /// Pillow 风格 JPEG 解码器（渐进式部分）
    /// 独立承载渐进式扫描与系数缓冲的管理逻辑。
    /// </summary>
    public partial class PillowStyleJpegDecoder
    {
        // 渐进式 AC 细化扫描需要的 EOBRUN 状态（每次扫描重置）
        private int eobRun;
        /// <summary>
        /// 为渐进JPEG分配并初始化系数缓冲区
        /// - 每个分量按块数量分配 64 系数的缓冲
        /// - 重置每分量的块游标
        /// </summary>
        private void AllocateProgressiveBuffersIfNeeded()
        {
            if (!progressiveMode) return;

            // 细化扫描计数重置
            eobRun = 0;

            // 重置游标（解码与显示）
            for (int i = 0; i < progBlockCursor.Length; i++)
            {
                progBlockCursor[i] = 0;
                progDecodeCursor[i] = 0;
                progDisplayCursor[i] = 0;
            }

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
        /// DC 细化扫描（Ss==0, Ah>0）：对已存在的 DC 系数按位细化
        /// </summary>
        private void RefineDCProgressive(short[] targetBlock, int Al)
        {
            int bit = GetBits(1);
            if (bit != 0)
            {
                int delta = 1 << Al;
                if (targetBlock[0] >= 0)
                    targetBlock[0] = (short)(targetBlock[0] + delta);
                else
                    targetBlock[0] = (short)(targetBlock[0] - delta);
            }
        }

        /// <summary>
        /// AC 细化扫描（Ss>=1, Ah>0）：支持 EOBRUN 与 ZRL
        /// - 对谱选择范围内的非零系数进行逐位细化
        /// - 根据霍夫曼符号插入新出现的±(1<<Al)系数
        /// - 处理 EOBRUN：对当前带的非零系数细化一次后退出
        /// </summary>
        private void RefineACBandProgressive(short[] targetBlock, int componentIndex, int startS, int endS, int Al)
        {
            var htbl = acHuffmanTables[componentInfoExt[componentIndex].ac_tbl_no];
            if (htbl == null)
            {
                throw new InvalidOperationException($"AC Huffman table {componentInfoExt[componentIndex].ac_tbl_no} not initialized");
            }

            int p1 = 1 << Al;   // 正向细化步长
            int m1 = -p1;       // 负向细化步长
            int k = startS;

            // 若存在未消耗的 EOBRUN，仅对当前带非零系数做一次细化即可
            if (eobRun > 0)
            {
                for (int spectralIndex = startS; spectralIndex <= endS; spectralIndex++)
                {
                    int zig = ZigZagOrder[spectralIndex];
                    if (targetBlock[zig] != 0)
                    {
                        int b = GetBits(1);
                        if (b != 0)
                        {
                            targetBlock[zig] = (short)(targetBlock[zig] >= 0 ? targetBlock[zig] + p1 : targetBlock[zig] + m1);
                        }
                    }
                }
                eobRun--;
                return;
            }

            while (k <= endS)
            {
                int rs = HuffmanDecode(htbl);
                int rRun = (rs >> 4) & 0x0F;
                int sSize = rs & 0x0F;

                if (sSize == 0)
                {
                    if (rRun == 15)
                    {
                        // ZRL：跳过16个零，同时遇到非零系数则消费一个细化位
                        int zeros = 16;
                        while (zeros > 0 && k <= endS)
                        {
                            int zig = ZigZagOrder[k];
                            if (targetBlock[zig] != 0)
                            {
                                int b = GetBits(1);
                                if (b != 0)
                                {
                                    targetBlock[zig] = (short)(targetBlock[zig] >= 0 ? targetBlock[zig] + p1 : targetBlock[zig] + m1);
                                }
                            }
                            else
                            {
                                zeros--;
                            }
                            k++;
                        }
                    }
                    else
                    {
                        // EOBRUN：读取 rRun 个附加位，形成 EOBRUN = (1<<rRun) + bits - 1
                        int add = rRun > 0 ? GetBits(rRun) : 0;
                        eobRun = (1 << rRun) + add - 1;
                        // 对当前带剩余非零系数做一次细化后退出
                        for (int spectralIndex = k; spectralIndex <= endS; spectralIndex++)
                        {
                            int zig = ZigZagOrder[spectralIndex];
                            if (targetBlock[zig] != 0)
                            {
                                int b = GetBits(1);
                                if (b != 0)
                                {
                                    targetBlock[zig] = (short)(targetBlock[zig] >= 0 ? targetBlock[zig] + p1 : targetBlock[zig] + m1);
                                }
                            }
                        }
                        return;
                    }
                }
                else if (sSize == 1)
                {
                    // 先跳过 rRun 个零位置；途中遇到非零系数则消费细化位
                    while (rRun > 0 && k <= endS)
                    {
                        int zig = ZigZagOrder[k];
                        if (targetBlock[zig] != 0)
                        {
                            int b = GetBits(1);
                            if (b != 0)
                            {
                                targetBlock[zig] = (short)(targetBlock[zig] >= 0 ? targetBlock[zig] + p1 : targetBlock[zig] + m1);
                            }
                        }
                        else
                        {
                            rRun--;
                        }
                        k++;
                    }

                    if (k > endS) break;

                    // 在当前位置引入一个新的非零系数，符号由 1 位决定
                    int zigNew = ZigZagOrder[k];
                    int signBit = GetBits(1);
                    targetBlock[zigNew] = (short)(signBit != 0 ? p1 : m1);
                    k++;
                }
                else
                {
                    SetError($"Invalid AC refine symbol s={sSize}");
                    return;
                }
            }

            // 完成本带后，对剩余位置的非零系数各做一次细化（若仍未处理）
            for (int spectralIndex = k; spectralIndex <= endS; spectralIndex++)
            {
                int zig = ZigZagOrder[spectralIndex];
                if (targetBlock[zig] != 0)
                {
                    int b = GetBits(1);
                    if (b != 0)
                    {
                        targetBlock[zig] = (short)(targetBlock[zig] >= 0 ? targetBlock[zig] + p1 : targetBlock[zig] + m1);
                    }
                }
            }
        }

        /// <summary>
        /// 解码单个DCT块（渐进式JPEG）
        /// - DC 首扫：解码DC并左移 Al 写入缓冲
        /// - AC 首扫：按谱选择范围写入指定频带
        /// - DC/AC 细化：依据 Ah>0 执行逐次逼近
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

            // 非参与扫描的组件：不消耗位流，仅用于合成输出的游标推进
            if (ext != null && !ext.InScan)
            {
                int didx = progDisplayCursor[componentIndex];
                if (didx < 0 || didx >= compBuf.Length)
                {
                    // 合成游标超出则回绕到起始，避免显示越界
                    didx = 0;
                }
                Array.Copy(compBuf[didx], coeffs, 64);
                progDisplayCursor[componentIndex]++;
                return true;
            }

            int index = progDecodeCursor[componentIndex];
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
            // DC 细化（Ss==0, Ah>0）
            else if (Ss == 0 && Ah > 0)
            {
                RefineDCProgressive(compBuf[index], Al);
            }
            // AC 细化（Ss>=1, Ah>0）
            else if (Ss >= 1 && Ah > 0)
            {
                RefineACBandProgressive(compBuf[index], componentIndex, Ss, Se, Al);
            }
            else
            {
                SetError($"Unsupported progressive scan: Ss={Ss}, Se={Se}, Ah={Ah}, Al={Al}");
                return false;
            }

            // 将当前块的合并系数复制到输出缓冲并推进解码游标
            Array.Copy(compBuf[index], coeffs, 64);
            progDecodeCursor[componentIndex]++;
            return true;
        }
    }
}