using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace UniversalEyeCompat
{
    /// <summary>
    /// 本模组的共享状态。启动期扫描一次写入，运行期只读。
    ///
    /// 生命周期：
    /// EyeCompatStartup（StaticConstructorOnStartup，Def 数据库就绪后）填充本类型；
    /// 之后 EyeLoadTexturesPatches 每次重载瞳孔贴图时查这里的「临时上色」表，
    /// EyeCheckLoadTexturesPatches 靠它判断颜色变没变，EyeRefreshPatches 在 hediff / 基因 / 特性变化时触发刷新。
    ///
    /// 表分三类，别混：
    /// 1. GeneKeys / HediffKeys —— 只是「这条来源与眼睛有关」的索引，供刷新补丁做廉价判断；
    ///    颜色不在这里，基因与「挂在眼球上的 hediff」的颜色交给 FA 的 EyeballColorDef（见 EyeballColorDefInjector）。
    /// 2. 下面五张颜色表 —— 都是「FA 表达不了、只能渲染前临时上色」的来源：
    ///    颜色可能是固定值，也可能是「跟随发色 / 肤色」的规则（EyeColorSpec）。
    /// 3. ColoredGenes / ColoredHediffs / ColoredTraits —— 「颜色确实由我们决定」的来源，
    ///    渲染期用它判断要不要连这些来源自带的眼部叠加一起摘掉（见 EyeOverlaySuppressPatch）。
    ///
    /// 注意：Initialized 为 false 时所有补丁都必须直接放行，避免在 FA 缺席或扫描失败时干扰游戏。
    /// </summary>
    public static class EyeCompatRegistry
    {
        public static bool Initialized;

        /// <summary>已识别为「眼部外观来源」的基因 defName（含只交给 FA 处理的那批）。</summary>
        public static readonly HashSet<string> GeneKeys = new HashSet<string>();

        /// <summary>已识别为「眼部外观来源」的 hediff defName（含只交给 FA 处理的那批）。</summary>
        public static readonly HashSet<string> HediffKeys = new HashSet<string>();

        /// <summary>
        /// 特性（定义 + 等级，见 TraitKey）→ 瞳色。特性没有 FA 通道，只能在渲染前临时上色。
        ///
        /// 用「字符串键」而不是 TraitDef：TraitDef 只能定位到特性本身，
        /// 但带眼睛节点的其实是某个等级，两者混用会出现「等级 0 也跟着变色」的错误。
        /// </summary>
        public static readonly Dictionary<string, EyeColorSpec> TraitColors = new Dictionary<string, EyeColorSpec>();

        /// <summary>
        /// 不挂在眼球部件上的 hediff → 瞳色。异象的「虚空之触」这类状态，
        /// FA 的 hediff 通道（要求 hediff.Part == 眼球）永远匹配不到，只能走渲染前临时上色。
        /// </summary>
        public static readonly Dictionary<HediffDef, EyeColorSpec> GlobalHediffColors = new Dictionary<HediffDef, EyeColorSpec>();

        /// <summary>
        /// 眼球装置类 hediff → 瞳色，两个来源会落到这里：
        /// 1. 颜色是「跟随发色 / 肤色」的义眼 —— FA 的 EyeballColorDef 只存固定色，存不下规则色；
        /// 2. 配方显示装在非眼球部位、却自带眼部叠加的装置（典型：装在头/脸上的机械面罩）——
        ///    FA 的 hediff 通道要求 hediff.Part 正好是眼球部件，这类来源注册了也匹配不到。
        /// 两条都靠渲染前临时上色，因为那条路只看「pawn 身上有没有这条 hediff」，不看部位。
        /// </summary>
        public static readonly Dictionary<HediffDef, EyeColorSpec> EyeHediffOverrides = new Dictionary<HediffDef, EyeColorSpec>();

        /// <summary>颜色是「跟随发色 / 肤色」的基因（固定色的基因走 FA 的 EyeballColorDef）。</summary>
        public static readonly Dictionary<string, EyeColorSpec> GeneOverrides = new Dictionary<string, EyeColorSpec>();

        /// <summary>种族自带的眼睛 → 瞳色。整个种族通用，FA 没有对应通道。</summary>
        public static readonly Dictionary<ThingDef, EyeColorSpec> RaceColors = new Dictionary<ThingDef, EyeColorSpec>();

        /// <summary>
        /// 「颜色确实由本模组决定」的基因。与 GeneKeys 的区别很关键：
        /// GeneKeys 只是「这条基因与眼睛有关」的索引（包含让位给 FA / 别人、以及我们取不到色的），
        /// 这里只收我们已经给出颜色的 —— 因为它的用途是「摘掉这条来源自带的眼部叠加」，
        /// 对没上色的来源摘掉叠加会让这颗眼睛彻底消失。
        /// </summary>
        public static readonly HashSet<string> ColoredGenes = new HashSet<string>();

        /// <summary>「颜色确实由本模组决定」的 hediff（含眼球装置与全身状态两档），用途同 ColoredGenes。</summary>
        public static readonly HashSet<HediffDef> ColoredHediffs = new HashSet<HediffDef>();

        /// <summary>「颜色确实由本模组决定」的特性等级，键同 TraitKey（TraitDef@等级）。</summary>
        public static readonly HashSet<string> ColoredTraits = new HashSet<string>();

        /// <summary>
        /// 是否存在任何「只能渲染前临时上色」的来源。
        /// 为 false 时相关补丁可以完全空转，不必每次都去翻 pawn 的特性表与 hediff 表。
        /// </summary>
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
        /// 这个节点挂着的来源，颜色是否已经由本模组接管。
        ///
        /// 只有「是」的时候才允许摘掉它自带的眼部叠加（见 EyeOverlaySuppressPatch）：
        /// 接管了颜色才补得上被摘掉的外观，否则会画出一只没有眼睛的脸。
        /// </summary>
        public static bool IsColoredEyeSource(Gene gene, Hediff hediff, Trait trait)
        {
            if (gene?.def != null && ColoredGenes.Contains(gene.def.defName))
            {
                return true;
            }
            if (hediff?.def != null && ColoredHediffs.Contains(hediff.def))
            {
                return true;
            }
            if (trait?.def != null && ColoredTraits.Contains(TraitKey(trait.def.defName, trait.Degree)))
            {
                return true;
            }
            return false;
        }

        /// <summary>已接管外观（自带叠加会被摘掉）的来源总数，供启动日志与调试。</summary>
        public static int ColoredSourceCount => ColoredGenes.Count + ColoredHediffs.Count + ColoredTraits.Count;

        /// <summary>
        /// 取 pawn 身上该用的临时瞳色。优先级（命中即停）：
        ///
        /// 1. 眼球装置 hediff（义眼、机械面罩）—— 直接替换/遮住眼球，是最具体、最该生效的来源
        ///    （与 FA 自己「先读基因再读 hediff」的顺序一致，hediff 压过基因）；
        /// 2. 特性 —— 角色自身固定外观，比基因更「像这个人」；
        /// 3. 基因 —— 会被特性 / 其它基因压制，且 FA 自己也要读它；
        /// 4. 全身状态 hediff —— 随战斗疾病反复增减，压过它反而容易让人困惑；
        /// 5. 种族 —— 全族默认值，最低，用来给上面都没声明的种族兜底。
        ///
        /// 5 条同时命中的情况极少（同一只眼睛又是义眼又是种族默认色之类），
        /// 因此这里只定一个明确、写在注释里的顺序，不做花样；开发者模式下会有一次性提示。
        /// </summary>
        public static bool TryGetEffectiveOverride(Pawn pawn, out Color color, out string origin)
        {
            return TryResolveOverride(pawn, out color, out origin, true);
        }

        /// <summary>
        /// 只要颜色、不要来源说明的快路径。
        ///
        /// 为什么单独开一个：EyeCheckLoadTexturesPatches 会在每次「FA 判断要不要重载贴图」时调用，
        /// 频率远高于真正重载贴图（每 pawn 每次渲染更新都会问一次）。那边只需要知道颜色变没变，
        /// 不拼字符串就不会在热路径上产生垃圾。
        /// </summary>
        public static bool TryResolveOverrideColor(Pawn pawn, out Color color)
        {
            return TryResolveOverride(pawn, out color, out _, false);
        }

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
        }
    }
}
