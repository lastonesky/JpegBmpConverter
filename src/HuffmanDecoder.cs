using System;

namespace JpegToBmpConverter
{
    /// <summary>
    /// 真正的霍夫曼解码器实现
    /// 直接翻译自 libjpeg-turbo 的 jdhuff.c 和 jdhuff.h
    /// </summary>
    public class HuffmanDecoder
    {
        // 霍夫曼查找表的位数
        private const int HUFF_LOOKAHEAD = 8;
        
        // 最大霍夫曼表数量
        private const int NUM_HUFF_TBLS = 4;
        
        // DCT块大小
        private const int DCTSIZE2 = 64;
        
        // 最大组件数
        private const int MAX_COMPS_IN_SCAN = 4;
        
        // 最大MCU中的块数
        private const int D_MAX_BLOCKS_IN_MCU = 10;

        /// <summary>
        /// 霍夫曼表结构 - 对应 JHUFF_TBL
        /// </summary>
        public class HuffmanTable
        {
            public byte[] bits = new byte[17];      // bits[k] = # of symbols with codes of length k bits; bits[0] is unused
            public byte[] huffval = new byte[256];  // The symbols, in order of incr code length
            public bool sent_table;                 // TRUE when table has been output
        }

        /// <summary>
        /// 派生的霍夫曼表 - 对应 d_derived_tbl
        /// </summary>
        public class DerivedHuffmanTable
        {
            // 基本表：(每个数组的元素[0]未使用)
            public long[] maxcode = new long[18];      // 长度为k的最大代码 (-1表示没有)
            public long[] valoffset = new long[18];    // huffval[]中长度为k的代码的偏移量
            
            // 指向公共霍夫曼表的链接
            public HuffmanTable pub;
            
            // 查找表：由输入数据流的下HUFF_LOOKAHEAD位索引
            public int[] lookup = new int[1 << HUFF_LOOKAHEAD];
        }

        /// <summary>
        /// 位读取状态 - 对应 bitread_perm_state
        /// </summary>
        public class BitreadPermState
        {
            public ulong get_buffer;    // 当前位提取缓冲区
            public int bits_left;       // 其中未使用的位数
        }

        /// <summary>
        /// 位读取工作状态 - 对应 bitread_working_state
        /// </summary>
        public class BitreadWorkingState
        {
            public byte[] next_input_byte;  // 指向要从源读取的下一个字节
            public int byte_offset;         // next_input_byte的偏移量
            public int bytes_in_buffer;     // 源缓冲区中剩余的字节数
            public ulong get_buffer;        // 当前位提取缓冲区
            public int bits_left;           // 其中未使用的位数
        }

        /// <summary>
        /// 可保存状态 - 对应 savable_state
        /// </summary>
        public class SaveableState
        {
            public int[] last_dc_val = new int[MAX_COMPS_IN_SCAN]; // 每个组件的最后DC系数
        }

        /// <summary>
        /// 霍夫曼熵解码器 - 对应 huff_entropy_decoder
        /// </summary>
        public class HuffmanEntropyDecoder
        {
            public BitreadPermState bitstate = new BitreadPermState();
            public SaveableState saved = new SaveableState();
            public uint restarts_to_go;
            
            // 指向派生表的指针
            public DerivedHuffmanTable[] dc_derived_tbls = new DerivedHuffmanTable[NUM_HUFF_TBLS];
            public DerivedHuffmanTable[] ac_derived_tbls = new DerivedHuffmanTable[NUM_HUFF_TBLS];
            
            // 预计算的信息，用于MCU解码
            public DerivedHuffmanTable[] dc_cur_tbls = new DerivedHuffmanTable[D_MAX_BLOCKS_IN_MCU];
            public DerivedHuffmanTable[] ac_cur_tbls = new DerivedHuffmanTable[D_MAX_BLOCKS_IN_MCU];
            public bool[] dc_needed = new bool[D_MAX_BLOCKS_IN_MCU];
            public bool[] ac_needed = new bool[D_MAX_BLOCKS_IN_MCU];
            
            public bool insufficient_data;
            
