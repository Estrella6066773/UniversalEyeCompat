using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 已知瞳色的显式色表。
    ///
    /// 为什么需要它：贴图取色对「一整颗彩色眼球」的贴图不够可靠 —— 黑色描边、白色眼白、
    /// 抗锯齿边缘都会把结果拉暗拉灰，而瞳色是要直接乘在 FA 那张接近白色的眼珠上的，
    /// 偏灰的颜色在游戏里看起来就是「没上色」。所以只要某个来源的颜色是可穷举的，
    /// 就优先用这里写死的值，而不是去猜。
    ///
    /// 三张表，查找顺序由 EyeColorResolver 决定：
    /// 1. GeneColors  —— 基因 defName 精确匹配（原版、FA、精灵、以及复用了精灵贴图的种族模组）；
    /// 2. HediffColors —— 义眼 / 超凡眼这类 hediff defName；
    /// 3. TextureColors —— 贴图路径片段，用来兜住「改了 defName、贴图还是原版那张」的第三方模组。
    ///
    /// 数值来源：
    /// - Eyes_Red / Eyes_Gray 与 FA 本体 1.6/Defs/EyeColorDefs/Biotech.xml 完全一致；
    /// - BionicEye / ArchotechEye 与 Experimentals 的默认值一致；
    /// - 精灵的四种颜色取自 Faction - Elves 的
    ///   Textures/Things/Pawn/Humanlike/HeadAttachments/{Blue,Green,Purple,Yellow}Eyes，
    ///   是虹膜本身的主色（已去掉描边与眼白），不是整张贴图的平均色。
    /// </summary>
    internal static class EyeColorTables
    {
        private static readonly Color FaRed = new Color(1.0f, 0.152f, 0.568f);
        private static readonly Color FaGray = new Color(0.75f, 0.75f, 0.75f);
        private static readonly Color BionicCyan = new Color(0.25f, 0.85f, 1f);
        private static readonly Color ArchotechGreen = new Color(0.85f, 1f, 0.25f);

        /// <summary>精灵（Faction - Elves）：贴图里虹膜的主色。</summary>
        private static readonly Color ElfBlue = new Color(0.24f, 0.55f, 0.85f);
        private static readonly Color ElfGreen = new Color(0.28f, 0.62f, 0.26f);
        private static readonly Color ElfPurple = new Color(0.55f, 0.18f, 0.78f);
        private static readonly Color ElfYellow = new Color(0.96f, 0.80f, 0.32f);

        /// <summary>精灵瞳色基因。也是「这条来源属于精灵兼容层」的判据。</summary>
        public static readonly HashSet<string> ElfGenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Eyes_BlueEye",
            "Eyes_GreenEye",
            "Eyes_PurpleEye",
            "Eyes_YellowEye"
        };

        /// <summary>精灵瞳色对应的贴图目录片段。</summary>
        private static readonly (string Fragment, Color Color)[] ElfTextures =
        {
            ("HeadAttachments/BlueEyes", ElfBlue),
            ("HeadAttachments/GreenEyes", ElfGreen),
            ("HeadAttachments/PurpleEyes", ElfPurple),
            ("HeadAttachments/YellowEyes", ElfYellow)
        };

        private static readonly Dictionary<string, Color> GeneColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            // 原版 + FA 本体
            { "Eyes_Red", FaRed },
            { "Eyes_Gray", FaGray },

            // VRE 的装饰性瞳色基因（复用原版贴图的改名变体）
            { "VRE_Eyes_Red_Cosmetic", FaRed },
            { "VRE_Eyes_Gray_Cosmetic", FaGray },
            { "VREA_Eyes_Red", FaRed },
            { "VREA_Eyes_Gray", FaGray },

            // 精灵（Faction - Elves / ICC.FOV.ELVES）
            { "Eyes_BlueEye", ElfBlue },
            { "Eyes_GreenEye", ElfGreen },
            { "Eyes_PurpleEye", ElfPurple },
            { "Eyes_YellowEye", ElfYellow },

            // 复用精灵贴图的改名变体（例如 VRE 安卓的 VREA_Eyes_BlueEye 用的是同一批贴图）。
            // 这些 def 在某些加载顺序下不一定拿得到贴图路径，按名字兜住最稳。
            { "VREA_Eyes_BlueEye", ElfBlue },
            { "VREA_Eyes_GreenEye", ElfGreen },
            { "VREA_Eyes_PurpleEye", ElfPurple },
            { "VREA_Eyes_YellowEye", ElfYellow }
        };

        private static readonly Dictionary<string, Color> HediffColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            { "BionicEye", BionicCyan },
            { "ArchotechEye", ArchotechGreen }
        };

        /// <summary>
        /// 按贴图路径认色，排在贴图采样之前。顺序即优先级，越具体的放前面。
        /// 用子串匹配、忽略大小写，路径里的公共部分（HeadAttachments/GrayEyes）即可命中。
        /// </summary>
        private static readonly (string Fragment, Color Color)[] TextureColors =
        {
            ("Things/Pawn/Wounds/BionicEye", BionicCyan),
            ("Things/Pawn/Wounds/ArchotechEye", ArchotechGreen),
            ("HeadAttachments/RedEyes", FaRed),
            ("HeadAttachments/GrayEyes", FaGray),
            ("HeadAttachments/BlueEyes", ElfBlue),
            ("HeadAttachments/GreenEyes", ElfGreen),
            ("HeadAttachments/PurpleEyes", ElfPurple),
            ("HeadAttachments/YellowEyes", ElfYellow)
        };

        public static bool TryGetGeneColor(string geneDefName, out Color color)
        {
            color = Color.white;
            return !string.IsNullOrEmpty(geneDefName) && GeneColors.TryGetValue(geneDefName, out color);
        }

        public static bool TryGetHediffColor(string hediffDefName, out Color color)
        {
            color = Color.white;
            return !string.IsNullOrEmpty(hediffDefName) && HediffColors.TryGetValue(hediffDefName, out color);
        }

        public static bool TryGetTextureColor(List<string> texturePaths, out Color color)
        {
            color = Color.white;
            if (texturePaths == null || texturePaths.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < texturePaths.Count; i++)
            {
                string path = texturePaths[i];
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                for (int t = 0; t < TextureColors.Length; t++)
                {
                    if (path.IndexOf(TextureColors[t].Fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        color = TextureColors[t].Color;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>这条来源是不是精灵瞳色（按基因名或贴图目录判断）。</summary>
        public static bool IsElfSource(string defName, List<string> texturePaths)
        {
            if (!string.IsNullOrEmpty(defName) && ElfGenes.Contains(defName))
            {
                return true;
            }
            if (texturePaths == null)
            {
                return false;
            }
            for (int i = 0; i < texturePaths.Count; i++)
            {
                string path = texturePaths[i];
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                for (int t = 0; t < ElfTextures.Length; t++)
                {
                    if (path.IndexOf(ElfTextures[t].Fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
