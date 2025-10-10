using System;
using System.IO;
using Console = JpegToBmpConverter.SilentConsole;

namespace JpegToBmpConverter
{
    /// <summary>
    /// 基于libjpeg-turbo源码实现的JPEG解码器
    /// 直接翻译libjpeg-turbo的C源码逻辑到C#
    /// </summary>
    public class LibjpegTurboStyleDecoder
    {
        // 解码器状态枚举 - 对应libjpeg-turbo中的DSTATE_*
        public enum DecompressState
        {
            DSTATE_START = 200,     /* after create_decompress */
            DSTATE_INHEADER = 201,  /* reading header markers, no SOS yet */
            DSTATE_READY = 202,     /* found SOS, ready for start_decompress */
            DSTATE_PRELOAD = 203,   /* reading multiscan file in start_decompress*/
            DSTATE_PRESCAN = 204,   /* performing dummy pass for 2-pass quant */
            DSTATE_SCANNING = 205,  /* start_decompress done, read_scanlines OK */
            DSTATE_RAW_OK = 206,    /* start_decompress done, read_raw_data OK */
            DSTATE_BUFIMAGE = 207,  /* expecting jpeg_start_output */
            DSTATE_BUFPOST = 208,   /* looking for SOS/EOI in jpeg_finish_output */
            DSTATE_STOPPING = 209   /* looking for EOI in jpeg_finish_decompress */
        }

        // 缓冲模式枚举 - 对应libjpeg-turbo中的J_BUF_MODE
        public enum BufferMode
        {
            JBUF_PASS_THRU,     /* Plain stripwise operation */
            JBUF_SAVE_SOURCE,   /* Run source subobject only, save output */
            JBUF_CRANK_DEST,    /* Run dest subobject only, using saved data */
            JBUF_SAVE_AND_PASS  /* Run both subobjects, save output */
        }

        // 解码器信息结构 - 对应libjpeg-turbo中的j_decompress_struct
        public class DecompressInfo
        {
            public DecompressState global_state;
            public int output_scanline;     /* 0 .. output_height-1  */
            public int output_height;       /* nominal image height (rounded up) */
            public int output_width;        /* scaled image width */
            public int output_components;   /* # of color components in out_color_space */
            public int data_precision;      /* bits of precision in image data */
            
            // 进度监控
            public ProgressMonitor progress;
            
            // 主控制器
            public MainController main;
            
            // 系数控制器
            public CoefController coef;
            
            // 后处理器
            public PostProcessor post;
            
            // 熵解码器
            public EntropyDecoder entropy;
            
            // IDCT管理器
            public IdctManager idct;
            
            // 当前扫描的组件信息
            public ComponentInfo[] cur_comp_info;
            public int comps_in_scan;
            public int blocks_in_MCU;
            
            // MCU相关
            public int input_iMCU_row;
            public int _min_DCT_scaled_size = 8;  // 通常是8
            
            // 重启间隔
            public int restart_interval;
            
            // 数据源
            public DataSource src;
            public int unread_marker;
            
            // 主控制器
            public MasterController master;
        }

        // 进度监控器 - 对应libjpeg-turbo中的jpeg_progress_mgr
        public class ProgressMonitor
        {
            public long pass_counter;       /* work units completed in this pass */
            public long pass_limit;         /* total number of work units in this pass */
            public Action<DecompressInfo> progress_monitor;  /* progress monitor routine */
        }

        // 主控制器 - 对应libjpeg-turbo中的jpeg_d_main_controller
        public class MainController
        {
            public delegate void ProcessDataDelegate(DecompressInfo cinfo, byte[][] output_buf, ref int out_row_ctr, int out_rows_avail);
            public ProcessDataDelegate _process_data;
            public bool buffer_full;
            public int rowgroup_ctr;
            public byte[][][] buffer;  // 主缓冲区
        }

        // 系数控制器 - 对应libjpeg-turbo中的jpeg_d_coef_controller  
        public class CoefController
        {
            public Func<DecompressInfo, byte[][][], bool> _decompress_data;
            public short[][][] MCU_buffer;  // MCU缓冲区
            public int MCU_ctr;
            public int MCU_vert_offset;
        }

        // 后处理器 - 对应libjpeg-turbo中的jpeg_d_post_controller
        public class PostProcessor
        {
            public delegate void PostProcessDataDelegate(DecompressInfo cinfo, byte[][][] input_buf, ref int in_row_group_ctr, int in_row_groups_avail, byte[][] output_buf, ref int out_row_ctr, int out_rows_avail);
            public PostProcessDataDelegate _post_process_data;
        }

