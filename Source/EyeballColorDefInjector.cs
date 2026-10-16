using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace UniversalEyeCompat
{
    /// <summary>一次扫描的登记结果，供启动日志汇总。</summary>
    public sealed class EyeCompatInjectResult
    {
        /// <summary>写进 FA 的 EyeballColorDef 表（颜色固定，FA 原流程会自己用）。</summary>
        public int InjectedToFA;

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

        /// <summary>按名称/配方识别出来的启发式来源数，方便核查误判。</summary>
        public int HeuristicDetected;

        public readonly List<string> Detail = new List<string>();
    }

    /// <summary>
    /// 把扫描结果分流写进 FA 的定义表或本模组的运行期临时上色表。
    ///
    /// 分流规则（按顺序判断）：
    /// 1. 特性 → TraitColors（FA 没有特性通道）；
    /// 2. 种族 → RaceColors（FA 没有种族通道），且只接受来源自己声明的颜色（见 EyeColorResolver）；
    /// 3. 不挂眼球的状态 → GlobalHediffColors（FA 的 hediff 分支要求 hediff.Part == 眼球，匹配不到）；
    /// 4. 基因 / 挂眼球的 hediff：
    ///    - 别人已经写过同一键的 EyeballColorDef → 让位（本模组存在的意义就是兜底）；
    ///    - 颜色固定 → 注册一条 EyeballColorDef，交给 FA 原流程（对任何角色自动生效，最省事也最稳）；
    ///    - 颜色是「跟随发色/肤色」的规则 → FA 的静态定义存不下，改登记 GeneOverrides / EyeHediffOverrides。
    ///
    /// 退让规则：只要表里已经有指向同一个 geneDef 或 hediffDef 的条目 —— 无论是 FA 本体、
    /// Experimentals、还是任何重绘包写的 —— 就直接跳过，绝不覆盖别人的颜色。
    ///
    /// 顺带记账：凡是「颜色确实由本模组给出」的来源都会登记进 EyeCompatRegistry.Colored*，
    /// 渲染期据此把它们自带的眼部叠加摘掉（见 EyeOverlaySuppressPatch）—— 只改颜色不摘叠加，
    /// 模组自己的眼睛贴图会盖在 FA 的眼球上，看起来就是瞳色没被替换。
    ///
    /// 注意事项：
    /// - 只能在 Def 数据库加载完成后调用一次，运行期不要再 Add，否则会触发重复 defName 报错。
    /// </summary>
    public static class EyeballColorDefInjector
    {
        public static EyeCompatInjectResult Inject(List<EyeSource> sources)
        {
            EyeCompatInjectResult result = new EyeCompatInjectResult();

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
                bool alreadyHandled = source.Kind == EyeSourceKind.Gene
                    ? existingGenes.Contains(source.Key)
                    : existingHediffs.Contains(source.Key);
                if (alreadyHandled)
                {
                    result.SkippedExisting++;
                    result.Detail.Add("[让位] " + KindName(source) + " " + source.Key + " —— 已有专用瞳色定义");
                    continue;
                }

                if (!EyeColorResolver.TryResolve(source, true, out EyeColorSpec spec, out string origin))
                {
                    result.Unsolved++;
                    result.Detail.Add("[无法取色] " + KindName(source) + " " + source.Key + "（" + source.Label + "）" + Describe(source));
                    continue;
                }

                // 能走 FA 原生通道的条件有两条，缺一不可：
                // 1. 这条来源装得上/挂得上（hediff 必须确定在眼球部件上，否则 FA 匹配不到）；
                // 2. 颜色是固定值（EyeballColorDef 只有固定色字段，存不下「跟随发色/肤色」）。
                bool faChannel = spec.Mode == EyeColorMode.Fixed
                    && (source.Kind == EyeSourceKind.Gene || source.AppliesToEyePart);
                if (!faChannel)
                {
                    // 改走运行期临时上色。这条路不看部位，也不要求固定色。
                    if (source.Kind == EyeSourceKind.Gene)
                    {
                        EyeCompatRegistry.GeneOverrides[source.Key] = spec;
                        EyeCompatRegistry.ColoredGenes.Add(source.Key);
                        result.DynamicRegistered++;
                        result.Detail.Add("[已登记·规则色] 基因 " + source.Key + " → " + spec.Describe()
                            + "（识别：" + source.DetectReason + "）");
                    }
                    else
                    {
                        HediffDef hediffTarget = (HediffDef)source.SourceDef;
                        EyeCompatRegistry.ColoredHediffs.Add(hediffTarget);
                        if (source.IsImplant)
                        {
                            // 装置类（义眼、面罩）比疾病/状态类更具体，放更高优先级的档位。
                            EyeCompatRegistry.EyeHediffOverrides[hediffTarget] = spec;
                            result.DeviceRegistered++;
                            result.Detail.Add("[已登记·装置] hediff " + source.Key + " → " + spec.Describe()
                                + "（来源：" + origin + "，识别：" + source.DetectReason + "）");
                        }
                        else
                        {
                            EyeCompatRegistry.GlobalHediffColors[hediffTarget] = spec;
                            result.GlobalRegistered++;
                            result.Detail.Add("[已登记·全身状态] hediff " + source.Key + " → " + spec.Describe()
                                + "（来源：" + origin + "，识别：" + source.DetectReason + "）");
                        }
                    }
                    continue;
                }

                // 固定色：统一过一次 TryResolve，把 colorRGBPostFactor 乘好再写进定义。
                if (!spec.TryResolve(null, out Color fixedColor))
                {
                    result.Unsolved++;
                    continue;
                }

                string defName = "UEC_" + source.Kind + "_" + source.Key;
                HediffDef hediffDef = source.Kind == EyeSourceKind.Hediff ? source.SourceDef as HediffDef : null;
                string geneName = source.Kind == EyeSourceKind.Gene ? source.Key : null;
                if (FacialAnimationBridge.TryAddEyeballColorDef(defName, "万能眼部兼容：" + source.Label, geneName, hediffDef, fixedColor))
                {
                    if (geneName != null)
                    {
                        EyeCompatRegistry.ColoredGenes.Add(geneName);
                    }
                    else if (hediffDef != null)
                    {
                        EyeCompatRegistry.ColoredHediffs.Add(hediffDef);
                    }
                    result.InjectedToFA++;
                    result.Detail.Add("[已注册·FA] " + KindName(source) + " " + source.Key + " → " + ColorText(fixedColor)
                        + "（来源：" + origin + "，识别：" + source.DetectReason + "）");
                }
                else
                {
                    result.Unsolved++;
                }
            }

            return result;
        }

        private static void RegisterTraitOverride(EyeSource source, EyeCompatInjectResult result)
        {
            if (!EyeColorResolver.TryResolve(source, true, out EyeColorSpec spec, out string origin))
            {
                result.Unsolved++;
                result.Detail.Add("[无法取色] 特性 " + source.Key + "（" + source.Label + "）" + Describe(source));
                return;
            }
            EyeCompatRegistry.TraitColors[source.Key] = spec;
            EyeCompatRegistry.ColoredTraits.Add(source.Key);
            result.TraitRegistered++;
            result.Detail.Add("[已登记·特性] " + source.Key + " → " + spec.Describe()
                + "（来源：" + origin + "，识别：" + source.DetectReason + "）");
        }

        private static void RegisterRaceOverride(EyeSource source, EyeCompatInjectResult result)
        {
            // 种族来源禁止猜色，理由见 EyeColorResolver.TryResolve 的 allowGuessing 注释。
            if (!EyeColorResolver.TryResolve(source, false, out EyeColorSpec spec, out string origin))
            {
                // 没声明颜色的种族在前面已被记录过，这里不重复计为失败。
                return;
            }
            EyeCompatRegistry.RaceColors[(ThingDef)source.SourceDef] = spec;
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

        public static string ColorText(Color color)
        {
            return "RGB(" + color.r.ToString("0.000") + "," + color.g.ToString("0.000") + "," + color.b.ToString("0.000") + ")";
        }
    }
}
