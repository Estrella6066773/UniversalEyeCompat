using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 决定一条眼部来源该用什么颜色。
    ///
    /// 取色顺序（命中即停）：
    /// 1. 来源自己声明的颜色（原版渲染节点的 color / colorType，含跟随发色/肤色）；
    /// 2. 显式色表（EyeColorTables）：原版/DLC/FA 已知项，以及精灵四种瞳色的专用取值；
    /// 3. 贴图路径表：复用原版/精灵贴图的改名变体按路径取既定色；
    /// 4. 贴图主色：按色相分桶、丢弃描边与眼白、按饱和度加权（见 DominantColor）；
    /// 5. 名称关键词：defName / label / 贴图路径里的颜色词；
    /// 6. 基因图标：没有颜色声明、名字里也没颜色词的基因，最后从自己的图标取主色；
    /// 7. 都失败就放弃这条来源，只记一条日志。
    ///
    /// 注意：色表只在「Def 数据库里还没有对应 EyeballColorDef」时才会被用到，
    /// 所以装了 Experimentals 或专用重绘包时不生效，不会覆盖别人的颜色。
    /// </summary>
    public static class EyeColorResolver
    {
        /// <summary>
        /// <paramref name="allowGuessing"/> 为 false 时只接受「来源自己声明的颜色」。
        ///
        /// 种族级来源必须传 false：它的颜色会套到整个种族所有 pawn 身上，
        /// 如果改成按贴图或名称猜，等于把该种族每只 pawn 的瞳色统一覆盖掉。
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

            // 显式色表：基因按 defName，hediff 按 defName。
            //
            // 精灵（Faction - Elves）的四种瞳色就在这里。它们的贴图是一整颗彩色眼球，
            // 采样会被黑色描边与眼白稀释，游戏里表现为「灰蒙蒙的一团」，
            // 所以这一层必须先于贴图采样命中。
            Color tableColor;
            if (source.Kind == EyeSourceKind.Gene && EyeColorTables.TryGetGeneColor(source.Key, out tableColor))
            {
                spec = EyeColorSpec.FromFixed(tableColor, 1f);
                origin = source.Elf ? "table(精灵色表)" : "table";
                return true;
            }
            if (source.Kind == EyeSourceKind.Hediff && EyeColorTables.TryGetHediffColor(source.Key, out tableColor))
            {
                spec = EyeColorSpec.FromFixed(tableColor, 1f);
                origin = "table";
                return true;
            }

            // 贴图路径表：改名复用的原版 / 精灵贴图。
            if (EyeColorTables.TryGetTextureColor(source.TexturePaths, out Color fromPath))
            {
                spec = EyeColorSpec.FromFixed(fromPath, 1f);
                origin = EyeColorTables.IsElfSource(source.Key, source.TexturePaths) ? "texture-path(精灵贴图)" : "texture-path";
                return true;
            }

            if (TextureColorSampler.TrySample(source.TexturePaths, out Color sampled))
            {
                spec = EyeColorSpec.FromFixed(ColorUtil.Saturate(sampled, 1.2f), 1f);
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
            // 像 Vanilla Races Expanded 的复眼/单眼基因，既没写颜色、名字里也没有任何颜色词，
            // 唯一的视觉意图就在图标里。放在关键词之后是因为「名字里写了颜色」更明确。
            // 安全性来自 DominantColor 本身：它会丢弃纯白眼白与近黑瞳孔。
            if (!string.IsNullOrEmpty(source.IconPath) && TextureColorSampler.TrySample(new[] { source.IconPath }, out Color fromIcon))
            {
                spec = EyeColorSpec.FromFixed(ColorUtil.Saturate(fromIcon, 1.2f), 1f);
                origin = "icon";
                return true;
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
