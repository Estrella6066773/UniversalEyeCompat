using System.Reflection;
using HarmonyLib;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// FA 的 DrawFaceGraphicsComp.CompRenderNodes 跑完之后，复核「首帧被推迟判定」的 pawn。
    ///
    /// 为什么挂在 CompRenderNodes 上：
    /// 它是 FA 决定「这张脸画不画、画哪些层」的唯一入口，执行完以后
    /// isInitialized 与 EnableDrawing 才是最终值。此时把渲染树标脏一次，
    /// 下一次构建就会按真实状态决定是否摘掉眼部叠加。
    ///
    /// DrawFaceGraphicsComp 是 internal 类，只能按类型反射拿方法；
    /// 拿不到（FA 改名/改结构）时本补丁不生效，由原有逻辑兜底。
    /// </summary>
    [HarmonyPatch]
    internal static class FaceCompRebuildPatch
    {
        public static bool Prepare()
        {
            return TargetMethod() != null;
        }

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(FacialAnimationBridge.DrawFaceCompType, "CompRenderNodes");
        }

        public static void Postfix(Pawn ___pawn)
        {
            FaceRebuildGate.Flush(___pawn);
        }
    }
}
