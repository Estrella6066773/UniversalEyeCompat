using System.Collections.Generic;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 启动期唯一一次扫描与注册。
    ///
    /// 时机：StaticConstructorOnStartup，此时 Def 数据库（含 FA 自己的 EyeballColorDef）已经全部就绪，
    /// 而任何 pawn 都还没开始渲染，因此这里的写入不会撞上运行期的读取。
    ///
    /// 流程：
    /// 1. 扫描 Def 数据库，得到全部「眼部外观来源」（EyeSourceDiscovery）；
    /// 2. 把基因 / hediff 的键登记进 EyeColorRegistry，供运行期刷新补丁使用；
    /// 3. 按来源种类与颜色是否固定分流（EyeColorDefInjector）。
    ///
    /// 注意事项：
    /// - 全程只读扫描 + 一次性 Add，不修改任何已有 Def。
    /// - FA 不在场（未安装或类型改名）时直接返回，只留一条日志，绝不抛异常。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class EyeColorStartup
    {
        static EyeColorStartup()
        {
            Run();
        }

        public static void Run()
        {
            if (!FacialAnimationBridge.Available)
            {
                EyeColorPatchMod.Warn("没有找到 [NL] Facial Animation 的 EyeballColorDef / EyeballControllerComp，本模组停用。");
                return;
            }

            EyeColorRegistry.Reset();

            try
            {
                List<EyeSource> sources = EyeSourceDiscovery.Discover(out int unsupportedTypes, out List<string> notes);
                foreach (EyeSource source in sources)
                {
                    switch (source.Kind)
                    {
                        case EyeSourceKind.Gene:
                            EyeColorRegistry.GeneKeys.Add(source.Key);
                            break;
                        case EyeSourceKind.Hediff:
                            EyeColorRegistry.HediffKeys.Add(source.Key);
                            break;
                    }
                }

                EyeColorInjectResult result = EyeColorDefInjector.Inject(sources);
                EyeColorRegistry.Initialized = true;

                EyeColorPatchMod.Info("眼部来源扫描完成：" + sources.Count + " 条。"
                    + "交给 FA 着色 " + result.InjectedToFa
                    + "（基因 " + EyeColorRegistry.GeneKeys.Count
                    + " / 眼球 hediff " + EyeColorRegistry.HediffKeys.Count + " 条已登记刷新）"
                    + "；本模组临时上色 " + CountOverrides(result)
                    + "（特性 " + result.TraitRegistered
                    + "，规则色 " + result.DynamicRegistered
                    + "，装置 " + result.DeviceRegistered
                    + "，全身状态 " + result.GlobalRegistered
                    + "，种族 " + result.RaceRegistered + "）"
                    + "；让位给专用定义 " + result.SkippedExisting
                    + "，无法取色 " + result.Unsolved
                    + "，启发式识别 " + result.HeuristicDetected
                    + (result.ElfDetected > 0 ? "，精灵来源 " + result.ElfDetected : string.Empty)
                    + "。");

                if (unsupportedTypes > 0)
                {
                    EyeColorPatchMod.Info("另有 " + unsupportedTypes + " 种带眼部节点、但无法按角色匹配的 Def 类型未纳入。");
                }

                // 「模组自己的眼睛贴图会被收起来」的凭据：
                // 这张表 = 本模组给颜色的 + FA/别人已经给颜色的，两类都会在渲染期摘掉自带叠加。
                if (EyeColorRegistry.ColoredSourceCount > 0 || EyeColorRegistry.FaColoredSourceCount > 0)
                {
                    EyeColorPatchMod.Info("已接管外观 " + EyeColorRegistry.ColoredSourceCount + " 条来源"
                        + "（基因 " + EyeColorRegistry.ColoredGenes.Count
                        + " / hediff " + EyeColorRegistry.ColoredHediffs.Count
                        + " / 特性 " + EyeColorRegistry.ColoredTraits.Count + "），"
                        + "另有 " + EyeColorRegistry.FaColoredSourceCount + " 条由 FA/其它模组的瞳色定义负责"
                        + "（基因 " + EyeColorRegistry.FaColoredGenes.Count
                        + " / hediff " + EyeColorRegistry.FaColoredHediffs.Count + "）："
                        + "这些来源自带的眼部叠加都会被摘掉，前提是 FA 确实在画这张脸。");
                }

                if (Prefs.DevMode)
                {
                    for (int i = 0; i < result.Detail.Count; i++)
                    {
                        EyeColorPatchMod.Info(result.Detail[i]);
                    }
                    for (int i = 0; i < notes.Count; i++)
                    {
                        EyeColorPatchMod.Info("[提示] " + notes[i]);
                    }
                }
            }
            catch (System.Exception e)
            {
                EyeColorPatchMod.Error("扫描失败，本模组停用：" + e);
            }
        }

        private static int CountOverrides(EyeColorInjectResult result)
        {
            return result.TraitRegistered + result.DynamicRegistered + result.DeviceRegistered + result.GlobalRegistered + result.RaceRegistered;
        }
    }
}
