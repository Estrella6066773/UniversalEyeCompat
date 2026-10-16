using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace UniversalEyeCompat
{
    /// <summary>
    /// 模组入口。HarmonyId 必须与 About.xml 的 packageId 一致。
    ///
    /// 构造函数里立刻打补丁，而不是等到 StaticConstructorOnStartup：
    /// 属性式补丁（EyeLoadTexturesPatches）要在任何 pawn 渲染之前就位，
    /// 刷新补丁（EyeRefreshPatches）也要在主菜单之前挂好，否则开局就有窗口期。
    ///
    /// 数据注册不在这里做，见 EyeCompatStartup（它需要等 Def 数据库就绪）。
    /// </summary>
    public class UniversalEyeCompatMod : Mod
    {
        public const string HarmonyId = "Estrella6066.universalEyeCompat";

        public UniversalEyeCompatMod(ModContentPack content) : base(content)
        {
            Harmony harmony = new Harmony(HarmonyId);
            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception e)
            {
                Log.Error("[万能眼部兼容] 属性补丁失败：" + e);
            }

            EyeRefreshPatches.Apply(harmony);
        }
    }
}
