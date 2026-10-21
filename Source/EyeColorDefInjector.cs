using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>一次扫描的登记结果，供启动日志汇总。</summary>
    public sealed class EyeColorInjectResult
    {
        /// <summary>写进 FA 的 EyeballColorDef 表（颜色固定，FA 原流程会自己用）。</summary>
        public int InjectedToFa;

        /// <summary>颜色是规则（跟随发色/肤色），留在本模组做渲染前临时上色。</summary>
        public int DynamicRegistered;

        /// <summary>不挂眼球的状态，登记进临时上色表。</summary>
        public int GlobalRegistered;

        /// <summary>义眼、面罩这类装置类 hediff，部位未证明或颜色是规则色，登记进临时上色表。</summary>
        public int DeviceRegistered;

        /// <summary>特性，登记进临时上色表。</summary>
        public int TraitRegistered;

        /// <summary>种族自带眼睛，登记进临时上色表。</summary>
        public int RaceRegistered;

        /// <summary>FA 或其它模组已经写过瞳色，本模组让位。</summary>
        public int SkippedExisting;

        /// <summary>取不到颜色，放弃这条来源。</summary>
        public int Unsolved;

        /// <summary>按名称/配方识别出来的启发式来源数。</summary>
        public int HeuristicDetected;

        /// <summary>精灵（Faction - Elves）来源数。</summary>
        public int ElfDetected;

        public readonly List<string> Detail = new List<string>();
    }

    /// <summary>
    /// 把扫描结果分流写进 FA 的定义表或本模组的运行期临时上色表。
    ///
    /// 分流规则（按顺序判断）：
    /// 1. 特性 → TraitColors（FA 没有特性通道）；
    /// 2. 种族 → RaceColors，且只接受来源自己声明的颜色；
    /// 3. 不挂眼球的状态 → GlobalHediffColors；
    /// 4. 基因 / 挂眼球的 hediff：
    ///    - 别人已经写过同一键的 EyeballColorDef → 让位，但记进 FaColored*（渲染期照样摘叠加）；
    ///    - 颜色固定 → 注册一条 EyeballColorDef，交给 FA 原流程；
    ///    - 颜色是「跟随发色/肤色」的规则 → 登记 GeneOverrides / EyeHediffOverrides。
    ///
    /// 退让规则：只要表里已经有指向同一个 geneDef 或 hediffDef 的条目 —— 无论是 FA 本体、
    /// Experimentals、还是任何重绘包写的 —— 就直接跳过，绝不覆盖别人的颜色。
    /// </summary>
    public static class EyeColorDefInjector
    {
        public static EyeColorInjectResult Inject(List<EyeSource> sources)
        {
            EyeColorInjectResult result = new EyeColorInjectResult();

            HashSet<string> existingGenes = new HashSet<string>();
            HashSet<string> existingHediffs = new HashSet<string>();
            foreach (EyeballColorEntry entry in FacialAnimationBridge.ReadExistingEntries())
            {
                if (!string.IsNullOrEmpty(entry.GeneDefName))
                {
                    existingGenes.Add(entry.GeneDefName);
                }
                if (entry.HediffDef != null)
                {
                    existingHediffs.Add(entry.HediffDef.defName);
                }
            }

            for (int i = 0; i < sources.Count; i++)
            {
                EyeSource source = sources[i];
                if (source.Heuristic)
                {
                    result.HeuristicDetected++;
                }
                if (source.Elf)
                {
                    result.ElfDetected++;
                }

                switch (source.Kind)
                {
                    case EyeSourceKind.Trait:
                        RegisterTraitOverride(source, result);
                        continue;

                    case EyeSourceKind.Race:
                        RegisterRaceOverride(source, result);
                        continue;
                }

                // 让位：别人（FA 本体、Experimentals、重绘包）已经写过同一条来源的颜色，不覆盖。
                // 但仍然记进 FaColored*：FA 会给这颗眼珠上色，所以渲染期可以放心摘掉来源自带的叠加。
                bool alreadyHandled = source.Kind == EyeSourceKind.Gene
                    ? existingGenes.Contains(source.Key)
                    : existingHediffs.Contains(source.Key);
                if (alreadyHandled)
                {
                    if (source.Kind == EyeSourceKind.Gene)
                    {
                        EyeColorRegistry.FaColoredGenes.Add(source.Key);
                    }
                    else
                    {
                        EyeColorRegistry.FaColoredHediffs.Add(source.Key);
                    }
                    result.SkippedExisting++;
                    result.Detail.Add("[让位] " + KindName(source) + " " + source.Key
                        + " —— 已有专用瞳色定义（叠加仍会摘掉，颜色由它负责）");
                    continue;
                }

                if (!EyeColorResolver.TryResolve(source, true, out EyeColorSpec spec, out string origin))
                {
                    result.Unsolved++;
                    result.Detail.Add("[无法取色] " + KindName(source) + " " + source.Key + "（" + source.Label + "）" + Describe(source));
                    continue;
                }

                // 能走 FA 原生通道的条件有两条，缺一不可：
                // 1. hediff 必须确定在眼球部件上；
                // 2. 颜色是固定值（EyeballColorDef 只有固定色字段）。
                bool faChannel = spec.Mode == EyeColorMode.Fixed
                    && (source.Kind == EyeSourceKind.Gene || source.AppliesToEyePart);
                if (!faChannel)
                {
                    RegisterRunTimeOverride(source, spec, origin, result);
                    continue;
                }

                // 固定色：统一过一次 TryResolve，把 colorRGBPostFactor 乘好再写进定义。
                if (!spec.TryResolve(null, out Color fixedColor))
                {
                    result.Unsolved++;
                    continue;
                }

                string defName = "UECP_" + source.Kind + "_" + source.Key;
                HediffDef hediffDef = source.Kind == EyeSourceKind.Hediff ? source.SourceDef as HediffDef : null;
                string geneName = source.Kind == EyeSourceKind.Gene ? source.Key : null;
                if (FacialAnimationBridge.TryAddEyeballColorDef(defName, "万用瞳色补丁：" + source.Label, geneName, hediffDef, fixedColor))
                {
                    if (geneName != null)
                    {
                        EyeColorRegistry.ColoredGenes.Add(geneName);

                        // 精灵兼容的第二道保险：除了注册 FA 定义，也登记进运行期临时上色表。
                        // 两者的颜色相同，正常情况没有区别；万一 FA 的 geneDef 通道因为版本 / 加载顺序
                        // 没能生效，LoadTexturesPatch 仍会在它读颜色之前把同一颜色写进去。
                        // 精灵是玩家最容易直接看到「颜色没变」的一类来源，值得多这一层。
                        if (source.Elf)
                        {
                            EyeColorRegistry.GeneOverrides[geneName] = spec;
                        }
                    }
                    else if (hediffDef != null)
                    {
                        EyeColorRegistry.ColoredHediffs.Add(hediffDef);
                    }
                    result.InjectedToFa++;
                    result.Detail.Add("[已注册·FA] " + KindName(source) + " " + source.Key + " → " + ColorText.Of(fixedColor)
                        + "（来源：" + origin + "，识别：" + source.DetectReason + "）" + ElfTag(source));
                }
                else
                {
                    result.Unsolved++;
                }
            }

            return result;
        }

        private static void RegisterRunTimeOverride(EyeSource source, EyeColorSpec spec, string origin, EyeColorInjectResult result)
        {
            if (source.Kind == EyeSourceKind.Gene)
            {
                EyeColorRegistry.GeneOverrides[source.Key] = spec;
                EyeColorRegistry.ColoredGenes.Add(source.Key);
                result.DynamicRegistered++;
                result.Detail.Add("[已登记·规则色] 基因 " + source.Key + " → " + spec.Describe()
                    + "（识别：" + source.DetectReason + "）" + ElfTag(source));
                return;
            }

            HediffDef hediffTarget = (HediffDef)source.SourceDef;
            EyeColorRegistry.ColoredHediffs.Add(hediffTarget);
            if (source.IsImplant)
            {
                // 装置类（义眼、面罩）比疾病/状态类更具体，放更高优先级的档位。
                EyeColorRegistry.EyeHediffOverrides[hediffTarget] = spec;
                result.DeviceRegistered++;
                result.Detail.Add("[已登记·装置] hediff " + source.Key + " → " + spec.Describe()
                    + "（来源：" + origin + "，识别：" + source.DetectReason + "）");
            }
            else
            {
                EyeColorRegistry.GlobalHediffColors[hediffTarget] = spec;
                result.GlobalRegistered++;
                result.Detail.Add("[已登记·全身状态] hediff " + source.Key + " → " + spec.Describe()
                    + "（来源：" + origin + "，识别：" + source.DetectReason + "）");
            }
        }

        private static void RegisterTraitOverride(EyeSource source, EyeColorInjectResult result)
        {
            if (!EyeColorResolver.TryResolve(source, true, out EyeColorSpec spec, out string origin))
            {
                result.Unsolved++;
                result.Detail.Add("[无法取色] 特性 " + source.Key + "（" + source.Label + "）" + Describe(source));
                return;
            }
            EyeColorRegistry.TraitColors[source.Key] = spec;
            EyeColorRegistry.ColoredTraits.Add(source.Key);
            result.TraitRegistered++;
            result.Detail.Add("[已登记·特性] " + source.Key + " → " + spec.Describe()
                + "（来源：" + origin + "，识别：" + source.DetectReason + "）");
        }

        private static void RegisterRaceOverride(EyeSource source, EyeColorInjectResult result)
        {
            // 种族来源禁止猜色，理由见 EyeColorResolver.TryResolve 的 allowGuessing 注释。
            if (!EyeColorResolver.TryResolve(source, false, out EyeColorSpec spec, out string origin))
            {
                // 没声明颜色的种族在前面已被记录过，这里不重复计为失败。
                return;
            }
            EyeColorRegistry.RaceColors[(ThingDef)source.SourceDef] = spec;
            result.RaceRegistered++;
            result.Detail.Add("[已登记·种族] " + source.Key + " → " + spec.Describe() + "（来源：" + origin + "）");
        }

        /// <summary>
        /// 取色失败时的诊断尾巴：列出这条来源采过哪些贴图、识别依据是什么。
        /// 只在开发者模式打印，用来判断「是没找到贴图，还是贴图里没有颜色词」。
        /// </summary>
        private static string Describe(EyeSource source)
        {
            if (!Prefs.DevMode)
            {
                return string.Empty;
            }
            string reason = "［识别：" + source.DetectReason + "］";
            if (source.TexturePaths.Count == 0)
            {
                return reason + "［诊断：无眼部贴图路径，只能靠定义名与显示名猜色］";
            }
            return reason + "［诊断：贴图=" + string.Join(" | ", source.TexturePaths) + "］";
        }

        private static string ElfTag(EyeSource source)
        {
            return source.Elf ? "（精灵兼容）" : string.Empty;
        }

        private static string KindName(EyeSource source)
        {
            switch (source.Kind)
            {
                case EyeSourceKind.Gene:
                    return "基因";
                case EyeSourceKind.Hediff:
                    return "hediff";
                case EyeSourceKind.Trait:
                    return "特性";
                default:
                    return "种族";
            }
        }
    }
}
