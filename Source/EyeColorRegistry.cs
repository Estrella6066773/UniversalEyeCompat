using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 本模组的共享状态。启动期扫描一次写入，运行期只读。
    ///
    /// 表分三类，别混：
    /// 1. GeneKeys / HediffKeys —— 只是「这条来源与眼睛有关」的索引，供刷新补丁做廉价判断；
    /// 2. 五张颜色表 —— 都是「FA 表达不了、只能渲染前临时上色」的来源；
    /// 3. ColoredGenes / ColoredHediffs / ColoredTraits —— 「颜色由本模组给出」的来源；
    ///    FaColoredGenes / FaColoredHediffs —— 「颜色由别人（FA 本体 / Experimentals / 重绘包）的
    ///    EyeballColorDef 给出」的来源。
    ///
    /// 第 3 类两张表都表示「FA 会给这颗眼珠上色」，渲染期据此摘掉来源自带的眼部叠加
    /// （见 OverlaySuppressPatch）。区别只在日志里用来区分是谁给的颜色。
    ///
    /// 注意：Initialized 为 false 时所有补丁都必须直接放行，避免在 FA 缺席或扫描失败时干扰游戏。
    /// </summary>
    public static class EyeColorRegistry
    {
        public static bool Initialized;

        /// <summary>已识别为「眼部外观来源」的基因 defName（含只交给 FA 处理的那批）。</summary>
        public static readonly HashSet<string> GeneKeys = new HashSet<string>();

        /// <summary>已识别为「眼部外观来源」的 hediff defName（含只交给 FA 处理的那批）。</summary>
        public static readonly HashSet<string> HediffKeys = new HashSet<string>();

        /// <summary>特性（定义 + 等级）→ 瞳色。特性没有 FA 通道，只能在渲染前临时上色。</summary>
        public static readonly Dictionary<string, EyeColorSpec> TraitColors = new Dictionary<string, EyeColorSpec>();

        /// <summary>不挂在眼球部件上的 hediff → 瞳色（异象的「虚空之触」这类状态）。</summary>
        public static readonly Dictionary<HediffDef, EyeColorSpec> GlobalHediffColors = new Dictionary<HediffDef, EyeColorSpec>();

        /// <summary>眼球装置类 hediff → 瞳色（规则色的义眼、装在头/脸上的面罩）。</summary>
        public static readonly Dictionary<HediffDef, EyeColorSpec> EyeHediffOverrides = new Dictionary<HediffDef, EyeColorSpec>();

        /// <summary>颜色是「跟随发色 / 肤色」的基因（固定色的基因走 FA 的 EyeballColorDef）。</summary>
        public static readonly Dictionary<string, EyeColorSpec> GeneOverrides = new Dictionary<string, EyeColorSpec>();

        /// <summary>种族自带的眼睛 → 瞳色。整个种族通用，FA 没有对应通道。</summary>
        public static readonly Dictionary<ThingDef, EyeColorSpec> RaceColors = new Dictionary<ThingDef, EyeColorSpec>();

        /// <summary>「颜色由本模组决定」的基因。用途：摘掉它自带的眼部叠加。</summary>
        public static readonly HashSet<string> ColoredGenes = new HashSet<string>();

        /// <summary>「颜色由本模组决定」的 hediff（含眼球装置与全身状态两档）。</summary>
        public static readonly HashSet<HediffDef> ColoredHediffs = new HashSet<HediffDef>();

        /// <summary>「颜色由本模组决定」的特性等级，键同 TraitKey（TraitDef@等级）。</summary>
        public static readonly HashSet<string> ColoredTraits = new HashSet<string>();

        /// <summary>「颜色由 FA / 其它模组已有的 EyeballColorDef 给出」的基因。</summary>
        public static readonly HashSet<string> FaColoredGenes = new HashSet<string>();

        /// <summary>「颜色由 FA / 其它模组已有的 EyeballColorDef 给出」的 hediff。</summary>
        public static readonly HashSet<string> FaColoredHediffs = new HashSet<string>();

        /// <summary>是否存在任何「只能渲染前临时上色」的来源。</summary>
        public static bool HasOverrideColors =>
            TraitColors.Count > 0 ||
            GlobalHediffColors.Count > 0 ||
            EyeHediffOverrides.Count > 0 ||
            GeneOverrides.Count > 0 ||
            RaceColors.Count > 0;

        /// <summary>特性查表键：同一条 TraitDef 的不同等级分开存。</summary>
        public static string TraitKey(string traitDefName, int degree)
        {
            return (traitDefName ?? string.Empty) + "@" + degree;
        }

        public static bool IsKnownEyeGene(string geneDefName)
        {
            return !string.IsNullOrEmpty(geneDefName) && GeneKeys.Contains(geneDefName);
        }

        public static bool IsKnownEyeHediff(HediffDef hediffDef)
        {
            return hediffDef != null && HediffKeys.Contains(hediffDef.defName);
        }

        /// <summary>
        /// 这颗节点的颜色是否已经由 FA 负责（无论定义是本模组写的还是别人写的）。
        ///
        /// 只有在「会有人给 FA 的眼珠上色」时才允许摘掉自带的眼部叠加：
        /// 摘了却没人补颜色，会画出一张没有眼睛的脸。
        /// </summary>
        public static bool IsColorProvidedByFa(Gene gene, Hediff hediff, Trait trait)
        {
            if (gene?.def != null)
            {
                string name = gene.def.defName;
                if (ColoredGenes.Contains(name) || FaColoredGenes.Contains(name))
                {
                    return true;
                }
            }
            if (hediff?.def != null)
            {
                if (ColoredHediffs.Contains(hediff.def) || FaColoredHediffs.Contains(hediff.def.defName))
                {
                    return true;
                }
            }
            if (trait?.def != null && ColoredTraits.Contains(TraitKey(trait.def.defName, trait.Degree)))
            {
                return true;
            }
            return false;
        }

        /// <summary>颜色是不是本模组给的（用于日志区分「我们接管」与「让位给 FA」）。</summary>
        public static bool IsOwnedByThisMod(PawnRenderNode node)
        {
            if (node?.gene?.def != null && ColoredGenes.Contains(node.gene.def.defName))
            {
                return true;
            }
            if (node?.hediff?.def != null && ColoredHediffs.Contains(node.hediff.def))
            {
                return true;
            }
            return node?.trait?.def != null && ColoredTraits.Contains(TraitKey(node.trait.def.defName, node.trait.Degree));
        }

        /// <summary>已接管外观（自带叠加会被摘掉）的来源总数，供启动日志与调试。</summary>
        public static int ColoredSourceCount =>
            ColoredGenes.Count + ColoredHediffs.Count + ColoredTraits.Count;

        /// <summary>由 FA / 别人的定义负责颜色的来源总数。</summary>
        public static int FaColoredSourceCount => FaColoredGenes.Count + FaColoredHediffs.Count;

        public static bool TryGetEffectiveOverride(Pawn pawn, out Color color, out string origin)
        {
            return TryResolveOverride(pawn, out color, out origin, true);
        }

        /// <summary>
        /// 只要颜色、不要来源说明的快路径。
        ///
        /// 为什么单独开一个：CheckLoadTexturesPatch 会在每次「FA 判断要不要重载贴图」时调用，
        /// 频率远高于真正重载贴图。那边只需要知道颜色变没变，不拼字符串就不会在热路径上产生垃圾。
        /// </summary>
        public static bool TryResolveOverrideColor(Pawn pawn, out Color color)
        {
            return TryResolveOverride(pawn, out color, out _, false);
        }

        /// <summary>
        /// 取 pawn 身上该用的临时瞳色。优先级（命中即停）：
        /// 1. 眼球装置 hediff（义眼、机械面罩）—— 最具体；
        /// 2. 特性 —— 角色自身固定外观；
        /// 3. 基因；
        /// 4. 全身状态 hediff；
        /// 5. 种族 —— 全族默认值，最低。
        /// </summary>
        private static bool TryResolveOverride(Pawn pawn, out Color color, out string origin, bool withOrigin)
        {
            color = Color.white;
            origin = null;
            if (!HasOverrideColors || pawn == null)
            {
                return false;
            }

            if (TryResolveHediffTable(pawn, EyeHediffOverrides, "眼球 hediff", out color, out origin, withOrigin) ||
                TryResolveTrait(pawn, out color, out origin, withOrigin) ||
                TryResolveGene(pawn, out color, out origin, withOrigin) ||
                TryResolveHediffTable(pawn, GlobalHediffColors, "全身状态", out color, out origin, withOrigin) ||
                TryResolveRace(pawn, out color, out origin, withOrigin))
            {
                return true;
            }
            return false;
        }

        private static bool TryResolveTrait(Pawn pawn, out Color color, out string origin, bool withOrigin)
        {
            color = Color.white;
            origin = null;
            List<Trait> traits = pawn.story?.traits?.allTraits;
            if (TraitColors.Count == 0 || traits == null)
            {
                return false;
            }
            for (int i = 0; i < traits.Count; i++)
            {
                Trait trait = traits[i];
                // Suppressed 的特性不参与渲染（见 DynamicPawnRenderNodeSetup_Traits），也不该参与上色。
                if (trait?.def == null || trait.Suppressed)
                {
                    continue;
                }
                if (TraitColors.TryGetValue(TraitKey(trait.def.defName, trait.Degree), out EyeColorSpec spec) &&
                    spec.TryResolve(pawn, out color))
                {
                    if (withOrigin)
                    {
                        origin = "特性 " + trait.def.defName + "@" + trait.Degree + " " + spec.Describe();
                    }
                    return true;
                }
            }
            return false;
        }

        private static bool TryResolveGene(Pawn pawn, out Color color, out string origin, bool withOrigin)
        {
            color = Color.white;
            origin = null;
            if (GeneOverrides.Count == 0 || !ModsConfig.BiotechActive || pawn.genes == null)
            {
                return false;
            }
            List<Gene> genes = pawn.genes.GenesListForReading;
            for (int i = 0; i < genes.Count; i++)
            {
                Gene gene = genes[i];
                if (gene?.def == null || !gene.Active)
                {
                    continue;
                }
                if (GeneOverrides.TryGetValue(gene.def.defName, out EyeColorSpec spec) && spec.TryResolve(pawn, out color))
                {
                    if (withOrigin)
                    {
                        origin = "基因 " + gene.def.defName + " " + spec.Describe();
                    }
                    return true;
                }
            }
            return false;
        }

        private static bool TryResolveHediffTable(Pawn pawn, Dictionary<HediffDef, EyeColorSpec> table, string channel, out Color color, out string origin, bool withOrigin)
        {
            color = Color.white;
            origin = null;
            if (table.Count == 0 || pawn?.health?.hediffSet == null)
            {
                return false;
            }
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                HediffDef def = hediffs[i]?.def;
                if (def == null)
                {
                    continue;
                }
                if (table.TryGetValue(def, out EyeColorSpec spec) && spec.TryResolve(pawn, out color))
                {
                    if (withOrigin)
                    {
                        origin = channel + " " + def.defName + " " + spec.Describe();
                    }
                    return true;
                }
            }
            return false;
        }

        private static bool TryResolveRace(Pawn pawn, out Color color, out string origin, bool withOrigin)
        {
            color = Color.white;
            origin = null;
            if (RaceColors.Count == 0 || pawn.def == null)
            {
                return false;
            }
            if (RaceColors.TryGetValue(pawn.def, out EyeColorSpec spec) && spec.TryResolve(pawn, out color))
            {
                if (withOrigin)
                {
                    origin = "种族 " + pawn.def.defName + " " + spec.Describe();
                }
                return true;
            }
            return false;
        }

        public static void Reset()
        {
            Initialized = false;
            GeneKeys.Clear();
            HediffKeys.Clear();
            TraitColors.Clear();
            GlobalHediffColors.Clear();
            EyeHediffOverrides.Clear();
            GeneOverrides.Clear();
            RaceColors.Clear();
            ColoredGenes.Clear();
            ColoredHediffs.Clear();
            ColoredTraits.Clear();
            FaColoredGenes.Clear();
            FaColoredHediffs.Clear();
        }
    }
}
