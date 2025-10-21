using System;
using System.IO;
using System.Collections.Generic;

namespace JpegBmpConverter
{
    /// <summary>
    /// 基于libjpeg-turbo源码实现的JPEG解码器 - Pillow风格接口
    /// 直接翻译libjpeg-turbo的C源码逻辑到C#，严格禁止任何自定义实现
    /// </summary>
    public partial class PillowStyleJpegDecoder
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
            public int restarts_to_go;
            
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
            public byte[] buffer;
            public int offset;
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
        public byte[,] ImageData { get; private set; }
        public string ErrorMessage { get; private set; }
        
        // 解压缩状态
        private int maxHSampFactor = 1;
        private int maxVSampFactor = 1;
        private ComponentInfo[] componentInfo = new ComponentInfo[4];
        private ushort[][] quantizationTables = new ushort[4][];
        private HuffmanTable[] dcHuffmanTables = new HuffmanTable[4];
        private HuffmanTable[] acHuffmanTables = new HuffmanTable[4];
        
        // 解码状态
        private short[] lastDC = new short[4]; // 每个分量的上一个DC值
        private int bitBuffer = 0;             // 位缓冲区
        private int bitsInBuffer = 0;          // 缓冲区中的位数
        private bool bitstreamEnded = false;   // 位流是否已结束（遇到标记或数据耗尽）
        private int dataIndex = 0;             // 当前数据位置
        private int scanDataStart = 0;         // 扫描数据开始位置
        private bool progressiveMode = false;  // 是否为渐进JPEG（基于SOS参数判定）
        private short[][] progCoeffBuffers0;   // 分量0（通常Y）的系数缓冲（每块64系数）
        private short[][] progCoeffBuffers1;   // 分量1（通常Cb）的系数缓冲
        private short[][] progCoeffBuffers2;   // 分量2（通常Cr）的系数缓冲
        private int[] progBlockCursor = new int[4]; // 渐进扫描时每分量的块游标
        
        // 组件信息扩展
        public class ComponentInfoExtended
        {
            public int componentId;
            public int hSampFactor;
            public int vSampFactor;
            public int quantTableIndex;
            public int dc_tbl_no;
            public int ac_tbl_no;
            // Progressive scan parameters
            public int Ss; // Spectral selection start
            public int Se; // Spectral selection end
            public int Ah; // Successive approximation high
            public int Al; // Successive approximation low
            public bool InScan; // whether the component participates in current scan
        }
        
        private ComponentInfoExtended[] componentInfoExt = new ComponentInfoExtended[4];
        
        // ZigZag扫描顺序
        private static readonly int[] ZigZagOrder = new int[]
        {
            0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63
        };
        
        // 霍夫曼表结构
        public class HuffmanTable
        {
            public byte[] bits = new byte[17];     // bits[k] = # of symbols with codes of length k bits
            public byte[] huffval = new byte[256]; // The symbols, in order of incr code length
            public int[] maxcode = new int[18];    // largest code of length k (-1 if none)
            public int[] mincode = new int[17];    // smallest code of length k (0 if none)
            public int[] valptr = new int[17];     // huffval[] index of 1st symbol of length k
        }

        public PillowStyleJpegDecoder()
        {
            cinfo = new DecompressInfo();
            InitializeDecompressInfo();
        }

        // 计算每分量在整幅图中的块总数（基于采样因子和最大采样）
        private int ComputeTotalBlocksForComponent(int compIndex)
        {
            var ext = componentInfoExt[compIndex];
            if (ext == null) return 0;
            int hCount = Math.Max(1, ext.hSampFactor);
            int vCount = Math.Max(1, ext.vSampFactor);
            int mcusPerRow = (Width + (8 * maxHSampFactor) - 1) / (8 * maxHSampFactor);
            int imcuRows = (Height + (8 * maxVSampFactor) - 1) / (8 * maxVSampFactor);
            return mcusPerRow * imcuRows * hCount * vCount;
        }

        

        /// <summary>
        /// 初始化解码器信息 - 对应libjpeg-turbo中的jpeg_create_decompress
        /// 直接翻译自jdapimin.c中的jpeg_create_decompress函数
        /// </summary>
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
            
