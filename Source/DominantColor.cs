namespace UniversalEyeCompat
{
    /// <summary>
    /// 从 RGBA 像素里挑出「这张图看起来是什么颜色」。
    ///
    /// 为什么单独成一个文件：
    /// 本类型刻意不引用 UnityEngine 与任何游戏类型，输入输出都是普通数值，
    /// 因此可以被离线测试工程直接编译进去，用 System.Drawing 之类的独立解码器做对照，
    /// 不必启动游戏就能验证算法改动。
    ///
    /// 算法：
    /// 1. 跳过透明像素（Alpha 过低）与过暗像素（瞳孔、描边）；
    /// 2. 按色相分成 24 个桶，另设一个「无彩色」桶装灰白；
    /// 3. 取权重最大的桶，在桶内做 Alpha 加权平均。
    ///
    /// 权重 = Alpha × (基础权重 + 饱和度)：
    /// 加饱和度是为了让彩色虹膜压过白色眼白，否则「红眼」会被眼白冲淡成粉色。
    /// </summary>
    internal static class DominantColor
    {
        /// <summary>色相分桶数量，每桶 15°。</summary>
        public const int HueBuckets = 24;

        /// <summary>低于这个明度的像素当瞳孔 / 描边丢掉，不参与统计。</summary>
        public const float MinValue = 0.10f;

        /// <summary>饱和度低于这个值的像素算无彩色，统一进最后一个桶。</summary>
        public const float AchromaticSaturation = 0.12f;

        /// <summary>
        /// 无彩色且亮过这个值的像素当眼白丢掉。
        /// 只对无彩色生效：高饱和的亮色像素是「亮金色」这类真正的虹膜色，不能一起丢掉。
        /// </summary>
        public const float MaxAchromaticValue = 0.90f;

        /// <summary>饱和度在权重里的基础占比，保证纯灰也有稳定的相对权重。</summary>
        public const float BaseWeight = 0.15f;

        /// <summary>
        /// 求主色。像素按 RGBA 顺序排列，长度为 pixelCount * 4。
        /// 没有可用像素（全透明、全黑）时返回 false。
        /// </summary>
        public static bool TryFind(byte[] rgba, int pixelCount, out float r, out float g, out float b)
        {
            r = 0f;
            g = 0f;
            b = 0f;
            if (rgba == null || pixelCount <= 0 || rgba.Length < pixelCount * 4)
            {
                return false;
            }

            double[] bucketR = new double[HueBuckets + 1];
            double[] bucketG = new double[HueBuckets + 1];
            double[] bucketB = new double[HueBuckets + 1];
            double[] bucketWeight = new double[HueBuckets + 1];

            for (int i = 0; i < pixelCount; i++)
            {
                int offset = i * 4;
                int alpha = rgba[offset + 3];
                if (alpha < 32)
                {
                    continue;
                }
                float pr = rgba[offset] / 255f;
                float pg = rgba[offset + 1] / 255f;
                float pb = rgba[offset + 2] / 255f;

                float max = Max(pr, pg, pb);
                if (max < MinValue)
                {
                    continue;
                }
                float min = Min(pr, pg, pb);
                float saturation = (max - min) / max;
                bool achromatic = saturation < AchromaticSaturation;
                if (achromatic && max > MaxAchromaticValue)
                {
                    // 白色眼白：不是虹膜颜色，丢掉。只对无彩色生效，别把亮金色也丢掉。
                    continue;
                }
                int bucket = achromatic ? HueBuckets : HueBucket(pr, pg, pb, max, min);

                double weight = (alpha / 255.0) * (BaseWeight + saturation);
                bucketR[bucket] += pr * weight;
                bucketG[bucket] += pg * weight;
                bucketB[bucket] += pb * weight;
                bucketWeight[bucket] += weight;
            }

            int best = -1;
            double bestWeight = 0.0;
            for (int i = 0; i <= HueBuckets; i++)
            {
                if (bucketWeight[i] > bestWeight)
                {
                    bestWeight = bucketWeight[i];
                    best = i;
                }
            }
            if (best < 0 || bestWeight <= 0.0)
            {
                return false;
            }
            r = (float)(bucketR[best] / bestWeight);
            g = (float)(bucketG[best] / bestWeight);
            b = (float)(bucketB[best] / bestWeight);
            return true;
        }

        /// <summary>把 RGB 归到 0 ~ HueBuckets-1 的色相桶。</summary>
        private static int HueBucket(float r, float g, float b, float max, float min)
        {
            float delta = max - min;
            if (delta <= 0f)
            {
                return HueBuckets;
            }
            float hue;
            if (max == r)
            {
                hue = (g - b) / delta % 6f;
            }
            else if (max == g)
            {
                hue = (b - r) / delta + 2f;
            }
            else
            {
                hue = (r - g) / delta + 4f;
            }
            hue *= 60f;
            if (hue < 0f)
            {
                hue += 360f;
            }
            int bucket = (int)(hue / (360f / HueBuckets));
            if (bucket < 0)
            {
                return 0;
            }
            return bucket >= HueBuckets ? HueBuckets - 1 : bucket;
        }

        private static float Max(float a, float b, float c)
        {
            float result = a > b ? a : b;
            return result > c ? result : c;
        }

        private static float Min(float a, float b, float c)
        {
            float result = a < b ? a : b;
            return result < c ? result : c;
        }
    }
}