        // 熵解码器 - 对应libjpeg-turbo中的jpeg_entropy_decoder
        public class EntropyDecoder
        {
            public Func<DecompressInfo, short[][][], bool> decode_mcu;
            public bool insufficient_data;
            public int restarts_to_go;
            
            // 霍夫曼解码器实例
            public HuffmanDecoder.HuffmanEntropyDecoder huffman_decoder;
        }

        // IDCT管理器 - 对应libjpeg-turbo中的jpeg_inverse_dct
        public class IdctManager
        {
            public Action<DecompressInfo, ComponentInfo, short[], byte[][], int>[] _inverse_DCT;
        }

        // 组件信息 - 对应libjpeg-turbo中的jpeg_component_info
        public class ComponentInfo
        {
            public int component_index;
            public bool component_needed;
            public int MCU_blocks;
            public int MCU_width;
            public int MCU_height;
            public int MCU_sample_width;
            public int last_col_width;
            public int last_row_height;
            public int _DCT_scaled_size = 8;
            public int quant_tbl_no;  // 量化表编号
        }

        // 数据源 - 对应libjpeg-turbo中的jpeg_source_mgr
        public class DataSource
        {
            public int bytes_in_buffer;
        }

        // 主控制器 - 对应libjpeg-turbo中的jpeg_decomp_master
        public class MasterController
        {
            public bool lossless;
            public int last_good_iMCU_row;
            public int first_iMCU_col;
            public int last_iMCU_col;
        }

        private DecompressInfo cinfo;
        private byte[] jpegData;
        
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Components { get; private set; }
        public byte[] ImageData { get; private set; }
        
        // 公共访问器用于测试
        public DecompressInfo CInfo => cinfo;

        public LibjpegTurboStyleDecoder()
        {
            cinfo = new DecompressInfo();
            InitializeDecompressInfo();
        }

        public void InitializeDecompressInfo()
        {
            cinfo.global_state = DecompressState.DSTATE_START;
            cinfo.output_scanline = 0;
            cinfo.data_precision = 8;  // 标准JPEG是8位
            cinfo._min_DCT_scaled_size = 8;
            
            // 初始化各个控制器
            cinfo.main = new MainController();
            cinfo.coef = new CoefController();
            cinfo.post = new PostProcessor();
            cinfo.entropy = new EntropyDecoder();
            cinfo.idct = new IdctManager();
            cinfo.src = new DataSource();
            cinfo.master = new MasterController();
            
            // 设置处理函数 - 对应libjpeg-turbo中的函数指针赋值
            cinfo.main._process_data = ProcessDataSimpleMain;
            cinfo.coef._decompress_data = DecompressData;
            cinfo.entropy.decode_mcu = DecodeMcu;
            cinfo.post._post_process_data = PostProcessData;
            
            // 初始化霍夫曼解码器
            cinfo.entropy.huffman_decoder = new HuffmanDecoder.HuffmanEntropyDecoder();
            cinfo.entropy.huffman_decoder.Initialize(cinfo);
            
            // 初始化IDCT函数指针数组
            cinfo.idct._inverse_DCT = new Action<DecompressInfo, ComponentInfo, short[], byte[][], int>[4];
            for (int i = 0; i < 4; i++)
            {
                cinfo.idct._inverse_DCT[i] = InverseDCT;
            }
        }

        /// <summary>
        /// 主解码函数 - 对应libjpeg-turbo中的jpeg_read_scanlines
        /// 直接翻译自jdapistd.c中的_jpeg_read_scanlines函数
        /// </summary>
        public int JpegReadScanlines(byte[][] scanlines, int max_lines)
        {
            int row_ctr;

            // 检查数据精度 - 对应源码第324-334行
            if (!cinfo.master.lossless)
            {
                if (cinfo.data_precision != 8)  // BITS_IN_JSAMPLE
                {
                    throw new InvalidOperationException($"Bad data precision: {cinfo.data_precision}");
                }
            }

            // 检查全局状态 - 对应源码第336-337行
            if (cinfo.global_state != DecompressState.DSTATE_SCANNING)
            {
                throw new InvalidOperationException($"Bad state: {cinfo.global_state}");
            }

            // 检查是否已经读取完所有扫描线 - 对应源码第338-341行
            if (cinfo.output_scanline >= cinfo.output_height)
            {
                Console.WriteLine("Warning: Too much data");
                return 0;
            }

            // 调用进度监控钩子 - 对应源码第343-347行
            if (cinfo.progress != null)
            {
                cinfo.progress.pass_counter = cinfo.output_scanline;
                cinfo.progress.pass_limit = cinfo.output_height;
                cinfo.progress.progress_monitor?.Invoke(cinfo);
            }

            // 处理数据 - 对应源码第349-361行
            row_ctr = 0;
            if (cinfo.main._process_data == null)
            {
                throw new InvalidOperationException($"Bad precision: {cinfo.data_precision}");
            }
            
            cinfo.main._process_data(cinfo, scanlines, ref row_ctr, max_lines);
            cinfo.output_scanline += row_ctr;
            
            return row_ctr;
        }

