namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 从 RGBA 像素里挑出「这张图的眼睛是什么颜色」。
    ///
    /// 本类型刻意不引用 UnityEngine 与任何游戏类型，输入输出都是普通数值，
    /// 因此可以被离线测试工程直接编译进去，不必启动游戏就能验证算法改动。
    ///
    /// 算法（两趟）：
    /// 1. 第一趟只统计「有彩色」的像素：Alpha 足够、明度不太暗（排除瞳孔与描边）、
    ///    饱和度够高（排除眼白与灰色抗锯齿）；
    /// 2. 按色相分 24 个桶，取权重最大的桶，桶内做饱和度为权的平均；
    /// 3. 第一趟一个像素都没有（灰度眼、纯色贴图）时，第二趟放宽到所有不透明像素。
    ///
    /// 为什么先只看彩色像素：
    /// 眼贴图里同时有白色眼白、黑色瞳孔与一圈黑色描边，还有大量低饱和的抗锯齿边缘。
    /// 直接平均会把红色算成粉色、蓝色算成灰蓝、黄色算成土黄 —— 精灵那种「一整颗彩色眼球」
    /// 的贴图尤其明显。先筛掉无彩色像素，剩下的就是虹膜本身。
    ///
    /// 权重 = Alpha × (基础权重 + 饱和度)，保证「饱和的虹膜像素」压过「浅色的高光」，
    /// 但高光仍然参与（它决定这颗眼睛偏亮还是偏暗）。
    /// </summary>
    internal static class DominantColor
    {
        /// <summary>色相分桶数量，每桶 15°。</summary>
        public const int HueBuckets = 24;

        /// <summary>低于这个明度的像素当瞳孔 / 描边丢掉。</summary>
        public const float MinValue = 0.12f;

        /// <summary>第一趟「只要彩色」时的饱和度门槛。</summary>
        public const float IrisSaturation = 0.18f;

        /// <summary>无彩色且亮过这个值的像素当眼白丢掉。</summary>
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

            // 第一趟：只收虹膜色的像素。
            if (Find(rgba, pixelCount, true, out r, out g, out b))
            {
                return true;
            }
            // 第二趟：灰度眼、纯色贴图，退回到所有不透明像素。
            return Find(rgba, pixelCount, false, out r, out g, out b);
        }

        private static bool Find(byte[] rgba, int pixelCount, bool colorfulOnly, out float r, out float g, out float b)
        {
            r = 0f;
            g = 0f;
            b = 0f;

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
                bool achromatic = saturation < 0.12f;
                if (achromatic && max > MaxAchromaticValue)
                {
                    // 白色眼白：不是虹膜颜色。
                    continue;
                }
                if (colorfulOnly && saturation < IrisSaturation)
                {
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