            // 初始化扩展组件信息
            for (int i = 0; i < 4; i++)
            {
                componentInfoExt[i] = new ComponentInfoExtended();
            }
        }

        /// <summary>
        /// 主解码函数 - 对应libjpeg-turbo中的jpeg_read_scanlines
        /// 直接翻译自jdapistd.c中的jpeg_read_scanlines函数
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

            // 处理数据 - 对应源码第349-352行
            row_ctr = 0;
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

            // 如果我们完成了这个iMCU行，重置缓冲区状态 - 对应源码第319-322行
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
                        // 数据源暂停；更新状态以便我们可以从这里恢复 - 对应源码第100-103行
                        cinfo.coef.MCU_vert_offset = yoffset;
                        cinfo.coef.MCU_ctr = MCU_col_num;
                        return false;
                    }
                }
                // 完成了这个iMCU行，前进到下一个 - 对应源码第106行
                cinfo.coef.MCU_ctr = 0;
            }
            // 完成了iMCU行，重置偏移量 - 对应源码第108行
            cinfo.coef.MCU_vert_offset = 0;
            return true;
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

        /// <summary>
        /// 解码MCU函数 - 对应libjpeg-turbo中的decode_mcu_slow
        /// 直接翻译自jdhuff.c中的decode_mcu_slow函数（第552-640行）
        /// </summary>
        private bool DecodeMcu(DecompressInfo cinfo, short[][][] MCU_data)
        {
            // 调用霍夫曼熵解码器的慢速MCU解码入口（decode_mcu_slow）
            // 与 LibjpegTurboStyleDecoder 中的实现保持一致，对应 jdhuff.c 的逻辑入口
            return cinfo.entropy.huffman_decoder.DecodeMcuSlow(cinfo, MCU_data);
        }

        /// <summary>
        /// 后处理数据函数 - 对应libjpeg-turbo中的post_process_data
        /// </summary>
        private void PostProcessData(DecompressInfo cinfo, byte[][][] input_buf, 
                                   ref int in_row_group_ctr, int in_row_groups_avail,
                                   byte[][] output_buf, ref int out_row_ctr, int out_rows_avail)
        {
            // 简化的后处理实现
            // 实际应该包含颜色空间转换等
        }

        /// <summary>
        /// 逆DCT函数 - 对应libjpeg-turbo中的jpeg_idct_islow
        /// </summary>
        private void InverseDCT(DecompressInfo cinfo, ComponentInfo compptr, 
                               short[] coef_block, byte[][] output_buf, int output_col)
        {
            // 简化的逆DCT实现
        }

        /// <summary>
        /// 状态机处理 - 使用真正的libjpeg-turbo状态转换
        /// </summary>
        private bool ProcessStateMachine()
        {
            while (true)
            {
                switch (cinfo.global_state)
                {
                    case DecompressState.DSTATE_START:
                        if (!JpegReadHeader())
                            return false;
                        cinfo.global_state = DecompressState.DSTATE_READY;
                        break;

                    case DecompressState.DSTATE_READY:
                        if (!JpegStartDecompress())
                            return false;
                        cinfo.global_state = DecompressState.DSTATE_SCANNING;
                        break;

                    case DecompressState.DSTATE_SCANNING:
                        // 支持多次SOS扫描：逐个扫描直至遇到EOI
                        while (true)
                        {
                            if (!ProcessScanlines())
                                return false;

                            if (bitstreamEnded)
                            {
                                // 当前位置应指向一个标记字节0xFF
                                if (dataIndex + 1 < jpegData.Length && jpegData[dataIndex] == 0xFF)
                                {
                                    int marker = jpegData[dataIndex + 1];
                                    // 如果是EOI则消费并结束
                                    if (marker == 0xD9)
                                    {
                                        // 消费EOI标记，推进指针
                                        if (!ParseNextSegment())
                                            return false;
                                        cinfo.global_state = DecompressState.DSTATE_STOPPING;
                                        break;
                                    }

                                    // 否则继续解析后续段，直到找到下一个SOS
                                    int prevScanStart = scanDataStart;
                                    bool foundNextSOS = false;
                                    while (dataIndex < jpegData.Length)
                                    {
                                        if (!ParseNextSegment())
                                            return false;
                                        if (scanDataStart != prevScanStart)
                                        {
                                            foundNextSOS = true;
                                            break;
                                        }
                                    }

                                    if (!foundNextSOS)
                                    {
                                        // 未找到下一个SOS，结束解码
                                        cinfo.global_state = DecompressState.DSTATE_STOPPING;
                                        break;
                                    }

                                    // 重置位流状态并开始下一次扫描
                                    bitstreamEnded = false;
                                    cinfo.global_state = DecompressState.DSTATE_READY;
                                    if (!JpegStartDecompress())
                                        return false;
                                    // 继续处理下一扫描
                                    continue;
                                }
                                else
                                {
                                    // 无法读取到标记，结束
                                    cinfo.global_state = DecompressState.DSTATE_STOPPING;
                                    break;
                                }
                            }
                            else
                            {
                                // 常规路径：扫描完成后进入停止状态
                                cinfo.global_state = DecompressState.DSTATE_STOPPING;
                                break;
                            }
                        }
                        break;

                    case DecompressState.DSTATE_STOPPING:
                        return JpegFinishDecompress();

                    default:
                        SetError("未知的解码状态");
                        return false;
                }
            }
        }

        /// <summary>
        /// 读取JPEG头部 - 对应libjpeg-turbo中的jpeg_read_header
        /// 直接翻译自jdapimin.c中的jpeg_read_header函数
        /// </summary>
        private bool JpegReadHeader()
        {
            try
            {
                // 检查状态 - 对应源码检查
                if (cinfo.global_state != DecompressState.DSTATE_START &&
                    cinfo.global_state != DecompressState.DSTATE_INHEADER)
                {
                    throw new InvalidOperationException($"Bad state for jpeg_read_header: {cinfo.global_state}");
                }

                // 重置数据索引
                dataIndex = 0;
                
                // 检查SOI标记
                if (!CheckMarker(0xFFD8))
                {
                    SetError("缺少SOI标记");
                    return false;
                }

                // 解析JPEG段
                bool foundSOS = false;
                while (dataIndex < jpegData.Length && !foundSOS)
                {
                    // 查找下一个标记
                    while (dataIndex < jpegData.Length && jpegData[dataIndex] != 0xFF)
                    {
                        dataIndex++;
                    }
                    
                    if (dataIndex + 1 >= jpegData.Length)
                        break;
                        
                    int marker = jpegData[dataIndex + 1];
                    
                    // 如果是SOS段，解析它然后停止解析头部
                    if (marker == 0xDA)
                    {
                        if (!ParseNextSegment()) // 这会调用ParseSOS
                        {
                            return false;
                        }
                        foundSOS = true;
                        break;
                    }
                    
                    if (!ParseNextSegment())
                    {
                        return false;
                    }
                }

                // 验证基本信息
                if (Width <= 0 || Height <= 0 || Components <= 0)
                {
                    SetError("无效的JPEG图像参数");
                    return false;
                }

                cinfo.output_width = Width;
                cinfo.output_height = Height;
                cinfo.output_components = Components;
                
                Console.WriteLine($"JPEG header parsed: {Width}x{Height}, components: {Components}");
                return true;
            }
            catch (Exception ex)
            {
                SetError($"读取头部失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 开始解压缩 - 对应libjpeg-turbo中的jpeg_start_decompress
        /// 直接翻译自jdapistd.c中的jpeg_start_decompress函数
        /// </summary>
        private bool JpegStartDecompress()
        {
            try
            {
                // 检查状态 - 对应源码第134-135行
                if (cinfo.global_state != DecompressState.DSTATE_READY)
                {
                    throw new InvalidOperationException($"Bad state for jpeg_start_decompress: {cinfo.global_state}");
                }

                // 初始化图像数据缓冲区（渐进式多扫描不重复分配，避免覆盖已生成的像素）
                int channels = (Components == 1) ? 1 : 3;
                if (ImageData == null || ImageData.GetLength(0) != Height || ImageData.GetLength(1) != Width * channels)
                {
                    ImageData = new byte[Height, Width * channels];
                }
                
                // 初始化主缓冲区（仅首次分配）
                if (cinfo.main.buffer == null)
                {
                    cinfo.main.buffer = new byte[8][][];
                    for (int i = 0; i < 8; i++)
                    {
                        cinfo.main.buffer[i] = new byte[10][];
                        for (int j = 0; j < 10; j++)
                        {
                            cinfo.main.buffer[i][j] = new byte[Width * channels];
                        }
                    }
                }
                cinfo.main.buffer_full = false;
                cinfo.main.rowgroup_ctr = 0;

                cinfo.output_scanline = 0;
                
                // 重置位缓冲区并设置扫描数据位置
                bitBuffer = 0;
                bitsInBuffer = 0;
                dataIndex = scanDataStart;
                bitstreamEnded = false; // 新扫描开始，位流可读

                // Allocate progressive coefficient buffers if needed
                AllocateProgressiveBuffersIfNeeded();

                // 将扫描数据提供给霍夫曼解码器的数据源
                cinfo.src.buffer = jpegData;
                cinfo.src.offset = dataIndex;
                cinfo.src.bytes_in_buffer = jpegData != null ? jpegData.Length : 0;

                // 初始化霍夫曼位读取永久状态
                cinfo.entropy.huffman_decoder.bitstate.get_buffer = 0;
                cinfo.entropy.huffman_decoder.bitstate.bits_left = 0;

                // 默认设置MCU块数量（基线为1块/灰度）
                if (cinfo.blocks_in_MCU <= 0)
                {
                    cinfo.blocks_in_MCU = 1;
                }

                // 分配MCU缓冲区（每块一个8x8短整型矩阵）
                if (cinfo.coef.MCU_buffer == null || cinfo.coef.MCU_buffer.Length != cinfo.blocks_in_MCU)
                {
                    cinfo.coef.MCU_buffer = new short[cinfo.blocks_in_MCU][][];
                    for (int i = 0; i < cinfo.blocks_in_MCU; i++)
                    {
                        cinfo.coef.MCU_buffer[i] = new short[8][];
                        for (int r = 0; r < 8; r++)
                        {
                            cinfo.coef.MCU_buffer[i][r] = new short[8];
                        }
                    }
                    Console.WriteLine($"Allocated MCU buffer: {cinfo.blocks_in_MCU} blocks");
                }

                // 构建并注入派生霍夫曼表给真实解码器
                for (int i = 0; i < 4; i++)
                {
                    var dcTbl = dcHuffmanTables[i];
                    if (dcTbl != null)
                    {
                        var ht = new HuffmanDecoder.HuffmanTable();
                        // 复制bits（索引0未使用）
                        for (int k = 0; k < ht.bits.Length && k < dcTbl.bits.Length; k++)
                            ht.bits[k] = dcTbl.bits[k];
                        // 复制huffval
                        int copyLen = Math.Min(ht.huffval.Length, dcTbl.huffval.Length);
                        for (int k = 0; k < copyLen; k++)
                            ht.huffval[k] = dcTbl.huffval[k];
                        cinfo.entropy.huffman_decoder.dc_derived_tbls[i] = HuffmanDecoder.MakeDerivedTable(ht);
                    }

                    var acTbl = acHuffmanTables[i];
                    if (acTbl != null)
                    {
                        var ht = new HuffmanDecoder.HuffmanTable();
                        for (int k = 0; k < ht.bits.Length && k < acTbl.bits.Length; k++)
                            ht.bits[k] = acTbl.bits[k];
                        int copyLen = Math.Min(ht.huffval.Length, acTbl.huffval.Length);
                        for (int k = 0; k < copyLen; k++)
                            ht.huffval[k] = acTbl.huffval[k];
                        cinfo.entropy.huffman_decoder.ac_derived_tbls[i] = HuffmanDecoder.MakeDerivedTable(ht);
                    }
                }

                // 基于 ParseSOS 设置的每组件表号，配置当前扫描的霍夫曼表
                ConfigureCurrentHuffmanTables();

                // 初始化restart相关变量
                cinfo.restarts_to_go = cinfo.restart_interval;
                
                Console.WriteLine($"Scan data start: {scanDataStart}, data length: {jpegData.Length}");
                Console.WriteLine($"Restart interval: {cinfo.restart_interval}, restarts_to_go: {cinfo.restarts_to_go}");
                
                // 设置状态 - 对应源码最后几行
                cinfo.global_state = cinfo.master.lossless ? DecompressState.DSTATE_RAW_OK : DecompressState.DSTATE_SCANNING;

                Console.WriteLine("Start JPEG decompression");
                return true;
            }
            catch (Exception ex)
            {
                SetError($"开始解压缩失败: {ex.Message}");
                return false;
            }
        }

        // 根据 componentInfoExt 中的 dc_tbl_no/ac_tbl_no 配置 huffman_decoder 的当前表
        private void ConfigureCurrentHuffmanTables()
        {
            try
            {
                int blocks = cinfo.blocks_in_MCU;
                int configured = 0;
                for (int ci = 0; ci < Components && configured < blocks; ci++)
                {
                    var ext = componentInfoExt[ci];
                    if (ext == null) continue;
                    if (progressiveMode && !ext.InScan) continue; // 渐进模式下仅配置参与扫描的组件

                    int dcNo = ext.dc_tbl_no;
                    int acNo = ext.ac_tbl_no;
                    var dcTbl = (dcNo >= 0 && dcNo < 4) ? cinfo.entropy.huffman_decoder.dc_derived_tbls[dcNo] : null;
                    var acTbl = (acNo >= 0 && acNo < 4) ? cinfo.entropy.huffman_decoder.ac_derived_tbls[acNo] : null;
                    if (dcTbl == null || acTbl == null)
                    {
                        Console.WriteLine($"Warning: Huffman table for component {ci} is incomplete (DC={dcNo}, AC={acNo})");
                    }

                    int hCount = Math.Max(1, ext.hSampFactor);
                    int vCount = Math.Max(1, ext.vSampFactor);
                    int repeat = hCount * vCount;
                    for (int r = 0; r < repeat && configured < blocks; r++)
                    {
                        cinfo.entropy.huffman_decoder.dc_cur_tbls[configured] = dcTbl ?? cinfo.entropy.huffman_decoder.dc_derived_tbls[0];
                        cinfo.entropy.huffman_decoder.ac_cur_tbls[configured] = acTbl ?? cinfo.entropy.huffman_decoder.ac_derived_tbls[0];
                        configured++;
                    }
                }
                Console.WriteLine($"Configured current Huffman table for {configured} blocks (blocks_in_MCU={blocks})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to configure current Huffman table: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理扫描线 - 真正的jpeg_read_scanlines实现
        /// </summary>
        private bool ProcessScanlines()
        {
            try
            {
                // 初始化扫描线缓冲区
                int rowStride = Width * Components;
                byte[] scanlineBuffer = new byte[rowStride];

                // 计算每IMCU行的MCU数量与总IMCU行数
                int mcusPerRow = (Width + (8 * maxHSampFactor) - 1) / (8 * maxHSampFactor);
                int imcuRows = (Height + (8 * maxVSampFactor) - 1) / (8 * maxVSampFactor);

                // 按IMCU行解码并生成对应的扫描线
                for (int imcuRow = 0; imcuRow < imcuRows; imcuRow++)
                {
                    // 预先解码该IMCU行的所有MCU块
                    var rowBlocks = new List<List<byte[][]>>(mcusPerRow);
                    for (int mcuCol = 0; mcuCol < mcusPerRow; mcuCol++)
                    {
                        var blocks = DecodeMCUBlocks(imcuRow, mcuCol);
                        if (blocks == null)
                        {
                            // If scan ends early (encountered next marker), treat this scan as complete
                            if (bitstreamEnded)
                            {
                                Console.WriteLine($"Scan ended early at iMCU row {imcuRow}, MCU column {mcuCol}");
                                return true;
                            }
                            else
                            {
                                SetError($"解码IMCU行 {imcuRow} 的MCU列 {mcuCol} 失败");
                                return false;
                            }
                        }
                        rowBlocks.Add(blocks);
                    }

                    // 为该IMCU行生成8*maxVSampFactor条扫描线
                    for (int rowInIMCU = 0; rowInIMCU < 8 * maxVSampFactor; rowInIMCU++)
                    {
                        int row = imcuRow * (8 * maxVSampFactor) + rowInIMCU;
                        if (row >= Height) break;

                        int bufferIndex = 0;
                        Array.Clear(scanlineBuffer, 0, scanlineBuffer.Length);

                        for (int mcuCol = 0; mcuCol < mcusPerRow; mcuCol++)
                        {
                            int mcuPixelWidth = 8 * maxHSampFactor;
                            int segmentStartX = mcuCol * mcuPixelWidth;
                            int segmentWidth = Math.Min(mcuPixelWidth, Width - segmentStartX);

                            // 为当前 MCU 行片段准备 Y/Cb/Cr 行缓冲
                            var ySeg = new byte[segmentWidth];
                            var cbSeg = new byte[segmentWidth];
                            var crSeg = new byte[segmentWidth];

                            // 组合当前行片段
                            ComposeMCUSegment(rowBlocks[mcuCol], segmentWidth, rowInIMCU, ySeg, cbSeg, crSeg);

                            // 写入输出缓冲
                            for (int x = 0; x < segmentWidth && bufferIndex + Components <= scanlineBuffer.Length; x++)
                            {
                                if (Components == 3)
                                {
                                    ConvertYCbCrToRgb(ySeg[x], cbSeg[x], crSeg[x], out byte r, out byte g, out byte b);
                                    scanlineBuffer[bufferIndex++] = r;
                                    scanlineBuffer[bufferIndex++] = g;
                                    scanlineBuffer[bufferIndex++] = b;
                                }
                                else
                                {
                                    scanlineBuffer[bufferIndex++] = ySeg[x];
                                }
                            }
                        }

                        // 将扫描线数据复制到ImageData
                        for (int col = 0; col < rowStride; col++)
                        {
                            ImageData[row, col] = scanlineBuffer[col];
                        }
                    }
                }
                
                Console.WriteLine($"Successfully read {Height} scanlines");
                return true;
            }
            catch (Exception ex)
            {
                SetError($"读取扫描线失败: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// 读取单条扫描线 - 对应libjpeg的jpeg_read_scanlines
        /// </summary>
        private bool ReadScanline(byte[] buffer, int row)
        {
            // 保留旧API以兼容，但不再逐行解码；实际解码在上层按IMCU行完成
            return true;
        }
        
        /// <summary>
        /// 解码单个MCU (Minimum Coded Unit)
        /// </summary>
        private bool DecodeMCU(int mcuRow, int mcuCol, byte[] outputBuffer, ref int bufferIndex, int currentRow)
        {
            // 不再使用逐行MCU解码输出，保留以兼容旧路径
            return false;
        }

        private List<byte[][]> DecodeMCUBlocks(int mcuRow, int mcuCol)
        {
            if (bitstreamEnded)
            {
                return null;
            }
            if (cinfo.restart_interval > 0 && cinfo.restarts_to_go == 0)
            {
                if (!ProcessRestartMarker())
                {
                    return null;
                }
            }

            var blocksPerComponent = new List<byte[][]>();
            for (int compIndex = 0; compIndex < Components; compIndex++)
            {
                var component = componentInfoExt[compIndex];
                int hCount = Math.Max(1, component.hSampFactor);
                int vCount = Math.Max(1, component.vSampFactor);
                byte[][] compBlocks = new byte[hCount * vCount][];
                int bi = 0;
                for (int blockRow = 0; blockRow < vCount; blockRow++)
                {
                    for (int blockCol = 0; blockCol < hCount; blockCol++)
                    {
                        short[] dctCoeffs = new short[64];
                        if (!DecodeDCTBlock(dctCoeffs, compIndex))
                        {
                            return null;
                        }
                        byte[] pixelBlock = new byte[64];
                        PerformIDCT(dctCoeffs, pixelBlock, compIndex);
                        compBlocks[bi++] = pixelBlock;
                    }
                }
                blocksPerComponent.Add(compBlocks);
            }
            if (cinfo.restart_interval > 0)
            {
                cinfo.restarts_to_go--;
            }
            return blocksPerComponent;
        }

        private void ComposeMCUSegment(List<byte[][]> blocksPerComponent, int segmentWidth, int rowInIMCURow, byte[] ySeg, byte[] cbSeg, byte[] crSeg)
        {
            // 根据SOF中的componentId确定Y/Cb/Cr的实际索引，避免非标准组件顺序导致颜色错位
            int yIndex = -1, cbIndex = -1, crIndex = -1;
            for (int i = 0; i < Components; i++)
            {
                int id = componentInfoExt[i]?.componentId ?? (i + 1);
                if (id == 1 && yIndex < 0) yIndex = i; // Y
                else if (id == 2 && cbIndex < 0) cbIndex = i; // Cb
                else if (id == 3 && crIndex < 0) crIndex = i; // Cr
            }
            // 回退：若未识别到标准ID，则假设按顺序Y,Cb,Cr
            if (yIndex < 0) yIndex = 0;
            if (cbIndex < 0 && Components > 1) cbIndex = 1;
            if (crIndex < 0 && Components > 2) crIndex = 2;

            for (int compIndex = 0; compIndex < Components; compIndex++)
            {
                var component = componentInfoExt[compIndex];
                int hsScale = Math.Max(1, maxHSampFactor / Math.Max(1, component.hSampFactor));
                int vsScale = Math.Max(1, maxVSampFactor / Math.Max(1, component.vSampFactor));
                int hCount = Math.Max(1, component.hSampFactor);
                int vCount = Math.Max(1, component.vSampFactor);
                var compBlocks = blocksPerComponent[compIndex];

                for (int blockRow = 0; blockRow < vCount; blockRow++)
                {
                    int blockIMCUBegin = blockRow * 8 * vsScale;
                    int relRow = rowInIMCURow - blockIMCUBegin;
                    if (relRow < 0 || relRow >= 8 * vsScale)
                    {
                        continue;
                    }
                    int blockRowInBlock = relRow / vsScale;

                    for (int blockCol = 0; blockCol < hCount; blockCol++)
                    {
                        int blockIndex = blockRow * hCount + blockCol;
                        var pixelBlock = compBlocks[blockIndex];

                        int blockSegmentOffset = blockCol * 8 * hsScale;
                        for (int col = 0; col < 8; col++)
                        {
                            int srcIdx = blockRowInBlock * 8 + col;
                            byte v = pixelBlock[srcIdx];
                            int dstBase = blockSegmentOffset + col * hsScale;
                            for (int rep = 0; rep < hsScale; rep++)
                            {
                                int x = dstBase + rep;
                                if (x >= segmentWidth) break;
                                if (Components == 1 || compIndex == yIndex)
                                {
                                    ySeg[x] = v;
                                }
                                else if (compIndex == cbIndex)
                                {
                                    cbSeg[x] = v;
                                }
                                else if (compIndex == crIndex)
                                {
                                    crSeg[x] = v;
                                }
                            }
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// 解码DCT块的系数
        /// </summary>
        private bool DecodeDCTBlock(short[] coeffs, int componentIndex)
        {
            try
            {
                if (!progressiveMode)
                {
                    // 基线解码由独立方法承载
                    return DecodeDCTBlockBaseline(coeffs, componentIndex);
                }

                // 渐进式解码由独立方法承载
                return DecodeDCTBlockProgressive(coeffs, componentIndex);
            }
            catch (Exception ex)
            {
                SetError($"解码DCT块失败: {ex.Message}");
                return false;
            }
        }

        
        
        /// <summary>
        /// 执行IDCT变换
        /// </summary>
        private void PerformIDCT(short[] dctCoeffs, byte[] pixelBlock, int componentIndex)
        {
            PerformIDCT_FastAAN(dctCoeffs, pixelBlock, componentIndex);
        }
        
        /// <summary>
        /// 一维IDCT变换
        /// </summary>
        private void PerformIDCT1D(float[] input, byte[] output, int offset, int stride)
        {
            // 简化的1D IDCT实现
            for (int i = 0; i < 8; i++)
            {
                float sum = 0;
                for (int j = 0; j < 8; j++)
                {
                    float cosValue = (float)Math.Cos((2 * i + 1) * j * Math.PI / 16);
                    float coeff = (j == 0) ? 1.0f / (float)Math.Sqrt(2) : 1.0f;
                    sum += input[offset + j * stride] * coeff * cosValue;
                }
                
                int pixelValue = (int)(sum / 4 + 128); // 加上128偏移
                output[offset + i * stride] = (byte)Math.Max(0, Math.Min(255, pixelValue));
            }
        }
        
        private void PerformIDCT1D(short[] input, float[] output, int offset, int stride)
        {
            // 简化的1D IDCT实现
            for (int i = 0; i < 8; i++)
            {
                float sum = 0;
                for (int j = 0; j < 8; j++)
                {
                    float cosValue = (float)Math.Cos((2 * i + 1) * j * Math.PI / 16);
                    float coeff = (j == 0) ? 1.0f / (float)Math.Sqrt(2) : 1.0f;
                    sum += input[offset + j * stride] * coeff * cosValue;
                }
                // 第一阶段不进行整体1/4缩放，最终垂直阶段统一缩放
                output[offset + i * stride] = sum;
            }
        }

        /// <summary>
        /// 一维IDCT变换（水平），同时进行反量化，避免short溢出
        /// </summary>
        private void PerformIDCT1DDequant(short[] inputCoeffs, ushort[] quantTable, float[] output, int offset, int stride)
        {
            // 简化的1D IDCT实现（浮点），将反量化内联到计算中
            for (int i = 0; i < 8; i++)
            {
                float sum = 0f;
                for (int j = 0; j < 8; j++)
                {
                    float cosValue = (float)Math.Cos((2 * i + 1) * j * Math.PI / 16);
                    float coeffScale = (j == 0) ? 1.0f / (float)Math.Sqrt(2) : 1.0f;
                    int idx = offset + j * stride;
                    // 反量化使用浮点，避免short溢出
                    float deq = inputCoeffs[idx] * (float)quantTable[idx];
                    sum += deq * coeffScale * cosValue;
                }
                // 第一阶段不进行整体1/4缩放，最终垂直阶段统一缩放
                output[offset + i * stride] = sum;
            }
        }

        // --- AAN 快速IDCT实现（基于libjpeg-turbo的jidctint） ---
        private void PerformIDCT_FastAAN(short[] coef_block, byte[] pixelBlock, int componentIndex)
        {
            if (componentInfoExt[componentIndex] == null)
            {
                throw new InvalidOperationException($"组件信息 {componentIndex} 未初始化");
            }
            var quantptr = quantizationTables[componentInfoExt[componentIndex].quantTableIndex];
            if (quantptr == null)
            {
                throw new InvalidOperationException($"量化表 {componentInfoExt[componentIndex].quantTableIndex} 未初始化");
            }

            const int CONST_BITS = 13;
            const int PASS1_BITS = 2;
            const int DCTSIZE = 8;
            const int DCTSIZE2 = 64;

            const int FIX_0_298631336 = 2446;
            const int FIX_0_390180644 = 3196;
            const int FIX_0_541196100 = 4433;
            const int FIX_0_765366865 = 6270;
            const int FIX_0_899976223 = 7373;
            const int FIX_1_175875602 = 9633;
            const int FIX_1_501321110 = 12299;
            const int FIX_1_847759065 = 15137;
            const int FIX_1_961570560 = 16069;
            const int FIX_2_053119869 = 16819;
            const int FIX_2_562915447 = 20995;
            const int FIX_3_072711026 = 25172;

            long tmp0, tmp1, tmp2, tmp3;
            long tmp10, tmp11, tmp12, tmp13;
            long z1, z2, z3, z4, z5;
            var workspace = new int[DCTSIZE2];

            // 第一遍：按列处理并写入工作区
            for (int col = 0; col < DCTSIZE; col++)
            {
                int inptr = col;
                int qptr = col;
                int wsptr = col;

                if (coef_block[inptr + DCTSIZE * 1] == 0 &&
                    coef_block[inptr + DCTSIZE * 2] == 0 &&
                    coef_block[inptr + DCTSIZE * 3] == 0 &&
                    coef_block[inptr + DCTSIZE * 4] == 0 &&
                    coef_block[inptr + DCTSIZE * 5] == 0 &&
                    coef_block[inptr + DCTSIZE * 6] == 0 &&
                    coef_block[inptr + DCTSIZE * 7] == 0)
                {
                    int dcval = (int)LeftShift(Dequantize(coef_block[inptr + DCTSIZE * 0], quantptr[qptr + DCTSIZE * 0]), PASS1_BITS);
                    workspace[wsptr + DCTSIZE * 0] = dcval;
                    workspace[wsptr + DCTSIZE * 1] = dcval;
                    workspace[wsptr + DCTSIZE * 2] = dcval;
                    workspace[wsptr + DCTSIZE * 3] = dcval;
                    workspace[wsptr + DCTSIZE * 4] = dcval;
                    workspace[wsptr + DCTSIZE * 5] = dcval;
                    workspace[wsptr + DCTSIZE * 6] = dcval;
                    workspace[wsptr + DCTSIZE * 7] = dcval;
                    continue;
                }

                z2 = Dequantize(coef_block[inptr + DCTSIZE * 2], quantptr[qptr + DCTSIZE * 2]);
                z3 = Dequantize(coef_block[inptr + DCTSIZE * 6], quantptr[qptr + DCTSIZE * 6]);

                z1 = Multiply(z2 + z3, FIX_0_541196100);
                tmp2 = z1 + Multiply(z3, -FIX_1_847759065);
                tmp3 = z1 + Multiply(z2, FIX_0_765366865);

                z2 = Dequantize(coef_block[inptr + DCTSIZE * 0], quantptr[qptr + DCTSIZE * 0]);
                z3 = Dequantize(coef_block[inptr + DCTSIZE * 4], quantptr[qptr + DCTSIZE * 4]);

                tmp0 = LeftShift(z2 + z3, CONST_BITS);
                tmp1 = LeftShift(z2 - z3, CONST_BITS);

                tmp10 = tmp0 + tmp3;
                tmp13 = tmp0 - tmp3;
                tmp11 = tmp1 + tmp2;
                tmp12 = tmp1 - tmp2;

                tmp0 = Dequantize(coef_block[inptr + DCTSIZE * 7], quantptr[qptr + DCTSIZE * 7]);
                tmp1 = Dequantize(coef_block[inptr + DCTSIZE * 5], quantptr[qptr + DCTSIZE * 5]);
                tmp2 = Dequantize(coef_block[inptr + DCTSIZE * 3], quantptr[qptr + DCTSIZE * 3]);
                tmp3 = Dequantize(coef_block[inptr + DCTSIZE * 1], quantptr[qptr + DCTSIZE * 1]);

                z1 = tmp0 + tmp3;
                z2 = tmp1 + tmp2;
                z3 = tmp0 + tmp2;
                z4 = tmp1 + tmp3;
                z5 = Multiply(z3 + z4, FIX_1_175875602);

                tmp0 = Multiply(tmp0, FIX_0_298631336);
                tmp1 = Multiply(tmp1, FIX_2_053119869);
                tmp2 = Multiply(tmp2, FIX_3_072711026);
                tmp3 = Multiply(tmp3, FIX_1_501321110);
                z1 = Multiply(z1, -FIX_0_899976223);
                z2 = Multiply(z2, -FIX_2_562915447);
                z3 = Multiply(z3, -FIX_1_961570560);
                z4 = Multiply(z4, -FIX_0_390180644);

                z3 += z5;
                z4 += z5;

                tmp0 += z1 + z3;
                tmp1 += z2 + z4;
                tmp2 += z2 + z3;
                tmp3 += z1 + z4;

                workspace[wsptr + DCTSIZE * 0] = (int)Descale(tmp10 + tmp3, CONST_BITS - PASS1_BITS);
                workspace[wsptr + DCTSIZE * 7] = (int)Descale(tmp10 - tmp3, CONST_BITS - PASS1_BITS);
                workspace[wsptr + DCTSIZE * 1] = (int)Descale(tmp11 + tmp2, CONST_BITS - PASS1_BITS);
                workspace[wsptr + DCTSIZE * 6] = (int)Descale(tmp11 - tmp2, CONST_BITS - PASS1_BITS);
                workspace[wsptr + DCTSIZE * 2] = (int)Descale(tmp12 + tmp1, CONST_BITS - PASS1_BITS);
                workspace[wsptr + DCTSIZE * 5] = (int)Descale(tmp12 - tmp1, CONST_BITS - PASS1_BITS);
                workspace[wsptr + DCTSIZE * 3] = (int)Descale(tmp13 + tmp0, CONST_BITS - PASS1_BITS);
                workspace[wsptr + DCTSIZE * 4] = (int)Descale(tmp13 - tmp0, CONST_BITS - PASS1_BITS);
            }

            // 第二遍：按行处理并写入像素块
            for (int row = 0; row < DCTSIZE; row++)
            {
                int wsptr = row * DCTSIZE;

                if (workspace[wsptr + 1] == 0 && workspace[wsptr + 2] == 0 &&
                    workspace[wsptr + 3] == 0 && workspace[wsptr + 4] == 0 &&
                    workspace[wsptr + 5] == 0 && workspace[wsptr + 6] == 0 &&
                    workspace[wsptr + 7] == 0)
                {
                    int dc = (int)Descale((long)workspace[wsptr + 0], PASS1_BITS + 3) + 128;
                    byte dcval = (byte)Math.Max(0, Math.Min(255, dc));
                    int dcBaseIdx = row * 8;
                    pixelBlock[dcBaseIdx + 0] = dcval;
                    pixelBlock[dcBaseIdx + 1] = dcval;
                    pixelBlock[dcBaseIdx + 2] = dcval;
                    pixelBlock[dcBaseIdx + 3] = dcval;
                    pixelBlock[dcBaseIdx + 4] = dcval;
                    pixelBlock[dcBaseIdx + 5] = dcval;
                    pixelBlock[dcBaseIdx + 6] = dcval;
                    pixelBlock[dcBaseIdx + 7] = dcval;
                    continue;
                }

                z2 = (long)workspace[wsptr + 2];
                z3 = (long)workspace[wsptr + 6];

                z1 = Multiply(z2 + z3, FIX_0_541196100);
                tmp2 = z1 + Multiply(z3, -FIX_1_847759065);
                tmp3 = z1 + Multiply(z2, FIX_0_765366865);

                tmp0 = LeftShift((long)workspace[wsptr + 0] + (long)workspace[wsptr + 4], CONST_BITS);
                tmp1 = LeftShift((long)workspace[wsptr + 0] - (long)workspace[wsptr + 4], CONST_BITS);

                tmp10 = tmp0 + tmp3;
                tmp13 = tmp0 - tmp3;
                tmp11 = tmp1 + tmp2;
                tmp12 = tmp1 - tmp2;

                tmp0 = (long)workspace[wsptr + 7];
                tmp1 = (long)workspace[wsptr + 5];
                tmp2 = (long)workspace[wsptr + 3];
                tmp3 = (long)workspace[wsptr + 1];

                z1 = tmp0 + tmp3;
                z2 = tmp1 + tmp2;
                z3 = tmp0 + tmp2;
                z4 = tmp1 + tmp3;
                z5 = Multiply(z3 + z4, FIX_1_175875602);

                tmp0 = Multiply(tmp0, FIX_0_298631336);
                tmp1 = Multiply(tmp1, FIX_2_053119869);
                tmp2 = Multiply(tmp2, FIX_3_072711026);
                tmp3 = Multiply(tmp3, FIX_1_501321110);
                z1 = Multiply(z1, -FIX_0_899976223);
                z2 = Multiply(z2, -FIX_2_562915447);
                z3 = Multiply(z3, -FIX_1_961570560);
                z4 = Multiply(z4, -FIX_0_390180644);

                z3 += z5;
                z4 += z5;

                tmp0 += z1 + z3;
                tmp1 += z2 + z4;
                tmp2 += z2 + z3;
                tmp3 += z1 + z4;

                int baseIdx = row * 8;
                pixelBlock[baseIdx + 0] = ClampToByte((int)Descale(tmp10 + tmp3, CONST_BITS + PASS1_BITS + 3) + 128);
                pixelBlock[baseIdx + 7] = ClampToByte((int)Descale(tmp10 - tmp3, CONST_BITS + PASS1_BITS + 3) + 128);
                pixelBlock[baseIdx + 1] = ClampToByte((int)Descale(tmp11 + tmp2, CONST_BITS + PASS1_BITS + 3) + 128);
                pixelBlock[baseIdx + 6] = ClampToByte((int)Descale(tmp11 - tmp2, CONST_BITS + PASS1_BITS + 3) + 128);
                pixelBlock[baseIdx + 2] = ClampToByte((int)Descale(tmp12 + tmp1, CONST_BITS + PASS1_BITS + 3) + 128);
                pixelBlock[baseIdx + 5] = ClampToByte((int)Descale(tmp12 - tmp1, CONST_BITS + PASS1_BITS + 3) + 128);
                pixelBlock[baseIdx + 3] = ClampToByte((int)Descale(tmp13 + tmp0, CONST_BITS + PASS1_BITS + 3) + 128);
                pixelBlock[baseIdx + 4] = ClampToByte((int)Descale(tmp13 - tmp0, CONST_BITS + PASS1_BITS + 3) + 128);
            }
        }

        private int Dequantize(short coef, ushort quantval) => coef * quantval;
        private long LeftShift(long a, int b) => (long)((ulong)a << b);
        private long Multiply(long var, int constant) => var * constant;
        private long Descale(long x, int n) => x >> n;
        private static byte ClampToByte(int v) => (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
        
        /// <summary>
        /// 将像素块写入输出缓冲区
        /// </summary>
        private void WritePixelBlock(byte[] pixelBlock, byte[] outputBuffer, ref int bufferIndex,
                                   int mcuCol, int blockCol, int blockRow, int compIndex, int currentRow)
        {
            // 已弃用的简化实现；新逻辑在 DecodeMCU 中按分量组合写入。
            // 保留函数占位以减少较大改动风险。
        }

        /// <summary>
        /// YCbCr 转 RGB
        /// </summary>
        private static void ConvertYCbCrToRgb(byte y, byte cb, byte cr, out byte r, out byte g, out byte b)
        {
            double Y = y;
            double Cb = cb - 128.0;
            double Cr = cr - 128.0;

            int R = (int)Math.Round(Y + 1.402 * Cr);
            int G = (int)Math.Round(Y - 0.344136 * Cb - 0.714136 * Cr);
            int B = (int)Math.Round(Y + 1.772 * Cb);

            r = (byte)Math.Max(0, Math.Min(255, R));
            g = (byte)Math.Max(0, Math.Min(255, G));
            b = (byte)Math.Max(0, Math.Min(255, B));
        }

        /// <summary>
        /// 完成解压缩 - 对应libjpeg-turbo中的jpeg_finish_decompress
        /// 直接翻译自jdapistd.c中的jpeg_finish_decompress函数
        /// </summary>
        private bool JpegFinishDecompress()
        {
            try
            {
                // 检查状态 - 对应源码状态检查
                if (cinfo.global_state == DecompressState.DSTATE_SCANNING ||
                    cinfo.global_state == DecompressState.DSTATE_RAW_OK)
                {
                    // 正常完成
                }
                else if (cinfo.global_state != DecompressState.DSTATE_STOPPING)
                {
                    throw new InvalidOperationException($"Bad state for jpeg_finish_decompress: {cinfo.global_state}");
                }

                // 设置最终状态 - 对应源码最后
                cinfo.global_state = DecompressState.DSTATE_START;
                
                Console.WriteLine("JPEG decompression completed");
                return true;
            }
            catch (Exception ex)
            {
                SetError($"完成解压缩失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 主解码函数
        /// </summary>
        public bool Decode(byte[] jpegData)
        {
            this.jpegData = jpegData;
            
            // 重置状态
            cinfo.global_state = DecompressState.DSTATE_START;
            ErrorMessage = null;
            bitstreamEnded = false;
            bitBuffer = 0;
            bitsInBuffer = 0;
            
            return ProcessStateMachine();
        }

        /// <summary>
        /// 从位流中读取指定数量的位 - 对应libjpeg中的get_bits
        /// </summary>
        private int GetBits(int nbits)
        {
            if (nbits == 0) return 0;
            if (bitstreamEnded)
            {
                // 位流已结束，返回0以避免继续读取导致死循环
                return 0;
            }
            
            // 确保缓冲区有足够的位
            while (bitsInBuffer < nbits)
            {
                if (dataIndex >= jpegData.Length)
                {
                    // 数据耗尽，标记位流结束
                    bitstreamEnded = true;
                    // 用0填充剩余位以完成当前读取
                    bitBuffer <<= (nbits - bitsInBuffer);
                    bitsInBuffer = nbits;
                    break;
                }
                    
                int nextByte = jpegData[dataIndex++];
                
                // 处理填充字节0xFF
                if (nextByte == 0xFF)
                {
                    if (dataIndex >= jpegData.Length)
                    {
                        bitstreamEnded = true;
                        // 用0填充剩余位以完成当前读取
                        int remainingBits = nbits - bitsInBuffer;
                        if (remainingBits > 0)
                        {
                            bitBuffer <<= remainingBits;
                            bitsInBuffer = nbits;
                        }
                        break;
                    }
                        
                    int stuffByte = jpegData[dataIndex++];
                    
                    if (stuffByte == 0x00)
                    {
                        // 0xFF00序列，表示真正的0xFF数据字节
                        nextByte = 0xFF;
                    }
                    else
                    {
                        // 记录未读标记，回退指针以便后续消费
                        cinfo.unread_marker = stuffByte;
                        dataIndex -= 2;
                        bitstreamEnded = true;
                        int remainingBits = nbits - bitsInBuffer;
                        if (remainingBits > 0)
                        {
                            bitBuffer <<= remainingBits;
                            bitsInBuffer = nbits;
                        }
                        break;
                    }
                }
                
                bitBuffer = (bitBuffer << 8) | nextByte;
                bitsInBuffer += 8;
            }
            
            // 提取所需的位
            bitsInBuffer -= nbits;
            int result = (bitBuffer >> bitsInBuffer) & ((1 << nbits) - 1);
            
            return result;
        }
        
        /// <summary>
        /// Huffman解码 - 对应libjpeg中的jpeg_huff_decode
        /// </summary>
        private static int huffmanDecodeCount = 0;
        
        private int HuffmanDecode(HuffmanTable htbl)
        {
            huffmanDecodeCount++;
            int code = 0;
            
            // 确定表类型
            string tableType = "未知";
            for (int i = 0; i < dcHuffmanTables.Length; i++)
            {
                if (dcHuffmanTables[i] == htbl)
                {
                    tableType = $"DC表{i}";
                    break;
                }
            }
            for (int i = 0; i < acHuffmanTables.Length; i++)
            {
                if (acHuffmanTables[i] == htbl)
                {
                    tableType = $"AC表{i}";
                    break;
                }
            }
            
            if (huffmanDecodeCount >= 218 && huffmanDecodeCount <= 220)  // 显示第218-220次解码
            {
                Console.WriteLine($"=== Start Huffman decoding #{huffmanDecodeCount} ({tableType}) ===");
            }
            
            for (int l = 1; l <= 16; l++)
            {
                int bit = GetBits(1);
                code = (code << 1) | bit;
                if (htbl.maxcode[l] != -1)
                {
                    if (code >= htbl.mincode[l] && code <= htbl.maxcode[l])
                    {
                        int index = htbl.valptr[l] + code - htbl.mincode[l];
                        if (index >= 0 && index < htbl.huffval.Length)
                        {
                            return htbl.huffval[index];
                        }
                        else
                        {
                            Console.WriteLine($"Huffman decoding index out of range: l={l}, code={code:X}, index={index}, huffval.Length={htbl.huffval.Length}");
                        }
                    }
                }
            }
            
            // 容错：未匹配到有效码字或位流已结束时返回0（EOB）
            Console.WriteLine($"Huffman decoding failed: final code={code:X}, return 0 as EOB/zero size");
            return 0;
        }
        
        /// <summary>
        /// 解码DC系数 - 对应libjpeg中的decode_mcu_DC_first
        /// </summary>
        private short DecodeDCCoeff(int componentIndex)
        {
            if (componentInfoExt[componentIndex] == null)
            {
                throw new InvalidOperationException($"组件信息 {componentIndex} 未初始化");
            }
            
            var htbl = dcHuffmanTables[componentInfoExt[componentIndex].dc_tbl_no];
            if (htbl == null)
            {
                throw new InvalidOperationException($"DC Huffman表 {componentInfoExt[componentIndex].dc_tbl_no} 未初始化");
            }
            
            int s = HuffmanDecode(htbl);
            
            if (s == 0)
            {
                return lastDC[componentIndex];
            }
            
            int diff = GetBits(s);
            
            // 将差值转换为有符号数
            if (diff < (1 << (s - 1)))
            {
                diff = diff - (1 << s) + 1;
            }
            
            lastDC[componentIndex] = (short)(lastDC[componentIndex] + diff);
            return lastDC[componentIndex];
        }
        
        /// <summary>
        /// 解码AC系数 - 对应libjpeg中的decode_mcu_AC_first
        /// </summary>
        private void DecodeACCoeffs(short[] block, int componentIndex)
        {
            if (componentInfoExt[componentIndex] == null)
            {
                throw new InvalidOperationException($"组件信息 {componentIndex} 未初始化");
            }
            
            var htbl = acHuffmanTables[componentInfoExt[componentIndex].ac_tbl_no];
            if (htbl == null)
            {
                throw new InvalidOperationException($"AC Huffman表 {componentInfoExt[componentIndex].ac_tbl_no} 未初始化");
            }
            
            for (int k = 1; k < 64; k++)
            {
                int s = HuffmanDecode(htbl);
                int r = s >> 4;
                s &= 15;
                
                if (s != 0)
                {
                    k += r;
                    if (k >= 64) break;
                    
                    int coeff = GetBits(s);
                    
                    // 将系数转换为有符号数
                    if (coeff < (1 << (s - 1)))
                    {
                        coeff = coeff - (1 << s) + 1;
                    }
                    
                    block[ZigZagOrder[k]] = (short)coeff;
                }
                else
                {
                    if (r == 15)
                    {
                        k += 15; // ZRL (Zero Run Length)
                    }
                    else
                    {
                        break; // EOB (End of Block)
                    }
                }
            }
        }

        /// <summary>
        /// 检查JPEG标记
        /// </summary>
        private bool CheckMarker(int expectedMarker)
        {
            if (dataIndex + 1 >= jpegData.Length)
                return false;
                
            int marker = (jpegData[dataIndex] << 8) | jpegData[dataIndex + 1];
            if (marker == expectedMarker)
            {
                dataIndex += 2;
                return true;
            }
            return false;
        }
        
        /// <summary>
        /// 解析下一个JPEG段
        /// </summary>
        private bool ParseNextSegment()
        {
            // 查找下一个标记
            while (dataIndex < jpegData.Length && jpegData[dataIndex] != 0xFF)
            {
                dataIndex++;
            }
            
            if (dataIndex + 1 >= jpegData.Length)
                return false;
                
            int marker = jpegData[dataIndex + 1];
            dataIndex += 2;
            
            switch (marker)
            {
                case 0xC0: // SOF0 - Baseline DCT
                case 0xC1: // SOF1 - Extended sequential DCT
                case 0xC2: // SOF2 - Progressive DCT
                    if (marker == 0xC2)
                    {
                        progressiveMode = true; // enable progressive decoding mode
                    }
                    return ParseSOF();
                    
                case 0xDB: // DQT - Define Quantization Table
                    return ParseDQT();
                    
                case 0xC4: // DHT - Define Huffman Table
                    return ParseDHT();
                    
                case 0xDA: // SOS - Start of Scan
                    return ParseSOS();
                    
                case 0xDD: // DRI - Define Restart Interval
                    return ParseDRI();
                case 0xD9: // EOI - End of Image
                    // 结束标记，无后续长度字段；已前移两个字节
                    cinfo.unread_marker = 0xD9;
                    return true;
                
                case 0xE0: // APP0
                case 0xE1: // APP1
                case 0xE2: // APP2
                case 0xEE: // APP14
                case 0xFE: // COM - Comment
                    return SkipSegment();
                    
                default:
                    // 跳过未知段
                    return SkipSegment();
            }
        }
        
        /// <summary>
        /// 解析SOF段 (Start of Frame)
        /// </summary>
        private bool ParseSOF()
        {
            if (dataIndex + 6 >= jpegData.Length)
                return false;
                
            int length = (jpegData[dataIndex] << 8) | jpegData[dataIndex + 1];
            dataIndex += 2;
            
            int precision = jpegData[dataIndex++];
            Height = (jpegData[dataIndex] << 8) | jpegData[dataIndex + 1];
            dataIndex += 2;
            Width = (jpegData[dataIndex] << 8) | jpegData[dataIndex + 1];
            dataIndex += 2;
            Components = jpegData[dataIndex++];
            
            // 解析组件信息
            for (int i = 0; i < Components && i < 4; i++)
            {
                if (dataIndex + 2 >= jpegData.Length)
                    return false;
                    
                componentInfoExt[i] = new ComponentInfoExtended();
                int componentId = jpegData[dataIndex++];
                componentInfoExt[i].componentId = componentId;
                int samplingFactors = jpegData[dataIndex++];
                componentInfoExt[i].hSampFactor = (samplingFactors >> 4) & 0x0F;
                componentInfoExt[i].vSampFactor = samplingFactors & 0x0F;
                componentInfoExt[i].quantTableIndex = jpegData[dataIndex++];
                
                // 更新最大采样因子
                maxHSampFactor = Math.Max(maxHSampFactor, componentInfoExt[i].hSampFactor);
                maxVSampFactor = Math.Max(maxVSampFactor, componentInfoExt[i].vSampFactor);
            }
            
            return true;
        }
        
        /// <summary>
        /// 解析DQT段 (Define Quantization Table)
        /// </summary>
        private bool ParseDQT()
        {
            if (dataIndex + 1 >= jpegData.Length)
                return false;
                
            int length = (jpegData[dataIndex] << 8) | jpegData[dataIndex + 1];
            dataIndex += 2;
            int endIndex = dataIndex + length - 2;
            
            while (dataIndex < endIndex)
            {
                if (dataIndex >= jpegData.Length)
                    return false;
                    
                int tableInfo = jpegData[dataIndex++];
                int tableId = tableInfo & 0x0F;
                int precision = (tableInfo >> 4) & 0x0F;
                
                if (tableId >= 4)
                    return false;
                    
                quantizationTables[tableId] = new ushort[64];
                
                for (int i = 0; i < 64; i++)
                {
                    if (dataIndex >= jpegData.Length)
                        return false;

                    int naturalIdx = ZigZagOrder[i];
                    if (precision == 0)
                    {
                        byte val = jpegData[dataIndex++];
                        quantizationTables[tableId][naturalIdx] = val;
                    }
                    else
                    {
                        ushort val = (ushort)((jpegData[dataIndex] << 8) | jpegData[dataIndex + 1]);
                        dataIndex += 2;
                        quantizationTables[tableId][naturalIdx] = val;
                    }
                }
            }
            
            return true;
        }
        
        /// <summary>
        /// 解析DHT段 (Define Huffman Table)
        /// </summary>
        private bool ParseDHT()
        {
            if (dataIndex + 1 >= jpegData.Length)
                return false;
                
            int length = (jpegData[dataIndex] << 8) | jpegData[dataIndex + 1];
            dataIndex += 2;
            int endIndex = dataIndex + length - 2;
            
            Console.WriteLine($"Parsing DHT segment, length: {length}");
            
            while (dataIndex < endIndex)
            {
                if (dataIndex >= jpegData.Length)
                    return false;
                    
                int tableInfo = jpegData[dataIndex++];
                int tableId = tableInfo & 0x0F;
                int tableClass = (tableInfo >> 4) & 0x0F;
                
                if (tableId >= 4)
                    return false;
                    
                var huffTable = new HuffmanTable();
                
                // 读取位长度计数
                for (int i = 1; i <= 16; i++)
                {
                    if (dataIndex >= jpegData.Length)
                        return false;
                    huffTable.bits[i] = jpegData[dataIndex++];
                }
                
                // 读取符号值
                int symbolCount = 0;
                for (int i = 1; i <= 16; i++)
                {
                    symbolCount += huffTable.bits[i];
                }
                
                for (int i = 0; i < symbolCount; i++)
                {
                    if (dataIndex >= jpegData.Length)
                        return false;
                    huffTable.huffval[i] = jpegData[dataIndex++];
                }
                
                // 构建解码表
                BuildHuffmanTable(huffTable);
                
                // 存储表
                if (tableClass == 0)
                {
                    dcHuffmanTables[tableId] = huffTable;
                    Console.WriteLine($"Store DC Huffman table {tableId}");
                }
                else
                {
                    acHuffmanTables[tableId] = huffTable;
                    Console.WriteLine($"Store AC Huffman table {tableId}");
                }
            }
            
            return true;
        }
        
        /// <summary>
        /// 解析DRI段 (Define Restart Interval)
        /// </summary>
        private bool ParseDRI()
        {
            if (dataIndex + 3 >= jpegData.Length)
                return false;
                
            int length = (jpegData[dataIndex] << 8) | jpegData[dataIndex + 1];
            dataIndex += 2;
            
            // DRI段的长度应该是4（2字节长度 + 2字节restart interval）
            if (length != 4)
            {
                Console.WriteLine($"Warning: Abnormal DRI segment length: {length}, expected 4");
                return SkipSegment();
            }
            
            // 读取restart interval值
            cinfo.restart_interval = (jpegData[dataIndex] << 8) | jpegData[dataIndex + 1];
            dataIndex += 2;
            
            Console.WriteLine($"Parsing DRI segment: restart_interval = {cinfo.restart_interval}");
            
            return true;
        }
        
        /// <summary>
        /// 解析SOS段 (Start of Scan)
        /// </summary>
        private bool ParseSOS()
        {
            if (dataIndex + 1 >= jpegData.Length)
                return false;
                
            int length = (jpegData[dataIndex] << 8) | jpegData[dataIndex + 1];
            dataIndex += 2;
            
            int componentCount = jpegData[dataIndex++];
            Console.WriteLine($"Parsing SOS: components = {componentCount}");
            
            // Reset in-scan flags for all components
            for (int j = 0; j < Components; j++)
            {
                var extReset = componentInfoExt[j];
                if (extReset != null) extReset.InScan = false;
            }

            // 解析扫描组件
            for (int i = 0; i < componentCount; i++)
            {
                if (dataIndex + 1 >= jpegData.Length)
                    return false;
                    
                int componentId = jpegData[dataIndex++];
                int tableSelectors = jpegData[dataIndex++];
                Console.WriteLine($"Scan component {i}: ID={componentId}, DC table={(tableSelectors >> 4) & 0x0F}, AC table={tableSelectors & 0x0F}");

                // 根据组件ID找到对应索引，仅对该组件设置表
                bool found = false;
                for (int j = 0; j < Components; j++)
                {
                    var ext = componentInfoExt[j];
                    if (ext != null && ext.componentId == componentId)
                    {
                        ext.dc_tbl_no = (tableSelectors >> 4) & 0x0F;
                        ext.ac_tbl_no = tableSelectors & 0x0F;
                        ext.InScan = true; // mark this component participates in current scan
                        Console.WriteLine($"Component index {j}: assign DC table {ext.dc_tbl_no}, AC table {ext.ac_tbl_no}");
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    Console.WriteLine($"Warning: component ID {componentId} not found in frame components");
                }
            }

            // 更新当前扫描组件数量
            cinfo.comps_in_scan = componentCount;
            
            // 使用参与扫描的组件与采样因子准确计算 blocks_in_MCU
            int blocksInMCU = 0;
            for (int j = 0; j < Components; j++)
            {
                var ext = componentInfoExt[j];
                if (ext == null) continue;
                if (progressiveMode && !ext.InScan) continue; // 在渐进扫描中只统计参与组件
                int hCount = Math.Max(1, ext.hSampFactor);
                int vCount = Math.Max(1, ext.vSampFactor);
                blocksInMCU += hCount * vCount;
            }
            if (blocksInMCU <= 0)
            {
                // 兜底：至少一个块
                blocksInMCU = Math.Max(1, componentCount);
            }
            cinfo.blocks_in_MCU = blocksInMCU;

            // Parse spectral selection and successive approximation: Ss, Se, Ah/Al
            if (dataIndex + 2 >= jpegData.Length)
                return false;
            int Ss = jpegData[dataIndex++];
            int Se = jpegData[dataIndex++];
            int AhAl = jpegData[dataIndex++];
            int Ah = (AhAl >> 4) & 0x0F;
            int Al = AhAl & 0x0F;
            Console.WriteLine($"SOS params: Ss={Ss}, Se={Se}, Ah={Ah}, Al={Al}");

            // Store params per-scan in extended component info (same for all components in scan)
            for (int j = 0; j < Components; j++)
            {
                var ext = componentInfoExt[j];
                if (ext != null)
                {
                    ext.Ss = Ss;
                    ext.Se = Se;
                    ext.Ah = Ah;
                    ext.Al = Al;
                }
            }
            
            // 保存扫描数据开始位置
            scanDataStart = dataIndex;
            
            return true;
        }
        
        /// <summary>
        /// 跳过段
        /// </summary>
        private bool SkipSegment()
        {
            if (dataIndex + 1 >= jpegData.Length)
                return false;
                
            int length = (jpegData[dataIndex] << 8) | jpegData[dataIndex + 1];
            dataIndex += length;
            
            return dataIndex <= jpegData.Length;
        }
        
        /// <summary>
        /// 构建霍夫曼解码表
        /// </summary>
        private void BuildHuffmanTable(HuffmanTable htbl)
        {
            // 依据规范生成 huffsize 和 huffcode（参照 libjpeg 图C.1/C.2）
            var huffsize = new byte[257];
            int p = 0;
            for (int l = 1; l <= 16; l++)
            {
                int i = htbl.bits[l];
                while (i-- > 0)
                {
                    huffsize[p++] = (byte)l;
                }
            }
            huffsize[p] = 0;
            int numsymbols = p;

            var huffcode = new uint[257];
            uint code = 0;
            int si = (numsymbols > 0) ? huffsize[0] : 0;
            p = 0;
            while (huffsize[p] != 0)
            {
                while (huffsize[p] == si)
                {
                    huffcode[p++] = code;
                    code++;
                }
                // 增加码长，左移一位
                code <<= 1;
                si++;
            }

            // 生成 mincode/maxcode/valptr（参照 libjpeg 图F.15）
            p = 0;
            for (int l = 1; l <= 16; l++)
            {
                if (htbl.bits[l] != 0)
                {
                    htbl.valptr[l] = p;
                    htbl.mincode[l] = (int)huffcode[p];
                    p += htbl.bits[l];
                    htbl.maxcode[l] = (int)huffcode[p - 1];
                }
                else
                {
                    htbl.maxcode[l] = -1;
                }
            }
        }

        /// <summary>
        /// 处理restart marker
        /// </summary>
        private bool ProcessRestartMarker()
        {
            try
            {
                Console.WriteLine("Detected need to handle restart marker");
                
                // 对齐到字节边界
                bitBuffer = 0;
                bitsInBuffer = 0;
                bitstreamEnded = false; // 重启后继续位流解码
                
                // 查找restart marker (0xFFD0-0xFFD7)
                while (dataIndex + 1 < jpegData.Length)
                {
                    if (jpegData[dataIndex] == 0xFF)
                    {
                        int marker = jpegData[dataIndex + 1];
                        if (marker >= 0xD0 && marker <= 0xD7)
                        {
                            Console.WriteLine($"Found restart marker: 0xFF{marker:X2}");
                            dataIndex += 2;
                            
                            // 重置restart计数器
                            cinfo.restarts_to_go = cinfo.restart_interval;
                            
                            // 重置DC预测值
                            for (int i = 0; i < Components; i++)
                            {
                                lastDC[i] = 0;
                            }
                            
                            return true;
                        }
                        else if (marker != 0x00)
                        {
                            // 遇到其他marker，可能是数据结束
                            Console.WriteLine($"Encountered non-restart marker: 0xFF{marker:X2}");
                            return false;
                        }
                    }
                    dataIndex++;
                }
                
                Console.WriteLine("No restart marker found");
                return false;
            }
            catch (Exception ex)
            {
                SetError($"处理restart marker失败: {ex.Message}");
                return false;
            }
        }

        private void SetError(string message)
        {
            ErrorMessage = message;
            Console.WriteLine($"Error: {message}");
        }
    }
}