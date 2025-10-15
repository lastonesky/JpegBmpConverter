using System;

namespace JpegBmpConverter
{
    /// <summary>
    /// Pillow 风格 JPEG 解码器（基线部分）
    /// 将基线解码相关逻辑与渐进式解码分离，便于维护与扩展。
    /// </summary>
    public partial class PillowStyleJpegDecoder
    {
        /// <summary>
        /// 解码单个DCT块（基线JPEG）
        /// - 解码DC系数并进行差分复原
        /// - 解码AC系数直至EOB
        /// </summary>
        /// <param name="coeffs">输出的64个系数缓冲区</param>
        /// <param name="componentIndex">当前分量索引</param>
        /// <returns>解码是否成功</returns>
        private bool DecodeDCTBlockBaseline(short[] coeffs, int componentIndex)
        {
            // 解码DC系数
            coeffs[0] = DecodeDCCoeff(componentIndex);
            // 解码AC系数
            DecodeACCoeffs(coeffs, componentIndex);
            return true;
        }
    }
}