        /// <summary>
        /// 简单主处理函数 - 对应libjpeg-turbo中的process_data_simple_main
        /// 直接翻译自jdmainct.c中的process_data_simple_main函数
        /// </summary>
        private void ProcessDataSimpleMain(DecompressInfo cinfo, byte[][] output_buf, 
                                         ref int out_row_ctr, int out_rows_avail)
        {
            int rowgroups_avail;

            // 如果主缓冲区未满，读取输入数据 - 对应源码第303-307行
            if (!cinfo.main.buffer_full)
            {
                if (!cinfo.coef._decompress_data(cinfo, cinfo.main.buffer))
                    return;  // 强制暂停，无法做更多事情
                cinfo.main.buffer_full = true;  // OK，我们有一个iMCU行可以处理
            }

            // 一个iMCU行中总是有min_DCT_scaled_size个行组 - 对应源码第309行
            rowgroups_avail = cinfo._min_DCT_scaled_size;
            
            // 注意：在图像底部，我们可能会向后处理器传递额外的垃圾行组
            // 后处理器无论如何都必须检查图像底部（在行分辨率下），所以我们也没必要这样做

            // 馈送后处理器 - 对应源码第315-317行
            cinfo.post._post_process_data(cinfo, cinfo.main.buffer,
                                        ref cinfo.main.rowgroup_ctr, rowgroups_avail,
                                        output_buf, ref out_row_ctr, out_rows_avail);

            // 后处理器是否已消耗所有数据？如果是，标记缓冲区为空 - 对应源码第319-322行
            if (cinfo.main.rowgroup_ctr >= rowgroups_avail)
            {
                cinfo.main.buffer_full = false;
                cinfo.main.rowgroup_ctr = 0;
            }
        }

