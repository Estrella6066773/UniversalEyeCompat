using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 摘掉「颜色已经由 FA 负责」的来源自带的眼部叠加节点。
    ///
    /// 为什么必须做这一步：
    /// FA 的瞳色是画在它自己那颗眼球上的，它并不会替别人把眼睛贴图收起来。
    /// 于是「模组自己的眼睛贴图」和「FA 上过色的眼球」会同时画出来 ——
    /// 表现就是瞳色明明换了，脸上却还压着原来那两个小圆点（原版红眼/灰眼最典型）。
    ///
    /// 摘除条件（三条同时满足）：
    /// 1. 是眼部节点（判据与取色共用 EyeNodeScanner.IsEyeNode，避免取色与摘叠加错配）；
    /// 2. 节点带得出来源身份（gene / hediff / trait），且这条来源的颜色由 FA 负责 ——
    ///    要么是本模组注册的 EyeballColorDef，要么是 FA 本体 / Experimentals / 重绘包早就写好的；
    /// 3. FA 确实会画这张脸（FacialAnimationBridge.ShouldDrawFace）。
    ///
    /// 第 3 条是本次重写的关键修复：上一版只检查「pawn 身上有没有 EyeballControllerComp」，
    /// 但组件存在不等于 FA 会绘制 —— 尸体、被 FA 设置关掉的种族/性别、FA 没在渲染的角色
    /// 都会因此被摘成一张没有眼睛的脸。现在确认不了就保留，宁可多一个圆点，也不要空眼眶。
    ///
    /// 种族自带的眼部叠加（节点上没有 gene / hediff / trait）不在处理范围内：
    /// 认不出来源，摘掉会连同该种族自己画的眼睛一起消失。
    /// </summary>
    [HarmonyPatch]
    internal static class OverlaySuppressPatch
    {
        /// <summary>补丁是否已经被调用过。用来给出「真的挂上了」的唯一凭据，只打一次。</summary>
        private static bool aliveReported;

        public static bool Prepare()
        {
            return FacialAnimationBridge.Available && TargetMethod() != null;
        }

        public static MethodBase TargetMethod()
        {
            MethodInfo exact = AccessTools.Method(
                typeof(PawnRenderTree), "AddChild", new[] { typeof(PawnRenderNode), typeof(PawnRenderNode) });
            if (exact != null)
            {
                return exact;
            }
            // 签名改名时的退路，与 FA 自己找这个方法的方式一致。
            return AccessTools.GetDeclaredMethods(typeof(PawnRenderTree))
                .FirstOrDefault(m => m.Name.Contains("AddChild"));
        }

        /// <summary>返回 false 表示这颗节点不进渲染树（它的子节点也随之一起消失）。</summary>
        public static bool Prefix(PawnRenderTree __instance, PawnRenderNode child)
        {
            ReportAliveOnce();

            if (!EyeColorRegistry.Initialized || child == null)
            {
                return true;
            }

            Pawn pawn = __instance?.pawn;
            PawnRenderNodeProperties props = child.Props;
            if (props == null || !EyeNodeScanner.IsEyeNode(props))
            {
                // 长得像眼睛却没命中三条判据的，也报一条：那颗「没人管的圆点」多半在这里。
                OverlayDiagnostics.NotClassified(pawn, child, props);
                return true;
            }

            // 种族级 / 无来源节点：认不出来源，不能摘。
            if (child.gene == null && child.hediff == null && child.trait == null)
            {
                OverlayDiagnostics.Kept(pawn, child, props, "无来源眼部节点（种族自带或节点没有来源身份）");
                return true;
            }

            if (!EyeColorRegistry.IsColorProvidedByFa(child.gene, child.hediff, child.trait))
            {
                OverlayDiagnostics.Kept(pawn, child, props, "这条来源没有瞳色定义，摘掉会让眼睛消失");
                return true;
            }

            bool determined;
            bool draws = FacialAnimationBridge.ShouldDrawFace(pawn, out determined);
            if (!determined)
            {
                // 问不到 FA 的判定（版本差异）时退回旧行为：有眼球控制器就当它在画。
                if (FacialAnimationBridge.GetEyeballComp(pawn) == null)
                {
                    OverlayDiagnostics.Kept(pawn, child, props, "无法确认 FA 是否绘制，且没有眼球控制器");
                    return true;
                }
            }
            else if (!draws)
            {
                OverlayDiagnostics.Kept(pawn, child, props,
                    "FA 没有绘制这张脸（尸体 / 种族或性别被关闭 / 未启用），保留自带眼睛");
                return true;
            }

            bool owned = EyeColorRegistry.IsOwnedByThisMod(child);
            OverlayDiagnostics.Suppressed(pawn, child, props, owned);
            return false;
        }

        /// <summary>
        /// 「补丁挂上了没有」只能由补丁自己被调用来证明 —— 靠读 Harmony 的补丁表在
        /// Harmony 2.3 / 2.4 之间字段名不一样，反而会引入新的版本依赖。
        /// </summary>
        private static void ReportAliveOnce()
        {
            if (aliveReported)
            {
                return;
            }
            aliveReported = true;
            EyeColorPatchMod.Info("摘除眼部叠加补丁已生效：Verse.PawnRenderTree.AddChild 前缀首次触发。"
                + "（开发者模式下会逐 pawn 打印「摘掉 / 保留 / 未识别」的眼部节点）");
        }
    }
}
