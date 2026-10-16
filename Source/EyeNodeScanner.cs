using System;
using System.Collections.Generic;
using Verse;

namespace UniversalEyeCompat
{
    /// <summary>
    /// 判读「一组渲染节点里有没有眼睛」以及「这颗眼睛是什么颜色」。
    ///
    /// 为什么单独成一个文件：
    /// 基因、hediff、特性等级、种族渲染树四个来源的节点结构完全一样，
    /// 判读逻辑只能有一份，否则改一处漏三处，覆盖率自然上不去 —— 而覆盖率正是本模组的意义。
    ///
    /// 取色优先级（命中即停）：
    /// 1. 节点显式声明的颜色（colorType = Hair / Skin，或 color 有值）—— 这是模组作者的明确意图，最可信；
    /// 2. 节点贴图（由 TextureColorSampler 采样，见 EyeColorResolver）。
    /// 显式颜色永远压过贴图采样：很多模组的眼睛贴图是灰度遮罩，
    /// 颜色完全写在 XML 的 color 里，只看贴图会把红眼算成灰眼。
    /// </summary>
    internal static class EyeNodeScanner
    {
        /// <summary>
        /// 名字里出现这些词就当「眼睛相关」。
        /// 只用于「没画眼部渲染节点」的兜底识别（纯数值义眼、没贴图的植入体），
        /// 不会单独用来给已有眼睛节点的来源取色。
        /// </summary>
        private static readonly string[] EyeNameTokens =
        {
            "eye", "eyes", "iris", "pupil", "sclera", "ocular", "optic", "retina", "cornea"
        };

        public static bool LooksLikeEyeName(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string lower = text.ToLowerInvariant();
            for (int i = 0; i < EyeNameTokens.Length; i++)
            {
                if (lower.IndexOf(EyeNameTokens[i], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>判断一组渲染节点（含 children）里是否含眼睛节点。</summary>
        public static bool HasEyeNode(List<PawnRenderNodeProperties> properties)
        {
            if (properties == null)
            {
                return false;
            }
            for (int i = 0; i < properties.Count; i++)
            {
                PawnRenderNodeProperties node = properties[i];
                if (IsEyeNode(node) || HasEyeNode(node?.children))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>种族渲染树是「一个根节点 + 递归 children」，单独给一个入口。</summary>
        public static bool HasEyeNodeInTree(PawnRenderNodeProperties root)
        {
            if (root == null)
            {
                return false;
            }
            if (IsEyeNode(root))
            {
                return true;
            }
            return HasEyeNode(root.children);
        }

        /// <summary>
        /// 收集一组节点里「眼睛外观」的全部信息：贴图路径 + 显式颜色。
        ///
        /// declared 为「第一条命中的显式颜色」：原版同一条来源上可能挂多个眼睛节点，
        /// 全部同色是常态，取第一条既有确定性也最省事；真出现多色，后写的也不会被静默吞掉，
        /// 因为安装后可以在开发者模式日志里看到每条来源最终用的是哪个颜色。
        ///
        /// 眼睛节点的 children 一并算作眼睛外观的一部分（insideEye）：
        /// 高光、瞳孔这类子节点常挂在眼睛节点下面，模组把颜色写在那里是常见写法。
        /// </summary>
        public static void CollectEyeData(List<PawnRenderNodeProperties> properties, List<string> texturePaths, ref EyeColorSpec? declared)
        {
            Collect(properties, texturePaths, ref declared, false);
        }

        private static void Collect(List<PawnRenderNodeProperties> properties, List<string> texturePaths, ref EyeColorSpec? declared, bool insideEye)
        {
            if (properties == null)
            {
                return;
            }
            for (int i = 0; i < properties.Count; i++)
            {
                PawnRenderNodeProperties node = properties[i];
                if (node == null)
                {
                    continue;
                }
                bool isEye = IsEyeNode(node);
                bool partOfEye = isEye || insideEye;
                if (partOfEye)
                {
                    AddPath(texturePaths, node.texPath);
                    AddPath(texturePaths, node.texPathFemale);
                    AddPaths(texturePaths, node.texPaths);
                    AddPaths(texturePaths, node.texPathsFemale);

                    if (!declared.HasValue)
                    {
                        switch (node.colorType)
                        {
                            case PawnRenderNodeProperties.AttachmentColorType.Hair:
                                declared = EyeColorSpec.FollowHair(node.colorRGBPostFactor);
                                break;
                            case PawnRenderNodeProperties.AttachmentColorType.Skin:
                                declared = EyeColorSpec.FollowSkin(node.colorRGBPostFactor);
                                break;
                            default:
                                // Custom：color 有值就用它；没值表示「用默认着色」，交给贴图/关键词兜底。
                                if (node.color.HasValue)
                                {
                                    declared = EyeColorSpec.FromFixed(node.color.Value, node.colorRGBPostFactor);
                                }
                                break;
                        }
                    }
                }
                Collect(node.children, texturePaths, ref declared, partOfEye);
            }
        }

        /// <summary>
        /// 判断一个节点是不是「眼睛叠加节点」。
        ///
        /// 判据有三个，命中任一即算（依据 Galaxy 里的实践反推）：
        /// 1. 就是 PawnRenderNodeProperties_Eye（原版与多数模组的写法）；
        /// 2. workerClass 是 PawnRenderNodeWorker_Eye 或它的子类 ——
        ///    这是原版给 Eye 类节点设的默认 worker，模组用基类 + 自带 worker 声明眼睛时靠这条兜住。
        ///    真实例子：Medical System Expansion 2 的机械面罩用基类 + PawnRenderNodeWorker_HediffEye 声明眼部叠加。
        /// 3. skipFlag 指向名为 Eyes 的 RenderSkipFlagDef ——
        ///    原版就是在 PawnRenderNodeProperties_Eye.ResolveReferences 里设这一项，
        ///    用来在「眼睛该被遮住」时跳过整颗眼球，是语义上最准的「这是眼睛」标记。
        ///
        /// 为什么不用 anchorTag 里有没有 Eye：
        /// 伤痕、青紫这类非瞳色叠加也会锚在 RightEye / LeftEye 上，
        /// 把它们当成瞳色来源会把淤青的紫色当成瞳色画到瞳孔上，宁可漏也不错。
        ///
        /// 同一个判据同时给「取色」和「摘掉自带叠加」用（见 EyeOverlaySuppressPatch）：
        /// 两处必须一致，否则会出现「按 A 取色、按 B 摘叠加」的错配。
        /// </summary>
        internal static bool IsEyeNode(PawnRenderNodeProperties properties)
        {
            if (properties == null)
            {
                return false;
            }
            if (properties is PawnRenderNodeProperties_Eye)
            {
                return true;
            }
            // 用 is 而不是 == typeof：第三方模组会继承 PawnRenderNodeWorker_Eye 加自己的行为。
            if (properties.workerClass != null && typeof(PawnRenderNodeWorker_Eye).IsAssignableFrom(properties.workerClass))
            {
                return true;
            }
            return properties.skipFlag != null && properties.skipFlag.defName == "Eyes";
        }

        private static void AddPaths(List<string> into, List<string> paths)
        {
            if (paths == null)
            {
                return;
            }
            for (int i = 0; i < paths.Count; i++)
            {
                AddPath(into, paths[i]);
            }
        }

        private static void AddPath(List<string> into, string path)
        {
            if (!string.IsNullOrEmpty(path) && !into.Contains(path))
            {
                into.Add(path);
            }
        }
    }
}
