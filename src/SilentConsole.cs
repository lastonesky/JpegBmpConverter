using System;

namespace JpegBmpConverter
{
    // 静默控制台：用于屏蔽解码过程中的日志输出
    public static class SilentConsole
    {
        public static void WriteLine() { }
        public static void WriteLine(string value) { }
        public static void WriteLine(string format, params object[] args) { }
    }
}