            /// <summary>
            /// 初始化霍夫曼解码器
            /// </summary>
            public void Initialize(object cinfo)
            {
                // 初始化派生表数组
                for (int i = 0; i < NUM_HUFF_TBLS; i++)
                {
                    dc_derived_tbls[i] = null;
                    ac_derived_tbls[i] = null;
                }
                
                // 初始化预计算信息
                for (int i = 0; i < D_MAX_BLOCKS_IN_MCU; i++)
                {
                    dc_cur_tbls[i] = null;
                    ac_cur_tbls[i] = null;
                    dc_needed[i] = true;
                    ac_needed[i] = true;
                }
                
                insufficient_data = false;
                restarts_to_go = 0;
            }
            
            /// <summary>
            /// 解码MCU - 慢速版本
            /// 直接翻译自 decode_mcu_slow
            /// </summary>
            public bool DecodeMcuSlow(object cinfo, short[][][] MCU_data)
            {
                // 基线JPEG慢速MCU解码：DC + AC
                try
                {
                    if (MCU_data == null || cinfo == null)
                    {
                        insufficient_data = true;
                        return false;
                    }

                    // 使用动态访问 DecompressInfo 的字段
                    dynamic dinfo = cinfo;

                    // 设置位读取工作状态
                    var state = new BitreadWorkingState
                    {
                        next_input_byte = (byte[])dinfo.src.buffer,
                        bytes_in_buffer = (int)dinfo.src.bytes_in_buffer,
                        byte_offset = (int)dinfo.src.offset,
                        get_buffer = bitstate.get_buffer,
                        bits_left = bitstate.bits_left
                    };

                    int blocksInMCU = 1;
                    try { blocksInMCU = (int)dinfo.blocks_in_MCU; } catch { blocksInMCU = MCU_data.Length; }
                    if (blocksInMCU <= 0) blocksInMCU = MCU_data.Length;

                    // 对每个块进行解码
                    for (int blkn = 0; blkn < blocksInMCU; blkn++)
                    {
                        var dcTbl = dc_cur_tbls[blkn] ?? dc_derived_tbls[0];
                        var acTbl = ac_cur_tbls[blkn] ?? ac_derived_tbls[0];
                        if (dcTbl == null || acTbl == null)
                        {
                            insufficient_data = true;
                            return false;
                        }

                        // 解码DC系数
                        int s = HuffmanDecode(state, dcTbl, 1);
                        if (s < 0)
                        {
                            insufficient_data = true;
                            return false;
                        }

                        int diff = 0;
                        if (s != 0)
                        {
                            diff = GetBits(state, s);
                            diff = HuffmanExtend(diff, s);
                        }

                        int ci = 0; // 简化：当前扫描仅支持单组件
                        int dcval = saved.last_dc_val[ci] + diff;
                        saved.last_dc_val[ci] = dcval;

                        // 将DC写入系数[0]
                        if (MCU_data[blkn] == null)
                        {
                            insufficient_data = true;
                            return false;
                        }
                        MCU_data[blkn][0][0] = (short)dcval;

                        // 解码AC系数
                        int k = 1;
                        while (k < 64)
                        {
                            int rs = HuffmanDecode(state, acTbl, 1);
                            if (rs < 0)
                            {
                                insufficient_data = true;
                                return false;
                            }
                            int r = rs >> 4;
                            s = rs & 15;
                            if (s != 0)
                            {
                                k += r;
                                if (k >= 64) break;
                                int val = GetBits(state, s);
                                val = HuffmanExtend(val, s);
                                int naturalIndex = jpeg_natural_order[k];
                                int row = naturalIndex >> 3;
                                int col = naturalIndex & 7;
                                MCU_data[blkn][row][col] = (short)val;
                                k++;
                            }
                            else
                            {
                                if (r == 15)
                                {
                                    k += 15; // ZRL
                                }
                                else
                                {
                                    break; // EOB
                                }
                            }
                        }
                    }

                    // 将工作状态写回永久状态和数据源偏移
                    bitstate.get_buffer = state.get_buffer;
                    bitstate.bits_left = state.bits_left;
                    try { dinfo.src.offset = state.byte_offset; } catch { }

                    insufficient_data = false;
                    try { dinfo.entropy.insufficient_data = false; } catch { }
                    return true;
                }
                catch (Exception)
                {
                    insufficient_data = true;
                    try { ((dynamic)cinfo).entropy.insufficient_data = true; } catch { }
                    return false;
                }
            }
        }

        // JPEG自然顺序表 - 对应 jpeg_natural_order
        private static readonly int[] jpeg_natural_order = {
            0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63
        };

