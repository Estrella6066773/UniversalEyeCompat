using System.Collections.Generic;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 「这次判定发生在 FA 初始化之前」的待复核队列。
    ///
    /// 为什么需要：
    /// 渲染树首帧里，FA 的 DrawFaceGraphicsComp 还没跑过 CompRenderNodes，
    /// isInitialized / EnableDrawing 都还是初始值（false）。这时不能把节点判成
    /// 「FA 不画」而保留 —— 节点一旦进了渲染树就不会自己消失，玩家看到的就是
    /// 「颜色对了，但原版那个小圆点还在」。
    ///
    /// 做法：
    /// 1. 首帧判定时把 pawn 记进来（OverlaySuppressPatch）；
    /// 2. FA 的 CompRenderNodes 跑完（此时 initialized/EnableDrawing 已确定）后，
    ///    把这个 pawn 的渲染树标脏一次，让它按真实状态重新判定并重建。
    ///
    /// 标记只保留一次，重建后不会再触发，不会循环。
    /// </summary>
    internal static class FaceRebuildGate
    {
        /// <summary>最多同时记多少个 pawn，防止异常情况下无界增长。</summary>
        private const int MaxPending = 4096;

        private static readonly HashSet<int> Pending = new HashSet<int>();

        public static void Mark(Pawn pawn)
        {
            if (pawn == null || Pending.Count >= MaxPending)
            {
                return;
            }
            Pending.Add(pawn.thingIDNumber);
        }

        public static void Flush(Pawn pawn)
        {
            if (pawn == null || !Pending.Remove(pawn.thingIDNumber))
            {
                return;
            }
            try
            {
                if (pawn.Drawer?.renderer != null)
                {
                    pawn.Drawer.renderer.SetAllGraphicsDirty();
                }
            }
            catch (System.Exception)
            {
            }
        }
    }
}
