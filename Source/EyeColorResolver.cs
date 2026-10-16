using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace UniversalEyeCompat
{
    /// <summary>
    /// 决定一条眼部来源该用什么颜色。
    ///
    /// 取色顺序（命中即停）：
    /// 1. 来源自己声明的颜色：原版渲染节点的 color / colorType（Hair、Skin）。
    ///    这是模组作者写明的意图，最可信 —— 很多模组的眼睛贴图是灰度遮罩，真颜色在 XML 里，
    ///    只看贴图会把红眼算成灰眼，所以显式颜色必须排第一；
    /// 2. 精选色表：原版 / DLC 已知项，用与 FA 本体、Experimentals 一致的数值，
    ///    这样在「没有装 Experimentals」时义眼与超凡眼也有合理瞳色；
    /// 3. 贴图路径表：复用原版眼睛贴图的改名变体（第三方义眼、模块化义眼、灰眼状态），
    ///    按路径直接给出与官方一致的颜色，比采样更稳；
    /// 4. 贴图主色：读该来源的眼睛叠加贴图（男/女各一张），按色相分桶取权重最大的一桶（见 TextureColorSampler）；
    /// 5. 名称关键词：defName / label / 贴图路径里有 red、gold、purple 之类的词就按词取色，
    ///    最后几条是「材质词」（bionic / prosthetic / optical），用来给义眼兜一个青蓝色；
    /// 6. 基因图标：没有颜色声明、名字里也没颜色词的基因（例：VRE 复眼/单眼），
    ///    最后从自己的图标取主色 —— DominantColor 会丢弃纯白眼白与近黑瞳孔，所以这一步出错空间很小；
    /// 7. 都失败就放弃这条来源，只记一条日志。
    ///
    /// 注意：色表只在「Def 数据库里还没有对应 EyeballColorDef」时才会被用到，
    /// 所以装了 Experimentals 或专用重绘包时不生效，不会覆盖别人的颜色。
    /// </summary>
    public static class EyeColorResolver
    {
        private static readonly Dictionary<string, Color> GeneTable = new Dictionary<string, Color>
        {
            // 取自 FA 本体 1.6/Defs/EyeColorDefs/Biotech.xml
            { "Eyes_Red", new Color(1.0f, 0.152f, 0.568f) },
            { "Eyes_Gray", new Color(0.75f, 0.75f, 0.75f) }
        };

        private static readonly Dictionary<string, Color> HediffTable = new Dictionary<string, Color>
        {
            // 取自 Experimentals 1.6/Defs/EyeColorDefs/defaults.xml，保持与原模组一致
            { "BionicEye", new Color(0.25f, 0.85f, 1f) },
            { "ArchotechEye", new Color(0.85f, 1f, 0.25f) }
        };

        /// <summary>
        /// 按「眼睛贴图路径」认色，排在贴图采样之前。
        ///
        /// 为什么需要这一层（来自实际模组核对）：
        /// 大量模组不新画贴图，而是直接复用原版的义眼/灰眼贴图，只改 defName。实测例子：
        /// - FSFVBE 的 FSFProstheticEye 用 Things/Pawn/Wounds/BionicEye；
        /// - EBSG 的 LTS_ModularBionicEye 也用 Things/Pawn/Wounds/BionicEye（原版义眼升级成模块化）；
        /// - 异象的「虚空之触」与「肉体掌控」都用 GrayEyes 贴图。
        /// 这类改名变体光看 defName 只能靠关键词猜（名字里若没有 bionic/eye 之类词就彻底失效），
        /// 而贴图路径是它们共有的、稳定的线索。
        ///
        /// 值直接对齐原版 / FA / Experimentals 的既有取值，所以命中即「与官方一致」。
        /// 用子串匹配、忽略大小写，路径里的公共部分（HeadAttachments/GrayEyes）即可命中。
        /// </summary>
        private static readonly (string Fragment, Color Color)[] TexturePathTable =
        {
            ("Things/Pawn/Wounds/BionicEye", new Color(0.25f, 0.85f, 1f)),
            ("Things/Pawn/Wounds/ArchotechEye", new Color(0.85f, 1f, 0.25f)),
            ("HeadAttachments/RedEyes", new Color(1.0f, 0.152f, 0.568f)),
            ("HeadAttachments/GrayEyes", new Color(0.75f, 0.75f, 0.75f))
        };

        /// <summary>
        /// <paramref name="allowGuessing"/> 为 false 时只接受「来源自己声明的颜色」。
        ///
        /// 种族级来源必须传 false：它的颜色会套到整个种族所有 pawn 身上，
        /// 如果改成按贴图或名称猜，等于把该种族每只 pawn 的瞳色统一覆盖掉，
        /// 反而毁掉玩家在 FA 里给角色挑的颜色。
        /// </summary>
        public static bool TryResolve(EyeSource source, bool allowGuessing, out EyeColorSpec spec, out string origin)
        {
            spec = default;
            origin = null;
            if (source == null || source.SourceDef == null)
            {
                return false;
            }

            if (source.Declared.HasValue)
            {
                spec = source.Declared.Value;
                origin = "declared(" + spec.Describe() + ")";
                return true;
            }
            if (!allowGuessing)
            {
                return false;
            }

            Dictionary<string, Color> table = source.Kind == EyeSourceKind.Gene ? GeneTable
                : source.Kind == EyeSourceKind.Hediff ? HediffTable
                : null;
            if (table != null && table.TryGetValue(source.Key, out Color fromTable))
            {
                spec = EyeColorSpec.FromFixed(fromTable, 1f);
                origin = "table";
                return true;
            }

            if (TryMatchTexturePath(source.TexturePaths, out Color fromPath))
            {
                spec = EyeColorSpec.FromFixed(fromPath, 1f);
                origin = "texture-path";
                return true;
            }

            if (TextureColorSampler.TrySample(source.TexturePaths, out Color sampled))
            {
                spec = EyeColorSpec.FromFixed(ColorUtil.Saturate(sampled, 1.15f), 1f);
                origin = "texture";
                return true;
            }

            if (ColorUtil.TryGuess(BuildKeywordHaystack(source), out Color guessed))
            {
                spec = EyeColorSpec.FromFixed(guessed, 1f);
                origin = "keyword";
                return true;
            }

            // 最后一层：基因自己的图标。
            //
            // 为什么值得试：像 Vanilla Races Expanded 的复眼/单眼基因，既没写颜色、名字里也没有任何颜色词，
            // 唯一的视觉意图就在图标里（UI/Icons/Genes/Gene_CompoundEye）。
            // 放在关键词之后是因为「名字里写了颜色」比「图标里画的颜色」更明确。
            // 安全性来自 DominantColor 本身：它会丢弃纯白眼白与近黑瞳孔，
            // 所以「白眼睛+红血丝」这类图标不会把瞳孔算成白色，最多算不出颜色而落到放弃。
            if (!string.IsNullOrEmpty(source.IconPath) && TextureColorSampler.TrySample(new[] { source.IconPath }, out Color fromIcon))
            {
                spec = EyeColorSpec.FromFixed(ColorUtil.Saturate(fromIcon, 1.15f), 1f);
                origin = "icon";
                return true;
            }

            return false;
        }

        /// <summary>贴图路径命中已知眼睛贴图 → 用该贴图的既定颜色。</summary>
        private static bool TryMatchTexturePath(List<string> texturePaths, out Color color)
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
                for (int t = 0; t < TexturePathTable.Length; t++)
                {
                    if (path.IndexOf(TexturePathTable[t].Fragment, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        color = TexturePathTable[t].Color;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 关键词兜底的输入：定义名 + 显示名 + 眼睛贴图路径。
        /// 贴图路径很有用 —— 异象的「肉体掌控」和「虚空之触」都复用 GrayEyes 贴图，
        /// 定义名里没有颜色词，路径里有。
        /// </summary>
        private static string BuildKeywordHaystack(EyeSource source)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append(source.SourceDef.defName).Append(' ');
            builder.Append(source.SourceDef.label).Append(' ');
            for (int i = 0; i < source.TexturePaths.Count; i++)
            {
                builder.Append(source.TexturePaths[i]).Append(' ');
            }
            return builder.ToString();
        }
    }
}