        /// <summary>
        /// 构建派生霍夫曼表
        /// 直接翻译自 jpeg_make_d_derived_tbl
        /// </summary>
        public static DerivedHuffmanTable MakeDerivedTable(HuffmanTable htbl)
        {
            var dtbl = new DerivedHuffmanTable();
            dtbl.pub = htbl;

            // 图C.1：为每个符号制作霍夫曼代码长度表
            var huffsize = new byte[257];
            int p = 0;
            for (int l = 1; l <= 16; l++)
            {
                int i = htbl.bits[l];
                if (i < 0 || p + i > 256)
                    throw new InvalidOperationException("Bad Huffman table");
                
                while (i-- > 0)
                    huffsize[p++] = (byte)l;
            }
            huffsize[p] = 0;
            int numsymbols = p;

            // 图C.2：生成代码本身
            var huffcode = new uint[257];
            uint code = 0;
            int si = huffsize[0];
            p = 0;
            while (huffsize[p] != 0)
            {
                while (huffsize[p] == si)
                {
                    huffcode[p++] = code;
                    code++;
                }
                
                if (code >= (1U << si))
                    throw new InvalidOperationException("Bad Huffman table");
                
                code <<= 1;
                si++;
            }

            // 图F.15：为位序列解码生成解码表
            p = 0;
            for (int l = 1; l <= 16; l++)
            {
                if (htbl.bits[l] != 0)
                {
                    dtbl.valoffset[l] = p - (long)huffcode[p];
                    p += htbl.bits[l];
                    dtbl.maxcode[l] = huffcode[p - 1];
                }
                else
                {
                    dtbl.maxcode[l] = -1;
                }
            }
            dtbl.valoffset[17] = 0;
            dtbl.maxcode[17] = 0xFFFFFL;

            // 计算查找表以加速解码
            for (int i = 0; i < (1 << HUFF_LOOKAHEAD); i++)
                dtbl.lookup[i] = (HUFF_LOOKAHEAD + 1) << HUFF_LOOKAHEAD;

            p = 0;
            for (int l = 1; l <= HUFF_LOOKAHEAD; l++)
            {
                for (int i = 1; i <= htbl.bits[l]; i++, p++)
                {
                    int lookbits = (int)(huffcode[p] << (HUFF_LOOKAHEAD - l));
                    for (int ctr = 1 << (HUFF_LOOKAHEAD - l); ctr > 0; ctr--)
                    {
                        dtbl.lookup[lookbits] = (l << HUFF_LOOKAHEAD) | htbl.huffval[p];
                        lookbits++;
                    }
                }
            }

            return dtbl;
        }