        /// <summary>
        /// 解压缩数据函数 - 对应libjpeg-turbo中的decompress_onepass
        /// 直接翻译自jdcoefct.c中的decompress_onepass函数（第78-165行）
        /// </summary>
        private bool DecompressData(DecompressInfo cinfo, byte[][][] output_buf)
        {
            int MCU_col_num;        // 当前MCU在行中的索引
            int last_MCU_col = GetMCUsPerRow() - 1;  // 最后一个MCU列
            int last_iMCU_row = GetTotalIMCURows() - 1;  // 最后一个iMCU行
            int blkn, ci, xindex, yindex, yoffset, useful_width;
            byte[][] output_ptr;
            int start_col, output_col;
            ComponentInfo compptr;

            // 循环处理一整个iMCU行 - 对应源码第89-90行
            for (yoffset = cinfo.coef.MCU_vert_offset; yoffset < GetMCURowsPerIMCURow(); yoffset++)
            {
                for (MCU_col_num = cinfo.coef.MCU_ctr; MCU_col_num <= last_MCU_col; MCU_col_num++)
                {
                    // 尝试获取一个MCU。熵解码器期望缓冲区被清零 - 对应源码第94-95行
                    ZeroMCUBuffer(cinfo.coef.MCU_buffer, cinfo.blocks_in_MCU);
                    
                    if (!cinfo.entropy.insufficient_data)
                        cinfo.master.last_good_iMCU_row = cinfo.input_iMCU_row;
                        
                    // 解码MCU - 对应源码第98行
                    if (!cinfo.entropy.decode_mcu(cinfo, cinfo.coef.MCU_buffer))
                    {
                        // 强制暂停；更新状态计数器并退出 - 对应源码第99-102行
                        cinfo.coef.MCU_vert_offset = yoffset;
                        cinfo.coef.MCU_ctr = MCU_col_num;
                        return false;  // JPEG_SUSPENDED
                    }

                    // 只对包含在所需裁剪区域内的块执行IDCT - 对应源码第104-107行
                    if (MCU_col_num >= cinfo.master.first_iMCU_col &&
                        MCU_col_num <= cinfo.master.last_iMCU_col)
                    {
                        // 确定数据应该放在output_buf的哪里并执行IDCT - 对应源码第108-113行
                        blkn = 0;  // 当前DCT块在MCU中的索引
                        
                        for (ci = 0; ci < cinfo.comps_in_scan; ci++)
                        {
                            compptr = cinfo.cur_comp_info[ci];
                            
                            // 不要费心对不感兴趣的组件进行IDCT - 对应源码第116-119行
                            if (!compptr.component_needed)
                            {
                                blkn += compptr.MCU_blocks;
                                continue;
                            }
                            
                            // 获取IDCT方法 - 对应源码第120行
                            var inverse_DCT = cinfo.idct._inverse_DCT[compptr.component_index];
                            
                            // 计算有用宽度 - 对应源码第121-122行
                            useful_width = (MCU_col_num < last_MCU_col) ?
                                         compptr.MCU_width : compptr.last_col_width;
                                         
                            // 计算输出指针 - 对应源码第123-124行
                            output_ptr = output_buf[compptr.component_index];
                            
                            // 计算起始列 - 对应源码第125-126行
                            start_col = (MCU_col_num - cinfo.master.first_iMCU_col) *
                                      compptr.MCU_sample_width;
                                      
                            // 处理MCU中的每一行 - 对应源码第127-142行
                            for (yindex = 0; yindex < compptr.MCU_height; yindex++)
                            {
                                if (cinfo.input_iMCU_row < last_iMCU_row ||
                                    yoffset + yindex < compptr.last_row_height)
                                {
                                    output_col = start_col;
                                    for (xindex = 0; xindex < useful_width; xindex++)
                                    {
                                        // 执行IDCT - 对应源码第133-135行
                                        var mcuBlock = GetMCUBlock(cinfo.coef.MCU_buffer, blkn + xindex);
                                        inverse_DCT(cinfo, compptr,
                                                  mcuBlock[0], // 获取第一个DCT块
                                                  output_ptr, output_col);
                                        output_col += compptr._DCT_scaled_size;
                                    }
                                }
                                blkn += compptr.MCU_width;
                            }
                        }
                    }
                }
                // 完成了一个MCU行，但可能不是一个iMCU行 - 对应源码第146行
                cinfo.coef.MCU_ctr = 0;
            }
            
            // 完成了iMCU行，为下一个推进计数器 - 对应源码第148-155行
            AdvanceOutputIMCURow();
            if (++cinfo.input_iMCU_row < GetTotalIMCURows())
            {
                StartIMCURow(cinfo);
                return true;  // JPEG_ROW_COMPLETED
            }
            
            // 完成了扫描 - 对应源码第156-158行
            FinishInputPass(cinfo);
            return true;  // JPEG_SCAN_COMPLETED
        }

        // 辅助方法 - 对应libjpeg-turbo中的各种宏和函数
        private int GetMCUsPerRow() => (Width + 7) / 8;  // 简化计算
        private int GetTotalIMCURows() => (Height + 7) / 8;  // 简化计算
        private int GetMCURowsPerIMCURow() => 1;  // 简化为1
        
        private void ZeroMCUBuffer(short[][][] buffer, int blocks_count)
        {
            // 清零MCU缓冲区 - 对应jzero_far函数
            for (int i = 0; i < blocks_count; i++)
            {
                if (buffer[i] != null)
                {
                    for (int j = 0; j < 8; j++)
                    {
                        for (int k = 0; k < 8; k++)
                        {
                            buffer[i][j][k] = 0;
                        }
                    }
                }
            }
        }
        
        private short[][] GetMCUBlock(short[][][] buffer, int index)
        {
            return buffer[index];
        }
        
        private void AdvanceOutputIMCURow()
        {
            // 推进输出iMCU行计数器
        }
        
        private void StartIMCURow(DecompressInfo cinfo)
        {
            // 开始新的iMCU行
        }
        
        private void FinishInputPass(DecompressInfo cinfo)
        {
            // 完成输入处理过程
        }

        /// <summary>
        /// 解码MCU函数 - 对应libjpeg-turbo中的decode_mcu_slow
        /// 直接翻译自jdhuff.c中的decode_mcu_slow函数（第552-640行）
        /// </summary>




        


