using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace UniversalEyeCompat
{
    /// <summary>
    /// 在 FA 决定「要不要重载瞳孔贴图」时，把「我们这边看到的有效颜色变了」也算进去。
    ///
    /// 为什么需要：
    /// FA 的 ControllerBaseComp.CheckLoadTextures 只比较它自己的 color 字段、脸型与性别。
    /// 特性与全身状态的瞳色是我们临时写进去的，FA 无从感知；一旦这类来源发生变化而没有人
    /// 显式标脏（典型例子：特性被基因压制 Suppressed、读写顺序让状态先于脏标记变化），
    /// 瞳孔就会一直停在旧颜色上。
    ///
    /// 做法（只动返回值，不动 FA 的状态）：
    /// 只在 CheckLoadTextures 已经返回 false 的情况下复核一次有效颜色，
    /// 与 EyeOverrideTracker 里「上一次真正写进去的颜色」不同，就把结果改成 true，
    /// 让 FA 照常走一遍 graphicList.Clear() + LoadTextures()（颜色由 EyeLoadTexturesPatches 在那时写入）。
    ///
    /// 注意事项：
    /// - 只改返回值，不碰 FA 的任何字段，也不碰渲染请求列表，因此不会和绘制流程抢数据、
    ///   也不会在绘制中途把正在遍历的集合清掉。
    /// - 没有「临时上色」来源（特性 / 全身状态）时直接放行，零开销。
    /// - 第一次见到某个控制器（还没有记录）不强制重载，避免开局把所有 pawn 推去重载一遍。
    /// - 与 EyeRefreshPatches 是互补关系：那边的 hediff / 基因 / 特性事件负责「尽快」刷新，
    ///   这里负责「兜住所有没人通知的变化」。
    /// </summary>
    [HarmonyPatch]
    public static class EyeCheckLoadTexturesPatches
    {
        public static bool Prepare()
        {
            return FacialAnimationBridge.Available && FacialAnimationBridge.CheckLoadTextures != null;
        }

        public static MethodBase TargetMethod()
        {
            return FacialAnimationBridge.CheckLoadTextures;
        }

        public static void Postfix(object __instance, ref bool __result)
        {
            if (__result || !EyeCompatRegistry.Initialized || !EyeCompatRegistry.HasOverrideColors || __instance == null)
            {
                return;
            }
            Pawn pawn = FacialAnimationBridge.GetPawn(__instance);
            if (pawn == null)
            {
                return;
            }
            if (!EyeOverrideTracker.TryGet(__instance, out bool hadColor, out Color applied))
            {
                // 还没给这个控制器上过色，先交给正常流程跑一遍，下一次再开始比较。
                return;
            }
            bool hasColor = EyeCompatRegistry.TryResolveOverrideColor(pawn, out Color color);
            if (hasColor == hadColor && (!hasColor || color == applied))
            {
                return;
            }
            __result = true;
        }
    }
}