        /// <summary>
        /// 填充位缓冲区
        /// 直接翻译自 jpeg_fill_bit_buffer
        /// </summary>
        public static bool FillBitBuffer(BitreadWorkingState state, int nbits)
        {
            // 参数验证
            if (state == null || state.next_input_byte == null)
                return false;
                
            if (nbits < 0 || nbits > 32)
                return false;
                
            const int MIN_GET_BITS = 25;
            
            // 优化：预先检查是否有足够的数据
            if (state.byte_offset + 4 > state.bytes_in_buffer && state.bits_left < MIN_GET_BITS)
            {
                // 数据不足，逐字节处理
                while (state.bits_left < MIN_GET_BITS && state.byte_offset < state.bytes_in_buffer)
                {
                    int c = state.next_input_byte[state.byte_offset++];
                    
                    // 优化：快速路径处理非0xFF字节
                    if (c != 0xFF)
                    {
                        state.get_buffer = (state.get_buffer << 8) | (uint)c;
                        state.bits_left += 8;
                        continue;
                    }
                    
                    // 处理0xFF字节
                    if (state.byte_offset >= state.bytes_in_buffer)
                        return false;
                    
                    int c2 = state.next_input_byte[state.byte_offset++];
                    
                    if (c2 == 0)
                    {
                        // FF/00 序列，表示FF数据字节
                        state.get_buffer = (state.get_buffer << 8) | 0xFF;
                        state.bits_left += 8;
                    }
                    else
                    {
                        // 遇到标记，填充剩余位
                        state.get_buffer <<= MIN_GET_BITS - state.bits_left;
                        state.bits_left = MIN_GET_BITS;
                        return true;
                    }
                }
                
                return state.bits_left >= MIN_GET_BITS;
            }
            
            // 优化：快速路径，批量处理多个字节
            while (state.bits_left < MIN_GET_BITS && state.byte_offset < state.bytes_in_buffer)
            {
                int c = state.next_input_byte[state.byte_offset++];
                
                if (c != 0xFF)
                {
                    state.get_buffer = (state.get_buffer << 8) | (uint)c;
                    state.bits_left += 8;
                }
                else
                {
                    if (state.byte_offset >= state.bytes_in_buffer)
                        return false;
                    
                    int c2 = state.next_input_byte[state.byte_offset++];
                    
                    if (c2 == 0)
                    {
                        state.get_buffer = (state.get_buffer << 8) | 0xFF;
                        state.bits_left += 8;
                    }
                    else
                    {
                        state.get_buffer <<= MIN_GET_BITS - state.bits_left;
                        state.bits_left = MIN_GET_BITS;
                        return true;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// 霍夫曼解码
        /// 直接翻译自 jpeg_huff_decode
        /// </summary>
        public static int HuffmanDecode(BitreadWorkingState state, DerivedHuffmanTable htbl, int min_bits)
        {
            // 优化：使用查找表进行快速解码（对于短码）
            if (state.bits_left >= HUFF_LOOKAHEAD)
            {
                int look = (int)((state.get_buffer >> (state.bits_left - HUFF_LOOKAHEAD)) & ((1 << HUFF_LOOKAHEAD) - 1));
                int sym = htbl.lookup[look];
                if (sym < 0)
                {
                    // 查找表命中，提取符号和位数
                    int nb = (-sym) >> 4;
                    sym = (-sym) & 15;
                    if (nb <= state.bits_left)
                    {
                        state.bits_left -= nb;
                        return sym;
                    }
                }
            }
            
            // 回退到标准解码
            int l = min_bits;
            long code = (long)(state.get_buffer >> (state.bits_left - l)) & ((1L << l) - 1);

            // 优化：预先检查是否需要更多位
            while (l <= 16 && code > htbl.maxcode[l])
            {
                code <<= 1;
                if (state.bits_left <= l)
                {
                    if (!FillBitBuffer(state, 1))
                        return -1;
                }
                
                code |= (long)((state.get_buffer >> (state.bits_left - l - 1)) & 1);
                l++;
            }

            // 对于垃圾输入，我们可能达到哨兵值l = 17
            if (l > 16)
                return 0; // 返回零作为最安全的结果

            state.bits_left -= l;
            return htbl.pub.huffval[(int)(code + htbl.valoffset[l])];
        }

        /// <summary>
        /// 扩展符号位
        /// 直接翻译自 HUFF_EXTEND 宏
        /// </summary>
        public static int HuffmanExtend(int x, int s)
        {
            const uint NEG_1 = unchecked((uint)-1);
            return x + (int)(((uint)(x - (1 << (s - 1))) >> 31) & ((NEG_1 << s) + 1));
        }

        /// <summary>
        /// 从位缓冲区获取指定数量的位
        /// </summary>
        public static int GetBits(BitreadWorkingState state, int nbits)
        {
            // 优化：内联常见情况的检查
            if (state.bits_left >= nbits)
            {
                // 快速路径：有足够的位
                state.bits_left -= nbits;
                return (int)((state.get_buffer >> state.bits_left) & ((1U << nbits) - 1));
            }
            
            // 慢速路径：需要填充缓冲区
            if (!FillBitBuffer(state, nbits))
                return 0; // 错误或数据不足

            state.bits_left -= nbits;
            return (int)((state.get_buffer >> state.bits_left) & ((1U << nbits) - 1));
        }

        /// <summary>
        /// 查看位缓冲区中的位而不移除它们
        /// </summary>
        public static int PeekBits(BitreadWorkingState state, int nbits)
        {
            if (state.bits_left < nbits)
            {
                if (!FillBitBuffer(state, nbits))
                    return 0;
            }
            
            return (int)((state.get_buffer >> (state.bits_left - nbits)) & ((1U << nbits) - 1));
        }

        /// <summary>
        /// 丢弃位缓冲区中的位
        /// </summary>
        public static void DropBits(BitreadWorkingState state, int nbits)
        {
            state.bits_left -= nbits;
        }
    }
}