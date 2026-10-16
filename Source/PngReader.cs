using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace UniversalEyeCompat
{
    /// <summary>
    /// 自带的最小 PNG 解码器。
    ///
    /// 为什么要自己解：
    /// 游戏加载贴图走的是 Texture2D.LoadFromFile / LoadImage，拿到手的是显存贴图，
    /// 多数情况下不保证可读，而且在开启贴图压缩后还会被压成 DXT，读像素必然失败。
    /// 取主色又必须看到真实像素，所以这里直接从模组目录里的 .png 文件读字节自己解。
    ///
    /// 支持：色彩类型 0/2/3/4/6，位深 1/2/4/8/16，标准滤波 0-4。
    /// 不支持：隔行扫描（interlace=1），遇到时返回 false 由上层的名称关键词兜底。
    ///
    /// 注意：本类型只在启动期扫描 Def 时调用，不在每帧路径上，性能不重要，可读性优先。
    /// </summary>
    internal static class PngReader
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        /// <summary>
        /// 读取 PNG 并展开成 RGBA8。失败时返回 false，rgba 置空。
        /// </summary>
        public static bool TryRead(string filePath, out int width, out int height, out byte[] rgba)
        {
            width = 0;
            height = 0;
            rgba = null;

            byte[] file;
            try
            {
                file = File.ReadAllBytes(filePath);
            }
            catch (Exception)
            {
                return false;
            }

            if (file.Length < 8)
            {
                return false;
            }
            for (int i = 0; i < Signature.Length; i++)
            {
                if (file[i] != Signature[i])
                {
                    return false;
                }
            }

            int bitDepth = 8;
            int colorType = 6;
            int interlace = 0;
            byte[] palette = null;
            byte[] transparency = null;
            using (MemoryStream idat = new MemoryStream())
            {
                int pos = 8;
                while (pos + 8 <= file.Length)
                {
                    int length = ReadInt32BigEndian(file, pos);
                    if (length < 0 || pos + 12 + (long)length > file.Length)
                    {
                        return false;
                    }
                    string chunkType = Encoding.ASCII.GetString(file, pos + 4, 4);
                    int dataStart = pos + 8;
                    switch (chunkType)
                    {
                        case "IHDR":
                            width = ReadInt32BigEndian(file, dataStart);
                            height = ReadInt32BigEndian(file, dataStart + 4);
                            bitDepth = file[dataStart + 8];
                            colorType = file[dataStart + 9];
                            interlace = file[dataStart + 12];
                            break;
                        case "PLTE":
                            palette = Copy(file, dataStart, length);
                            break;
                        case "tRNS":
                            transparency = Copy(file, dataStart, length);
                            break;
                        case "IDAT":
                            idat.Write(file, dataStart, length);
                            break;
                    }
                    pos = dataStart + length + 4;
                }

                if (width <= 0 || height <= 0 || interlace != 0)
                {
                    return false;
                }

                int channels = ChannelsOf(colorType);
                if (channels == 0 || bitDepth != 1 && bitDepth != 2 && bitDepth != 4 && bitDepth != 8 && bitDepth != 16)
                {
                    return false;
                }
                // 只有灰度与调色板允许小于 8 的位深。
                if (bitDepth < 8 && colorType != 0 && colorType != 3)
                {
                    return false;
                }
                if (colorType == 3 && (palette == null || palette.Length < 3))
                {
                    return false;
                }

                int rowBytes = (width * channels * bitDepth + 7) / 8;
                int filterStep = Math.Max(1, channels * bitDepth / 8);
                long expected = (long)(rowBytes + 1) * height;
                if (expected > int.MaxValue)
                {
                    return false;
                }

                byte[] inflated;
                try
                {
                    inflated = Inflate(idat.ToArray(), (int)expected + 1);
                }
                catch (Exception)
                {
                    return false;
                }
                if (inflated.Length < expected)
                {
                    return false;
                }

                byte[] raw = new byte[rowBytes * height];
                int cursor = 0;
                for (int y = 0; y < height; y++)
                {
                    int filter = inflated[cursor++];
                    int rowStart = y * rowBytes;
                    int priorStart = rowStart - rowBytes;
                    for (int x = 0; x < rowBytes; x++)
                    {
                        int left = x >= filterStep ? raw[rowStart + x - filterStep] : 0;
                        int up = y > 0 ? raw[priorStart + x] : 0;
                        int upLeft = y > 0 && x >= filterStep ? raw[priorStart + x - filterStep] : 0;
                        int value = inflated[cursor + x];
                        switch (filter)
                        {
                            case 1:
                                value += left;
                                break;
                            case 2:
                                value += up;
                                break;
                            case 3:
                                value += (left + up) / 2;
                                break;
                            case 4:
                                value += Paeth(left, up, upLeft);
                                break;
                        }
                        raw[rowStart + x] = (byte)(value & 0xFF);
                    }
                    cursor += rowBytes;
                }

                rgba = new byte[width * height * 4];
                int maxSample = (1 << bitDepth) - 1;
                for (int y = 0; y < height; y++)
                {
                    int rowStart = y * rowBytes;
                    for (int x = 0; x < width; x++)
                    {
                        int outIndex = (y * width + x) * 4;
                        switch (colorType)
                        {
                            case 0:
                            {
                                int gray = Scale(Sample(raw, rowStart, x, 0, bitDepth, channels), maxSample);
                                rgba[outIndex] = (byte)gray;
                                rgba[outIndex + 1] = (byte)gray;
                                rgba[outIndex + 2] = (byte)gray;
                                rgba[outIndex + 3] = 255;
                                break;
                            }
                            case 2:
                            {
                                rgba[outIndex] = (byte)Scale(Sample(raw, rowStart, x, 0, bitDepth, channels), maxSample);
                                rgba[outIndex + 1] = (byte)Scale(Sample(raw, rowStart, x, 1, bitDepth, channels), maxSample);
                                rgba[outIndex + 2] = (byte)Scale(Sample(raw, rowStart, x, 2, bitDepth, channels), maxSample);
                                rgba[outIndex + 3] = 255;
                                break;
                            }
                            case 3:
                            {
                                int index = Sample(raw, rowStart, x, 0, bitDepth, channels);
                                int paletteOffset = index * 3;
                                if (paletteOffset + 2 >= palette.Length)
                                {
                                    rgba[outIndex + 3] = 0;
                                    break;
                                }
                                rgba[outIndex] = palette[paletteOffset];
                                rgba[outIndex + 1] = palette[paletteOffset + 1];
                                rgba[outIndex + 2] = palette[paletteOffset + 2];
                                rgba[outIndex + 3] = transparency != null && index < transparency.Length ? transparency[index] : (byte)255;
                                break;
                            }
                            case 4:
                            {
                                int gray = Scale(Sample(raw, rowStart, x, 0, bitDepth, channels), maxSample);
                                rgba[outIndex] = (byte)gray;
                                rgba[outIndex + 1] = (byte)gray;
                                rgba[outIndex + 2] = (byte)gray;
                                rgba[outIndex + 3] = (byte)Scale(Sample(raw, rowStart, x, 1, bitDepth, channels), maxSample);
                                break;
                            }
                            default:
                            {
                                rgba[outIndex] = (byte)Scale(Sample(raw, rowStart, x, 0, bitDepth, channels), maxSample);
                                rgba[outIndex + 1] = (byte)Scale(Sample(raw, rowStart, x, 1, bitDepth, channels), maxSample);
                                rgba[outIndex + 2] = (byte)Scale(Sample(raw, rowStart, x, 2, bitDepth, channels), maxSample);
                                rgba[outIndex + 3] = (byte)Scale(Sample(raw, rowStart, x, 3, bitDepth, channels), maxSample);
                                break;
                            }
                        }
                    }
                }
            }

            return true;
        }

        private static int ChannelsOf(int colorType)
        {
            switch (colorType)
            {
                case 0:
                    return 1;
                case 2:
                    return 3;
                case 3:
                    return 1;
                case 4:
                    return 2;
                case 6:
                    return 4;
                default:
                    return 0;
            }
        }

        /// <summary>取出第 x 像素第 channel 个分量的原始值（不做归一化）。</summary>
        private static int Sample(byte[] row, int rowStart, int x, int channel, int bitDepth, int channels)
        {
            if (bitDepth >= 8)
            {
                int bytesPerSample = bitDepth / 8;
                int offset = rowStart + (x * channels + channel) * bytesPerSample;
                if (bytesPerSample == 2)
                {
                    // 16 位按高字节处理即可，取色不需要更高精度。
                    return row[offset];
                }
                return row[offset];
            }

            int bitIndex = (x * channels + channel) * bitDepth;
            int byteIndex = rowStart + (bitIndex >> 3);
            int shift = 8 - bitDepth - (bitIndex & 7);
            int mask = (1 << bitDepth) - 1;
            return (row[byteIndex] >> shift) & mask;
        }

        private static int Scale(int value, int maxSample)
        {
            return (int)Math.Round(value * 255.0 / maxSample);
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc)
            {
                return a;
            }
            return pb <= pc ? b : c;
        }

        private static byte[] Inflate(byte[] zlibData, int sizeHint)
        {
            using (MemoryStream input = new MemoryStream(zlibData))
            using (ZLibStream zlib = new ZLibStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream(Math.Max(sizeHint, 1024)))
            {
                zlib.CopyTo(output);
                return output.ToArray();
            }
        }

        private static int ReadInt32BigEndian(byte[] buffer, int offset)
        {
            return buffer[offset] << 24 | buffer[offset + 1] << 16 | buffer[offset + 2] << 8 | buffer[offset + 3];
        }

        private static byte[] Copy(byte[] source, int offset, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(source, offset, result, 0, length);
            return result;
        }
    }
}
