using System.Collections.Generic;
using Verse;

namespace UniversalEyeCompat
{
    /// <summary>
    /// 启动期唯一一次扫描与注册。
    ///
    /// 时机：StaticConstructorOnStartup，此时 Def 数据库（含 FA 自己的 EyeballColorDef）已经全部就绪，
    /// 而任何 pawn 都还没开始渲染，因此这里的写入不会撞上运行期的读取。
    ///
    /// 流程：
    /// 1. 扫描 Def 数据库，得到全部「眼部外观来源」（EyeEffectDiscovery）；
    /// 2. 把基因 / hediff 的键登记进 EyeCompatRegistry，供运行期刷新补丁使用 —— 即使它们已有专用颜色定义也要登记，
    ///    否则「已由别人管颜色」的义眼在安装后依然不会刷新；
    /// 3. 按来源种类与颜色是否固定分流（EyeballColorDefInjector）：固定色进 FA 的定义表，
    ///    规则色（跟随发色/肤色）、特性、种族、全身状态进本模组的临时上色表。
    ///
    /// 注意事项：
    /// - 全程只读扫描 + 一次性 Add，不修改任何已有 Def。
    /// - FA 不在场（未安装或类型改名）时直接返回，只留一条日志，绝不抛异常。
    /// - 日志按通道分开计数：覆盖率是本模组的核心指标，必须能一眼看出
    ///   「哪些来源交给了 FA」「哪些是本模组自己上的色」「哪些取不到色」。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class EyeCompatStartup
    {
        static EyeCompatStartup()
        {
            Run();
        }

        public static void Run()
        {
            if (!FacialAnimationBridge.Available)
            {
                Log.Warning("[万能眼部兼容] 没有找到 [NL] Facial Animation 的 EyeballColorDef / EyeballControllerComp，本模组停用。");
                return;
            }

            EyeCompatRegistry.Reset();

            try
            {
                List<EyeSource> sources = EyeEffectDiscovery.Discover(out int unsupportedTypes, out List<string> notes);
                foreach (EyeSource source in sources)
                {
                    switch (source.Kind)
                    {
                        case EyeSourceKind.Gene:
                            EyeCompatRegistry.GeneKeys.Add(source.Key);
                            break;
                        case EyeSourceKind.Hediff:
                            EyeCompatRegistry.HediffKeys.Add(source.Key);
                            break;
                    }
                }

                EyeCompatInjectResult result = EyeballColorDefInjector.Inject(sources);
                EyeCompatRegistry.Initialized = true;

                Log.Message("[万能眼部兼容] 眼部来源扫描完成：" + sources.Count + " 条。"
                    + "交给 FA 着色 " + result.InjectedToFA
                    + "（基因 " + EyeCompatRegistry.GeneKeys.Count
                    + " / 眼球 hediff " + EyeCompatRegistry.HediffKeys.Count + " 条已登记刷新）"
                    + "；本模组临时上色 " + CountOverrides(result)
                    + "（特性 " + result.TraitRegistered
                    + "，规则色 " + result.DynamicRegistered
                    + "，装置 " + result.DeviceRegistered
                    + "，全身状态 " + result.GlobalRegistered
                    + "，种族 " + result.RaceRegistered + "）"
                    + "；让位给专用定义 " + result.SkippedExisting
                    + "，无法取色 " + result.Unsolved
                    + "，启发式识别 " + result.HeuristicDetected + "。");

                if (unsupportedTypes > 0)
                {
                    Log.Message("[万能眼部兼容] 另有 " + unsupportedTypes + " 种带眼部节点、但无法按角色匹配的 Def 类型未纳入。");
                }

                // 这一行是「模组自己的眼睛贴图有没有被收起来」的唯一凭据：
                // 数量对得上，说明这些来源会走 EyeOverlaySuppressPatch 摘掉自带叠加，只留 FA 的眼球。
                if (EyeCompatRegistry.ColoredSourceCount > 0)
                {
                    Log.Message("[万能眼部兼容] 已接管外观 " + EyeCompatRegistry.ColoredSourceCount + " 条来源"
                        + "（基因 " + EyeCompatRegistry.ColoredGenes.Count
                        + " / hediff " + EyeCompatRegistry.ColoredHediffs.Count
                        + " / 特性 " + EyeCompatRegistry.ColoredTraits.Count
                        + "）：这些来源自带的眼部叠加会被摘掉，改由 FA 的眼球上色。");
                }

                if (Prefs.DevMode)
                {
                    for (int i = 0; i < result.Detail.Count; i++)
                    {
                        Log.Message("[万能眼部兼容] " + result.Detail[i]);
                    }
                    for (int i = 0; i < notes.Count; i++)
                    {
                        Log.Message("[万能眼部兼容·提示] " + notes[i]);
                    }
                }
            }
            catch (System.Exception e)
            {
                Log.Error("[万能眼部兼容] 扫描失败，本模组停用：" + e);
            }
        }

        private static int CountOverrides(EyeCompatInjectResult result)
        {
            return result.TraitRegistered + result.DynamicRegistered + result.DeviceRegistered + result.GlobalRegistered + result.RaceRegistered;
        }
    }
}
