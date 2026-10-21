using System.Runtime.CompilerServices;
using UnityEngine;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 记录「上一次真正写进 FA 控制器的临时瞳色」。
    ///
    /// FA 只在 CheckLoadTextures 认为「颜色 / 脸型 / 性别变了」或 dirtyFlag 被置位时才重载瞳孔贴图。
    /// 我们的临时上色只改内存里的颜色字段，用完就还原，FA 自己完全察觉不到「特性 / 全身状态」引起的变化。
    /// 有了这份记录，CheckLoadTexturesPatch 就能在有效颜色变化时主动要求重载一次。
    ///
    /// 为什么用 ConditionalWeakTable：
    /// 键就是控制器（ThingComp）本身，控制器活多久记录就活多久；
    /// 存档反复读入读出、pawn 被销毁时记录会跟着一起消失。
    ///
    /// 注意事项：
    /// - 「有没有记录」与「记录里有没有颜色」是两件事：
    ///   前者用来区分「第一次见到这个控制器」（不强制重载），后者才表示「上一次确实上过色」。
    /// - 只记录不还原。还原动作由 LoadTexturesPatch 的 Postfix 负责。
    /// </summary>
    internal static class EyeOverrideTracker
    {
        private sealed class Entry
        {
            public bool HasColor;
            public Color Color;
        }

        private static readonly ConditionalWeakTable<object, Entry> Entries = new ConditionalWeakTable<object, Entry>();

        /// <summary>
        /// 读出上次写进去的临时瞳色。
        /// 返回 false 表示这个控制器还没有任何记录（第一次见到），此时调用方应当放弃强制重载。
        /// </summary>
        public static bool TryGet(object comp, out bool hasColor, out Color color)
        {
            hasColor = false;
            color = Color.white;
            if (comp == null || !Entries.TryGetValue(comp, out Entry entry))
            {
                return false;
            }
            hasColor = entry.HasColor;
            color = entry.Color;
            return true;
        }

        /// <summary>记下这一次真正生效的临时瞳色；hasColor 为 false 表示这次没有上色。</summary>
        public static void Set(object comp, bool hasColor, Color color)
        {
            if (comp == null)
            {
                return;
            }
            Entry entry = Entries.GetOrCreateValue(comp);
            entry.HasColor = hasColor;
            entry.Color = color;
        }
    }
}
