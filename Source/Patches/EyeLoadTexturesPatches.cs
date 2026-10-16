using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace UniversalEyeCompat
{
    /// <summary>
    /// 在 FA 重新载入瞳孔贴图的前后临时替换瞳色。
    ///
    /// 这是「FA 表达不了的来源」唯一的落点，目前覆盖四类：
    /// 特性（按等级）、种族自带的眼睛、跟随发色/肤色的规则色（基因或义眼）、不挂眼球的状态。
    /// FA 只认 EyeballColorDef 上的 geneDef / hediffDef 且颜色必须是固定值，
    /// 上面这几类要么没有对应字段、要么存不下规则色，所以改为在它读颜色之前把
    /// FaceColor / FaceSecondColor 改掉，读完再还原。
    ///
    /// 为什么这样做不会抢专用模组的活：
    /// FA 的 LoadTextures 里，基因循环与 hediff 循环会在读完初始颜色之后覆盖 baseColor / eyeballColor。
    /// 也就是说，只要别人写过 EyeballColorDef，他们的颜色一定盖在我们的临时值之上；
    /// 我们只在「谁都没管这个来源」时才真正生效，天然就是兜底。
    ///
    /// 注意事项：
    /// - 只改内存里的颜色，Postfix 一定还原，不写回存档，也不动颜色以外的任何字段。
    /// - 目标方法由 FacialAnimationBridge 解析，FA 不在场时整个补丁不生效（Prepare 返回 false）。
    /// - 优先级（眼球 hediff &gt; 特性 &gt; 基因 &gt; 全身状态 &gt; 种族）集中写在
    ///   EyeCompatRegistry.TryGetEffectiveOverride 的注释里，只有一处。
    /// - 每次调用都顺手把「这次用了什么颜色」记进 EyeOverrideTracker，
    ///   好让 EyeCheckLoadTexturesPatches 下一次能判断出颜色变了没有。
    /// </summary>
    [HarmonyPatch]
    public static class EyeLoadTexturesPatches
    {
        public static bool Prepare()
        {
            return FacialAnimationBridge.Available && FacialAnimationBridge.LoadTextures != null;
        }

        public static MethodBase TargetMethod()
        {
            return FacialAnimationBridge.LoadTextures;
        }

        public static void Prefix(object __instance, out Color[] __state)
        {
            __state = null;
            if (!EyeCompatRegistry.Initialized || __instance == null)
            {
                return;
            }
            Pawn pawn = FacialAnimationBridge.GetPawn(__instance);
            if (pawn == null)
            {
                return;
            }

            bool hasColor = EyeCompatRegistry.TryGetEffectiveOverride(pawn, out Color color, out string origin);

            // 先记下「这一次本该是什么颜色」，无论成不成功都要记：
            // 记成「没有颜色」同样重要 —— 那表示特性 / 状态已经消失，下次要放行 FA 自己的颜色。
            EyeOverrideTracker.Set(__instance, hasColor, color);

            if (Prefs.DevMode && hasColor)
            {
                Log.Message("[万能眼部兼容] 临时上色：" + pawn.LabelShort + " ← " + origin);
            }

            if (!hasColor)
            {
                return;
            }
            if (!FacialAnimationBridge.TryGetOverrideColors(__instance, out Color oldFirst, out Color oldSecond))
            {
                return;
            }
            FacialAnimationBridge.SetColors(__instance, color, color);
            __state = new[] { oldFirst, oldSecond };
        }

        public static void Postfix(object __instance, Color[] __state)
        {
            if (__state == null || __instance == null)
            {
                return;
            }
            FacialAnimationBridge.SetColors(__instance, __state[0], __state[1]);
        }
    }
}
