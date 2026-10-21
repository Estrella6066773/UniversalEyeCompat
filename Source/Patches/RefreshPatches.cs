using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 在「眼部相关状态变化」之后让 FA 重新给瞳孔上色。
    ///
    /// 四个落点：
    /// 1. Pawn_HealthTracker.AddHediff(Hediff, ...)：装上义眼、染上「虚空之触」等；
    /// 2. Pawn_HealthTracker.RemoveHediff(Hediff)：拆掉义眼、状态痊愈；
    /// 3. Pawn_GeneTracker.Notify_GenesChanged(GeneDef)：基因增减；
    /// 4. TraitSet.GainTrait / RemoveTrait：特性增减。
    ///
    /// 为什么必须补：
    /// FA 的 EyeballControllerComp 只在颜色字段、脸型或被显式标脏时才重载瞳孔贴图。
    /// 上面四件事都只改变 hediff / 基因 / 特性，不会动颜色字段。
    ///
    /// 注意事项：
    /// - AddHediff 有 HediffDef 与 Hediff 两个重载，后者被前者调用，所以只补 Hediff 那个，避免重复刷新。
    /// - 触发频率高，先做「是否与眼睛有关」的廉价判断，再走 EyeRefreshUtility。
    /// - 这里只做「尽快刷新」；漏掉的路径由 CheckLoadTexturesPatch 兜底。
    /// </summary>
    public static class RefreshPatches
    {
        public const string HarmonyIdPrefix = "[万用瞳色补丁] ";

        public static void Apply(Harmony harmony)
        {
            PatchAddHediff(harmony);
            PatchRemoveHediff(harmony);
            PatchGeneChanged(harmony);
            PatchTraitChanged(harmony);
        }

        private static void PatchAddHediff(Harmony harmony)
        {
            try
            {
                MethodInfo target = typeof(Pawn_HealthTracker).GetMethods(AccessTools.all)
                    .FirstOrDefault(m => m.Name == "AddHediff"
                        && m.GetParameters().Length == 4
                        && m.GetParameters()[0].ParameterType == typeof(Hediff));
                if (target == null)
                {
                    EyeColorPatchMod.Warn("找不到 Pawn_HealthTracker.AddHediff(Hediff, ...)，瞳色不会随义眼安装刷新。");
                    return;
                }
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(RefreshPatches), nameof(Postfix_AddHediff)));
            }
            catch (Exception e)
            {
                EyeColorPatchMod.Warn("补丁 AddHediff 失败：" + e.Message);
            }
        }

        private static void PatchRemoveHediff(Harmony harmony)
        {
            try
            {
                MethodInfo target = typeof(Pawn_HealthTracker).GetMethods(AccessTools.all)
                    .FirstOrDefault(m => m.Name == "RemoveHediff"
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(Hediff));
                if (target == null)
                {
                    EyeColorPatchMod.Warn("找不到 Pawn_HealthTracker.RemoveHediff(Hediff)，瞳色不会随拆下义眼刷新。");
                    return;
                }
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(RefreshPatches), nameof(Postfix_RemoveHediff)));
            }
            catch (Exception e)
            {
                EyeColorPatchMod.Warn("补丁 RemoveHediff 失败：" + e.Message);
            }
        }

        private static void PatchGeneChanged(Harmony harmony)
        {
            try
            {
                MethodInfo target = typeof(Pawn_GeneTracker).GetMethods(AccessTools.all)
                    .FirstOrDefault(m => m.Name == "Notify_GenesChanged");
                if (target == null)
                {
                    EyeColorPatchMod.Warn("找不到 Pawn_GeneTracker.Notify_GenesChanged，瞳色不会随基因变化刷新。");
                    return;
                }
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(RefreshPatches), nameof(Postfix_Notify_GenesChanged)));
            }
            catch (Exception e)
            {
                EyeColorPatchMod.Warn("补丁 Notify_GenesChanged 失败：" + e.Message);
            }
        }

        /// <summary>
        /// TraitSet.GainTrait(Trait, bool) / RemoveTrait(Trait, bool)。
        /// 用参数类型精确定位，避免将来出现同名重载时补错方法。
        /// </summary>
        private static void PatchTraitChanged(Harmony harmony)
        {
            try
            {
                MethodInfo gain = typeof(TraitSet).GetMethod("GainTrait", AccessTools.all, null,
                    new[] { typeof(Trait), typeof(bool) }, null);
                if (gain == null)
                {
                    EyeColorPatchMod.Warn("找不到 TraitSet.GainTrait，瞳色不会随特性增加刷新。");
                }
                else
                {
                    harmony.Patch(gain, postfix: new HarmonyMethod(typeof(RefreshPatches), nameof(Postfix_TraitChanged)));
                }

                MethodInfo remove = typeof(TraitSet).GetMethod("RemoveTrait", AccessTools.all, null,
                    new[] { typeof(Trait), typeof(bool) }, null);
                if (remove == null)
                {
                    EyeColorPatchMod.Warn("找不到 TraitSet.RemoveTrait，瞳色不会随特性移除刷新。");
                }
                else
                {
                    harmony.Patch(remove, postfix: new HarmonyMethod(typeof(RefreshPatches), nameof(Postfix_TraitChanged)));
                }
            }
            catch (Exception e)
            {
                EyeColorPatchMod.Warn("补丁 TraitSet 失败：" + e.Message);
            }
        }

        public static void Postfix_AddHediff(Pawn ___pawn, Hediff hediff)
        {
            if (hediff == null)
            {
                return;
            }
            if (EyeRefreshUtility.IsEyePart(hediff.Part) || EyeColorRegistry.IsKnownEyeHediff(hediff.def))
            {
                EyeRefreshUtility.RefreshPawn(___pawn);
            }
        }

        public static void Postfix_RemoveHediff(Pawn ___pawn, Hediff hediff)
        {
            if (hediff == null)
            {
                return;
            }
            if (EyeRefreshUtility.IsEyePart(hediff.Part) || EyeColorRegistry.IsKnownEyeHediff(hediff.def))
            {
                EyeRefreshUtility.RefreshPawn(___pawn);
            }
        }

        public static void Postfix_Notify_GenesChanged(Pawn ___pawn, GeneDef addedOrRemovedGene)
        {
            if (addedOrRemovedGene == null || EyeColorRegistry.GeneKeys.Count == 0)
            {
                return;
            }
            if (EyeColorRegistry.IsKnownEyeGene(addedOrRemovedGene.defName))
            {
                EyeRefreshUtility.RefreshPawn(___pawn);
            }
        }

        /// <summary>
        /// 特性变化后刷新。不需要判断是哪个特性：TraitSet 的 pawn 字段就是我们要刷的角色，
        /// 而 EyeRefreshUtility 内部只做「有没有控制器」的判断，成本很低；
        /// 更要紧的是「特性被别的特性压制 / 解除压制」时也应该刷新。
        /// </summary>
        public static void Postfix_TraitChanged(Pawn ___pawn)
        {
            if (EyeColorRegistry.TraitColors.Count == 0)
            {
                return;
            }
            EyeRefreshUtility.RefreshPawn(___pawn);
        }
    }
}