        // 实现inverse_DCT函数，基于libjpeg-turbo的jpeg_idct_islow
        private void InverseDCT(DecompressInfo cinfo, ComponentInfo compptr, 
                               short[] coef_block, byte[][] output_buf, int output_col)
        {
            // IDCT常量定义 (基于jidctint.c)
            const int CONST_BITS = 13;
            const int PASS1_BITS = 2;
            const int DCTSIZE = 8;
            const int DCTSIZE2 = 64;
            
            // 固定点常量 (基于jidctint.c中的FIX_常量)
            const int FIX_0_298631336 = 2446;   // FIX(0.298631336)
            const int FIX_0_390180644 = 3196;   // FIX(0.390180644)
            const int FIX_0_541196100 = 4433;   // FIX(0.541196100)
            const int FIX_0_765366865 = 6270;   // FIX(0.765366865)
            const int FIX_0_899976223 = 7373;   // FIX(0.899976223)
            const int FIX_1_175875602 = 9633;   // FIX(1.175875602)
            const int FIX_1_501321110 = 12299;  // FIX(1.501321110)
            const int FIX_1_847759065 = 15137;  // FIX(1.847759065)
            const int FIX_1_961570560 = 16069;  // FIX(1.961570560)
            const int FIX_2_053119869 = 16819;  // FIX(2.053119869)
            const int FIX_2_562915447 = 20995;  // FIX(2.562915447)
            const int FIX_3_072711026 = 25172;  // FIX(3.072711026)

            long tmp0, tmp1, tmp2, tmp3;
            long tmp10, tmp11, tmp12, tmp13;
            long z1, z2, z3, z4, z5;
            int[] workspace = new int[DCTSIZE2]; // 工作空间缓冲区
            
            // 获取量化表
            ushort[] quantptr = GetQuantTable(compptr.quant_tbl_no);
            
            // 第一遍：处理列，存储到工作数组
            // 结果按sqrt(8)缩放，并按2**PASS1_BITS缩放
            int inptr_idx = 0;
            int quantptr_idx = 0;
            int wsptr_idx = 0;
            
            for (int ctr = DCTSIZE; ctr > 0; ctr--)
            {
                // 检查AC系数是否全为零的优化
                if (coef_block[inptr_idx + DCTSIZE * 1] == 0 && 
                    coef_block[inptr_idx + DCTSIZE * 2] == 0 &&
                    coef_block[inptr_idx + DCTSIZE * 3] == 0 && 
                    coef_block[inptr_idx + DCTSIZE * 4] == 0 &&
                    coef_block[inptr_idx + DCTSIZE * 5] == 0 && 
                    coef_block[inptr_idx + DCTSIZE * 6] == 0 &&
                    coef_block[inptr_idx + DCTSIZE * 7] == 0)
                {
                    // AC系数全为零
                    int dcval = (int)LeftShift(Dequantize(coef_block[inptr_idx + DCTSIZE * 0],
                                         quantptr[quantptr_idx + DCTSIZE * 0]), PASS1_BITS);

                    workspace[wsptr_idx + DCTSIZE * 0] = dcval;
                    workspace[wsptr_idx + DCTSIZE * 1] = dcval;
                    workspace[wsptr_idx + DCTSIZE * 2] = dcval;
                    workspace[wsptr_idx + DCTSIZE * 3] = dcval;
                    workspace[wsptr_idx + DCTSIZE * 4] = dcval;
                    workspace[wsptr_idx + DCTSIZE * 5] = dcval;
                    workspace[wsptr_idx + DCTSIZE * 6] = dcval;
                    workspace[wsptr_idx + DCTSIZE * 7] = dcval;

                    inptr_idx++;
                    quantptr_idx++;
                    wsptr_idx++;
                    continue;
                }

                // 偶数部分：反向DCT的偶数部分
                z2 = Dequantize(coef_block[inptr_idx + DCTSIZE * 2], quantptr[quantptr_idx + DCTSIZE * 2]);
                z3 = Dequantize(coef_block[inptr_idx + DCTSIZE * 6], quantptr[quantptr_idx + DCTSIZE * 6]);

                z1 = Multiply(z2 + z3, FIX_0_541196100);
                tmp2 = z1 + Multiply(z3, -FIX_1_847759065);
                tmp3 = z1 + Multiply(z2, FIX_0_765366865);

                z2 = Dequantize(coef_block[inptr_idx + DCTSIZE * 0], quantptr[quantptr_idx + DCTSIZE * 0]);
                z3 = Dequantize(coef_block[inptr_idx + DCTSIZE * 4], quantptr[quantptr_idx + DCTSIZE * 4]);

                tmp0 = LeftShift(z2 + z3, CONST_BITS);
                tmp1 = LeftShift(z2 - z3, CONST_BITS);

                tmp10 = tmp0 + tmp3;
                tmp13 = tmp0 - tmp3;
                tmp11 = tmp1 + tmp2;
                tmp12 = tmp1 - tmp2;

                // 奇数部分
                tmp0 = Dequantize(coef_block[inptr_idx + DCTSIZE * 7], quantptr[quantptr_idx + DCTSIZE * 7]);
                tmp1 = Dequantize(coef_block[inptr_idx + DCTSIZE * 5], quantptr[quantptr_idx + DCTSIZE * 5]);
                tmp2 = Dequantize(coef_block[inptr_idx + DCTSIZE * 3], quantptr[quantptr_idx + DCTSIZE * 3]);
                tmp3 = Dequantize(coef_block[inptr_idx + DCTSIZE * 1], quantptr[quantptr_idx + DCTSIZE * 1]);

                z1 = tmp0 + tmp3;
                z2 = tmp1 + tmp2;
                z3 = tmp0 + tmp2;
                z4 = tmp1 + tmp3;
                z5 = Multiply(z3 + z4, FIX_1_175875602); // sqrt(2) * c3

                tmp0 = Multiply(tmp0, FIX_0_298631336); // sqrt(2) * (-c1+c3+c5-c7)
                tmp1 = Multiply(tmp1, FIX_2_053119869); // sqrt(2) * ( c1+c3-c5+c7)
                tmp2 = Multiply(tmp2, FIX_3_072711026); // sqrt(2) * ( c1+c3+c5-c7)
                tmp3 = Multiply(tmp3, FIX_1_501321110); // sqrt(2) * ( c1+c3-c5-c7)
                z1 = Multiply(z1, -FIX_0_899976223); // sqrt(2) * ( c7-c3)
                z2 = Multiply(z2, -FIX_2_562915447); // sqrt(2) * (-c1-c3)
                z3 = Multiply(z3, -FIX_1_961570560); // sqrt(2) * (-c3-c5)
                z4 = Multiply(z4, -FIX_0_390180644); // sqrt(2) * ( c5-c3)

                z3 += z5;
                z4 += z5;

                tmp0 += z1 + z3;
                tmp1 += z2 + z4;
                tmp2 += z2 + z3;
                tmp3 += z1 + z4;

                // 最终输出阶段：输入为tmp10..tmp13, tmp0..tmp3
                workspace[wsptr_idx + DCTSIZE * 0] = (int)Descale(tmp10 + tmp3, CONST_BITS - PASS1_BITS);
                workspace[wsptr_idx + DCTSIZE * 7] = (int)Descale(tmp10 - tmp3, CONST_BITS - PASS1_BITS);
                workspace[wsptr_idx + DCTSIZE * 1] = (int)Descale(tmp11 + tmp2, CONST_BITS - PASS1_BITS);
                workspace[wsptr_idx + DCTSIZE * 6] = (int)Descale(tmp11 - tmp2, CONST_BITS - PASS1_BITS);
                workspace[wsptr_idx + DCTSIZE * 2] = (int)Descale(tmp12 + tmp1, CONST_BITS - PASS1_BITS);
                workspace[wsptr_idx + DCTSIZE * 5] = (int)Descale(tmp12 - tmp1, CONST_BITS - PASS1_BITS);
                workspace[wsptr_idx + DCTSIZE * 3] = (int)Descale(tmp13 + tmp0, CONST_BITS - PASS1_BITS);
                workspace[wsptr_idx + DCTSIZE * 4] = (int)Descale(tmp13 - tmp0, CONST_BITS - PASS1_BITS);

                inptr_idx++;
                quantptr_idx++;
                wsptr_idx++;
            }

            // 第二遍：处理行，存储到输出数组
            // 必须按因子8 == 2**3降级结果，并撤销PASS1_BITS缩放
            wsptr_idx = 0;
            byte[] range_limit = GetRangeLimit(cinfo);
            
            for (int ctr = 0; ctr < DCTSIZE; ctr++)
            {
                // 零行优化
                if (workspace[wsptr_idx + 1] == 0 && workspace[wsptr_idx + 2] == 0 && 
                    workspace[wsptr_idx + 3] == 0 && workspace[wsptr_idx + 4] == 0 &&
                    workspace[wsptr_idx + 5] == 0 && workspace[wsptr_idx + 6] == 0 && 
                    workspace[wsptr_idx + 7] == 0)
                {
                    // AC系数全为零
                    byte dcval = range_limit[(int)Descale((long)workspace[wsptr_idx + 0],
                                                         PASS1_BITS + 3) & GetRangeMask()];

                    output_buf[ctr][output_col + 0] = dcval;
                    output_buf[ctr][output_col + 1] = dcval;
                    output_buf[ctr][output_col + 2] = dcval;
                    output_buf[ctr][output_col + 3] = dcval;
                    output_buf[ctr][output_col + 4] = dcval;
                    output_buf[ctr][output_col + 5] = dcval;
                    output_buf[ctr][output_col + 6] = dcval;
                    output_buf[ctr][output_col + 7] = dcval;

                    wsptr_idx += DCTSIZE;
                    continue;
                }

                // 偶数部分：反向DCT的偶数部分
                z2 = (long)workspace[wsptr_idx + 2];
                z3 = (long)workspace[wsptr_idx + 6];

                z1 = Multiply(z2 + z3, FIX_0_541196100);
                tmp2 = z1 + Multiply(z3, -FIX_1_847759065);
                tmp3 = z1 + Multiply(z2, FIX_0_765366865);

                tmp0 = LeftShift((long)workspace[wsptr_idx + 0] + (long)workspace[wsptr_idx + 4], CONST_BITS);
                tmp1 = LeftShift((long)workspace[wsptr_idx + 0] - (long)workspace[wsptr_idx + 4], CONST_BITS);

                tmp10 = tmp0 + tmp3;
                tmp13 = tmp0 - tmp3;
                tmp11 = tmp1 + tmp2;
                tmp12 = tmp1 - tmp2;

                // 奇数部分
                tmp0 = (long)workspace[wsptr_idx + 7];
                tmp1 = (long)workspace[wsptr_idx + 5];
                tmp2 = (long)workspace[wsptr_idx + 3];
                tmp3 = (long)workspace[wsptr_idx + 1];

                z1 = tmp0 + tmp3;
                z2 = tmp1 + tmp2;
                z3 = tmp0 + tmp2;
                z4 = tmp1 + tmp3;
                z5 = Multiply(z3 + z4, FIX_1_175875602); // sqrt(2) * c3

                tmp0 = Multiply(tmp0, FIX_0_298631336); // sqrt(2) * (-c1+c3+c5-c7)
                tmp1 = Multiply(tmp1, FIX_2_053119869); // sqrt(2) * ( c1+c3-c5+c7)
                tmp2 = Multiply(tmp2, FIX_3_072711026); // sqrt(2) * ( c1+c3+c5-c7)
                tmp3 = Multiply(tmp3, FIX_1_501321110); // sqrt(2) * ( c1+c3-c5-c7)
                z1 = Multiply(z1, -FIX_0_899976223); // sqrt(2) * ( c7-c3)
                z2 = Multiply(z2, -FIX_2_562915447); // sqrt(2) * (-c1-c3)
                z3 = Multiply(z3, -FIX_1_961570560); // sqrt(2) * (-c3-c5)
                z4 = Multiply(z4, -FIX_0_390180644); // sqrt(2) * ( c5-c3)

                z3 += z5;
                z4 += z5;

                tmp0 += z1 + z3;
                tmp1 += z2 + z4;
                tmp2 += z2 + z3;
                tmp3 += z1 + z4;

                // 最终输出阶段：输入为tmp10..tmp13, tmp0..tmp3
                output_buf[ctr][output_col + 0] = range_limit[(int)Descale(tmp10 + tmp3,
                                                             CONST_BITS + PASS1_BITS + 3) & GetRangeMask()];
                output_buf[ctr][output_col + 7] = range_limit[(int)Descale(tmp10 - tmp3,
                                                             CONST_BITS + PASS1_BITS + 3) & GetRangeMask()];
                output_buf[ctr][output_col + 1] = range_limit[(int)Descale(tmp11 + tmp2,
                                                             CONST_BITS + PASS1_BITS + 3) & GetRangeMask()];
                output_buf[ctr][output_col + 6] = range_limit[(int)Descale(tmp11 - tmp2,
                                                             CONST_BITS + PASS1_BITS + 3) & GetRangeMask()];
                output_buf[ctr][output_col + 2] = range_limit[(int)Descale(tmp12 + tmp1,
                                                             CONST_BITS + PASS1_BITS + 3) & GetRangeMask()];
                output_buf[ctr][output_col + 5] = range_limit[(int)Descale(tmp12 - tmp1,
                                                             CONST_BITS + PASS1_BITS + 3) & GetRangeMask()];
                output_buf[ctr][output_col + 3] = range_limit[(int)Descale(tmp13 + tmp0,
                                                             CONST_BITS + PASS1_BITS + 3) & GetRangeMask()];
                output_buf[ctr][output_col + 4] = range_limit[(int)Descale(tmp13 - tmp0,
                                                             CONST_BITS + PASS1_BITS + 3) & GetRangeMask()];

                wsptr_idx += DCTSIZE;
            }
        }

