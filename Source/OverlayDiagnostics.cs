using System.Collections.Generic;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 「眼部叠加被摘掉 / 被保留 / 没被认出来」的开发者模式诊断。
    ///
    /// 眼睛叠加在画面上的表现就是两颗小圆点 —— 看得见「有」，看不见「是谁画的」。
    /// 一旦出现「颜色已经接管了、圆点却还在」，纯靠肉眼看不出原因，
    /// 唯一能定位的办法就是把渲染树构建期的判定过程打出来。
    ///
    /// 三类输出：
    /// 1. 摘掉：颜色由 FA 负责（本模组或已有的 EyeballColorDef），且 FA 确实在画这张脸；
    /// 2. 保留：是眼部节点，但颜色没人负责，或 FA 没在画这张脸（尸体、被设置关掉的种族/性别）；
    /// 3. 未识别：贴图名 / 节点类名 / worker 名里带 eye 字样，却三条眼部判据都没命中。
    ///
    /// 打印策略（渲染树会反复重建，不能逐节点直打）：
    /// 只在开发者模式打印；同一条「判定 + pawn + 来源 + 贴图 + 理由」只打一次；总量封顶。
    /// </summary>
    internal static class OverlayDiagnostics
    {
        /// <summary>最多打印多少行，防止一屏一屏地刷。</summary>
        private const int MaxLines = 200;

        /// <summary>去重键的存放上限，防止长期游戏里无界增长。</summary>
        private const int MaxKeys = 1500;

        private static readonly HashSet<string> Logged = new HashSet<string>();

        private static int lines;
        private static bool truncated;

        internal static void Suppressed(Pawn pawn, PawnRenderNode node, PawnRenderNodeProperties props, bool ownedByUs)
        {
            Report("摘掉", pawn, node, props, ownedByUs ? "颜色由本模组提供" : "颜色由 FA/其它模组的瞳色定义提供");
        }

        internal static void Kept(Pawn pawn, PawnRenderNode node, PawnRenderNodeProperties props, string reason)
        {
            Report("保留", pawn, node, props, reason);
        }

        /// <summary>
        /// 这颗节点不是眼部节点（按 EyeNodeScanner 的三条判据），
        /// 但名字里带 eye 字样 —— 可能正是那颗「没人管的圆点」。
        /// </summary>
        internal static void NotClassified(Pawn pawn, PawnRenderNode node, PawnRenderNodeProperties props)
        {
            if (!Prefs.DevMode || props == null)
            {
                return;
            }
            if (LooksLikeEyeTexture(props))
            {
                Report("未识别", pawn, node, props, "贴图名里有 eye，但三条眼部判据都没命中");
                return;
            }
            if (LooksLikeEyeClass(props))
            {
                Report("未识别", pawn, node, props, "节点类 / worker 名里有 eye，但三条眼部判据都没命中");
            }
        }

        private static void Report(string action, Pawn pawn, PawnRenderNode node, PawnRenderNodeProperties props, string reason)
        {
            if (!Prefs.DevMode)
            {
                return;
            }
            string source = DescribeSource(node);
            string key = action + "|" + (pawn?.ThingID ?? "无pawn") + "|" + source + "|" + props.texPath + "|" + reason;
            if (!Logged.Add(key))
            {
                return;
            }
            if (lines >= MaxLines || Logged.Count > MaxKeys)
            {
                if (!truncated)
                {
                    truncated = true;
                    EyeColorPatchMod.Info("[诊断] 眼部叠加诊断已打满 " + MaxLines + " 行，后面不再打印（重进游戏可重新看）。");
                }
                return;
            }
            lines++;
            EyeColorPatchMod.Info("[诊断] " + action + "眼部叠加：" + PawnLabel(pawn)
                + " ← " + source
                + (string.IsNullOrEmpty(reason) ? string.Empty : "（" + reason + "）")
                + "｜" + DescribeNode(props));
        }

        /// <summary>节点自带来源身份：基因 / hediff / 特性；三者都是 null 时说明来源认不出来。</summary>
        private static string DescribeSource(PawnRenderNode node)
        {
            if (node == null)
            {
                return "节点为空";
            }
            if (node.gene?.def != null)
            {
                return "基因 " + node.gene.def.defName;
            }
            if (node.hediff?.def != null)
            {
                return "hediff " + node.hediff.def.defName;
            }
            if (node.trait?.def != null)
            {
                return "特性 " + node.trait.def.defName + "@" + node.trait.Degree;
            }
            return "无来源（节点上 gene / hediff / trait 都是 null）";
        }

        /// <summary>把节点的关键身份写成一行：认节点用 class，认 mod 用贴图路径。</summary>
        private static string DescribeNode(PawnRenderNodeProperties props)
        {
            return "class=" + props.GetType().Name
                + " worker=" + (props.workerClass?.Name ?? "无")
                + " tex=" + (props.texPath ?? (props.texPathFemale ?? "(无)"))
                + " anchor=" + (props.anchorTag ?? "无")
                + (props.skipFlag != null ? " skipFlag=" + props.skipFlag.defName : string.Empty)
                + (string.IsNullOrEmpty(props.debugLabel) ? string.Empty : " debugLabel=" + props.debugLabel);
        }

        private static bool LooksLikeEyeTexture(PawnRenderNodeProperties props)
        {
            if (EyeNodeScanner.LooksLikeEyeName(props.texPath) || EyeNodeScanner.LooksLikeEyeName(props.texPathFemale))
            {
                return true;
            }
            if (ContainsEyeName(props.texPaths) || ContainsEyeName(props.texPathsFemale))
            {
                return true;
            }
            return false;
        }

        private static bool LooksLikeEyeClass(PawnRenderNodeProperties props)
        {
            return EyeNodeScanner.LooksLikeEyeName(props.nodeClass?.Name) ||
                   EyeNodeScanner.LooksLikeEyeName(props.workerClass?.Name);
        }

        private static bool ContainsEyeName(List<string> paths)
        {
            if (paths == null)
            {
                return false;
            }
            for (int i = 0; i < paths.Count; i++)
            {
                if (EyeNodeScanner.LooksLikeEyeName(paths[i]))
                {
                    return true;
                }
            }
            return false;
        }

        private static string PawnLabel(Pawn pawn)
        {
            if (pawn == null)
            {
                return "（还没有 pawn：渲染树正在搭建）";
            }
            return pawn.LabelShort + "/" + (pawn.def?.defName ?? "?");
        }
    }
}
