using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace UniversalEyeCompat
{
    /// <summary>
    /// 摘掉「已被本模组接管瞳色」的来源自带的眼部叠加节点。
    ///
    /// 为什么必须做这一步：
    /// FA 的瞳色是画在它自己那颗眼球上的，它并不会替别人把眼睛贴图收起来。
    /// 于是「模组自己的眼睛贴图」和「FA 上过色的眼球」会同时画出来 ——
    /// 表现就是瞳色明明换了，脸上却还压着原来那两个小圆点。
    ///
    /// FA 自己只处理了一部分：它的 PawnRenderTree.AddChild 前缀在
    /// child.gene 为 null 时直接放行（即不碰 hediff / 特性节点），
    /// 而且只认 PawnRenderNodeProperties_Eye 这一个类（模组用基类 + 眼部 worker 声明时漏掉）。
    /// 所以这里补的是三类它盖不住的情况，判据与取色共用 EyeNodeScanner.IsEyeNode，避免错配。
    ///
    /// 只摘「颜色确实由本模组给出」的来源（EyeCompatRegistry.Colored*）：
    /// 没给颜色的来源一旦摘掉叠加，那颗眼睛就彻底看不见了。
    /// 让位给 FA 本体 / Experimentals / 重绘包的来源也不在这里 —— 那是它们的事。
    ///
    /// 注意事项：
    /// - 只影响渲染树的构建，不改任何 Def、不写存档；来源消失后节点本来就不会再建，无需还原。
    /// - 目标是 RimWorld 的私有方法 PawnRenderTree.AddChild，找不到就整体不生效。
    /// - 这是每根节点都会走一遍的热路径，判定顺序按「先便宜后昂贵」排：索引 → 类型 → 来源 → comp。
    ///   这里刻意不写日志：渲染树重建次数不少，逐节点打印会把控制台刷爆。
    ///   想核查「哪些来源被接管了」看启动期那一行汇总即可。
    /// - 已知边界：种族自带的眼部叠加不在处理范围内。种族节点上 gene / hediff / trait 都是 null，
    ///   认不出来源，摘掉会连同该种族自己画的眼睛一起消失，宁可留给后续单独处理。
    /// </summary>
    [HarmonyPatch]
    internal static class EyeOverlaySuppressPatch
    {
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
            if (!EyeCompatRegistry.Initialized || child == null)
            {
                return true;
            }

            PawnRenderNodeProperties props = child.Props;
            if (props == null || !EyeNodeScanner.IsEyeNode(props))
            {
                return true;
            }

            // 节点自带来源身份：基因 / hediff / 特性。
            if (!EyeCompatRegistry.IsColoredEyeSource(child.gene, child.hediff, child.trait))
            {
                return true;
            }

            // FA 没在画这张脸（非人形、被设置屏蔽等）时交还原版，否则会摘出一张没有眼睛的脸。
            Pawn pawn = __instance?.pawn;
            return pawn == null || FacialAnimationBridge.GetEyeballComp(pawn) == null;
        }
    }
}