        // IDCT辅助函数，基于libjpeg-turbo的宏定义
        private int Dequantize(short coef, ushort quantval)
        {
            return coef * quantval;
        }

        private long LeftShift(long a, int b)
        {
            return (long)((ulong)a << b);
        }

        private long Multiply(long var, int constant)
        {
            return var * constant;
        }

        private long Descale(long x, int n)
        {
            return x >> n;
        }

        private ushort[] GetQuantTable(int tbl_no)
        {
            // 简化实现：返回标准量化表
            // 实际实现应该从DecompressInfo中获取
            return new ushort[64] 
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
        }

        private byte[] GetRangeLimit(DecompressInfo cinfo)
        {
            // 简化实现：创建范围限制表
            // 实际实现应该从cinfo中获取预计算的表
            byte[] range_limit = new byte[1024];
            for (int i = 0; i < 256; i++)
            {
                range_limit[i + 384] = (byte)i; // 正常范围
            }
            for (int i = 0; i < 384; i++)
            {
                range_limit[i] = 0; // 下溢
            }
            for (int i = 640; i < 1024; i++)
            {
                range_limit[i] = 255; // 上溢
            }
            return range_limit;
        }

        private int GetRangeMask()
        {
            return 1023; // 10位掩码
        }

        private bool DecodeMcu(DecompressInfo cinfo, short[][][] MCU_data)
        {
            // 使用真正的霍夫曼解码器
            return cinfo.entropy.huffman_decoder.DecodeMcuSlow(cinfo, MCU_data);
        }



