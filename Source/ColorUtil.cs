using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniversalEyeCompat
{
    /// <summary>
    /// 颜色小工具：饱和度调节与关键词猜色。
    /// </summary>
    public static class ColorUtil
    {
        /// <summary>
        /// 把颜色按 factor 放大饱和度（factor &gt; 1 更鲜艳）。贴图取平均会把颜色拉灰，这里补一点。
        /// </summary>
        public static Color Saturate(Color color, float factor)
        {
            float max = Math.Max(color.r, Math.Max(color.g, color.b));
            float min = Math.Min(color.r, Math.Min(color.g, color.b));
            float luminance = (max + min) * 0.5f;
            Color result = new Color(
                luminance + (color.r - luminance) * factor,
                luminance + (color.g - luminance) * factor,
                luminance + (color.b - luminance) * factor,
                1f);
            result.r = Mathf.Clamp01(result.r);
            result.g = Mathf.Clamp01(result.g);
            result.b = Mathf.Clamp01(result.b);
            return result;
        }

        /// <summary>
        /// 按 defName / label / 贴图路径里的颜色词猜一个瞳色。
        ///
        /// 顺序有讲究，分两档：
        /// 1. 前面全是真正的颜色词，长词、具体词排在前面，避免「goldeneyes」先命中别的词根；
        /// 2. 最后几条是「材质词」（bionic / prosthetic / optical / archotech），
        ///    它们不是颜色，只有在没有任何颜色词可用时才拿来当义眼的兜底色。
        /// </summary>
        private static readonly (string Keyword, Color Color)[] Keywords =
        {
            ("magenta", new Color(0.85f, 0.20f, 0.70f)),
            ("turquoise", new Color(0.20f, 0.85f, 0.85f)),
            ("amethyst", new Color(0.62f, 0.30f, 0.88f)),
            ("emerald", new Color(0.15f, 0.85f, 0.40f)),
            ("scarlet", new Color(0.85f, 0.10f, 0.12f)),
            ("crimson", new Color(0.78f, 0.06f, 0.16f)),
            ("sapphire", new Color(0.20f, 0.42f, 0.92f)),
            ("carmine", new Color(0.75f, 0.08f, 0.25f)),
            ("blood", new Color(0.72f, 0.06f, 0.10f)),
            ("cyan", new Color(0.20f, 0.82f, 0.88f)),
            ("teal", new Color(0.13f, 0.62f, 0.60f)),
            ("rose", new Color(0.95f, 0.40f, 0.62f)),
            ("pink", new Color(0.95f, 0.45f, 0.68f)),
            ("violet", new Color(0.60f, 0.28f, 0.88f)),
            ("purple", new Color(0.58f, 0.22f, 0.82f)),
            ("indigo", new Color(0.32f, 0.20f, 0.72f)),
            ("orange", new Color(0.95f, 0.55f, 0.15f)),
            ("amber", new Color(0.95f, 0.68f, 0.16f)),
            ("gold", new Color(0.93f, 0.76f, 0.22f)),
            ("golden", new Color(0.93f, 0.76f, 0.22f)),
            ("yellow", new Color(0.92f, 0.85f, 0.20f)),
            ("yellowish", new Color(0.92f, 0.85f, 0.20f)),
            ("silver", new Color(0.82f, 0.84f, 0.86f)),
            ("white", new Color(0.93f, 0.93f, 0.93f)),
            ("pale", new Color(0.85f, 0.86f, 0.88f)),
            ("gray", new Color(0.76f, 0.76f, 0.76f)),
            ("grey", new Color(0.76f, 0.76f, 0.76f)),
            ("green", new Color(0.22f, 0.80f, 0.34f)),
            ("jade", new Color(0.18f, 0.72f, 0.52f)),
            ("blue", new Color(0.24f, 0.52f, 0.95f)),
            ("azure", new Color(0.20f, 0.60f, 0.95f)),
            ("brown", new Color(0.45f, 0.30f, 0.20f)),
            ("hazel", new Color(0.55f, 0.42f, 0.22f)),
            ("red", new Color(0.85f, 0.13f, 0.13f)),
            ("black", new Color(0.10f, 0.10f, 0.12f)),

            // 以下是「材质词」而不是颜色词，故意排在最后，只有前面所有颜色词都没命中时才用。
            // 义眼 / 光学装置在贴图上通常就是原版 BionicEye 那一层青蓝色，用它当兜底最不容易出错。
            // 只有在「已确认是眼部来源」的前提下才会走到这里（调用方只对眼部来源取色），
            // 所以 implant / synthetic 这类泛词不会把非眼部件的东西染成青色。
            ("bionic", new Color(0.25f, 0.85f, 1f)),
            ("prosthetic", new Color(0.25f, 0.85f, 1f)),
            ("optical", new Color(0.25f, 0.85f, 1f)),
            ("ocular", new Color(0.25f, 0.85f, 1f)),
            ("cyber", new Color(0.25f, 0.85f, 1f)),
            ("synthetic", new Color(0.25f, 0.85f, 1f)),
            ("robotic", new Color(0.25f, 0.85f, 1f)),
            ("implant", new Color(0.25f, 0.85f, 1f)),
            ("archotech", new Color(0.85f, 1f, 0.25f))
        };

        /// <summary>按关键词猜色；猜不到返回 false。</summary>
        public static bool TryGuessFromName(string defName, string label, out Color color)
        {
            return TryGuess((defName ?? string.Empty) + " " + (label ?? string.Empty), out color);
        }

        /// <summary>
        /// 按任意文本猜色。调用方可以把 defName、label、甚至贴图路径拼进来：
        /// 原版与不少模组的眼睛贴图目录名本身就带颜色词（GrayEyes、PurpleEyes），比定义名更可靠。
        /// </summary>
        public static bool TryGuess(string haystack, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(haystack))
            {
                return false;
            }
            string text = haystack.ToLowerInvariant();
            for (int i = 0; i < Keywords.Length; i++)
            {
                if (text.IndexOf(Keywords[i].Keyword, StringComparison.Ordinal) >= 0)
                {
                    color = Keywords[i].Color;
                    return true;
                }
            }
            return false;
        }
    }
}
