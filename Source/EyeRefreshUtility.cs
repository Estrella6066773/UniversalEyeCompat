using RimWorld;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 让 FA 的瞳孔重新上色。
    ///
    /// 为什么需要它：
    /// FA 的 EyeballControllerComp 只在「颜色字段变了」「脸型变了」或「被显式标脏」时才重新载入瞳孔贴图。
    /// 装/拆义眼、身上多出「虚空之触」这类状态只改变 hediff，不会动颜色字段，
    /// 于是 FA 会拿旧的贴图继续画，玩家看到的就是瞳色不更新。
    ///
    /// 做法（不碰 FA 主逻辑）：
    /// 1. 把控制器标脏（SetDirty），下一次 ReloadIfNeed 就会走 LoadTextures；
    /// 2. 顺手把渲染树与头像缓存标脏，保证重建节点时能走到上面那一步。
    ///
    /// 注意：本方法可能被频繁触发，因此在调用方先做「是否与眼睛有关」的廉价判断。
    /// </summary>
    public static class EyeRefreshUtility
    {
        public static void RefreshPawn(Pawn pawn)
        {
            if (pawn == null || !EyeColorRegistry.Initialized)
            {
                return;
            }
            object comp = FacialAnimationBridge.GetEyeballComp(pawn);
            if (comp == null)
            {
                return;
            }
            FacialAnimationBridge.MarkCompDirty(comp);
            try
            {
                if (pawn.Drawer?.renderer != null)
                {
                    pawn.Drawer.renderer.SetAllGraphicsDirty();
                }
                if (pawn.Spawned)
                {
                    PortraitsCache.SetDirty(pawn);
                }
            }
            catch (System.Exception)
            {
            }
        }

        /// <summary>
        /// 眼球部位判定：原版左右眼共用 BodyPartDef「Eye」，这里按名称兜住各种变体写法。
        /// 关键词表统一放在 EyeNodeScanner，避免「扫描时认得、刷新时不认得」这种不一致。
        /// </summary>
        public static bool IsEyePart(BodyPartRecord part)
        {
            if (part == null)
            {
                return false;
            }
            return EyeNodeScanner.LooksLikeEyeName(part.def?.defName)
                || EyeNodeScanner.LooksLikeEyeName(part.untranslatedCustomLabel);
        }
    }
}