        public bool Decode(byte[] jpegData)
        {
            this.jpegData = jpegData;
            
            // 设置解码器状态为扫描状态
            cinfo.global_state = DecompressState.DSTATE_SCANNING;
            
            // 暂时使用原始解码器来获取基本信息
            // 这部分后续需要用真正的JPEG头部解析来替换
            var originalDecoder = new JpegDecoder();
            if (!originalDecoder.Decode(jpegData))
                return false;
                
            Width = originalDecoder.Width;
            Height = originalDecoder.Height;
            Components = originalDecoder.Components;
            
            cinfo.output_width = Width;
            cinfo.output_height = Height;
            cinfo.output_components = Components;
            cinfo.output_scanline = 0;
            
            // 分配图像数据缓冲区
            ImageData = new byte[Width * Height * Components];
            
            return true;
        }

        /// <summary>
        /// 后处理数据 - 简化实现，直接复制数据
        /// </summary>
        private void PostProcessData(DecompressInfo cinfo, byte[][][] input_buf, 
                                   ref int in_row_group_ctr, int in_row_groups_avail,
                                   byte[][] output_buf, ref int out_row_ctr, int out_rows_avail)
        {
            // 简化实现：直接复制数据
            int rows_to_copy = Math.Min(in_row_groups_avail - in_row_group_ctr, out_rows_avail - out_row_ctr);
            
            for (int i = 0; i < rows_to_copy; i++)
            {
                if (in_row_group_ctr < input_buf.Length && 
                    out_row_ctr < output_buf.Length &&
                    input_buf[in_row_group_ctr] != null &&
                    input_buf[in_row_group_ctr].Length > 0 &&
                    input_buf[in_row_group_ctr][0] != null)
                {
                    Array.Copy(input_buf[in_row_group_ctr][0], 0, 
                              output_buf[out_row_ctr], 0, 
                              Math.Min(input_buf[in_row_group_ctr][0].Length, output_buf[out_row_ctr].Length));
                }
                in_row_group_ctr++;
                out_row_ctr++;
            }
        }
    }
}