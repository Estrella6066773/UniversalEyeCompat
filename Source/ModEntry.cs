using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 模组入口。HarmonyId 必须与 About.xml 的 packageId 一致。
    ///
    /// 构造函数里立刻打补丁，而不是等到 StaticConstructorOnStartup：
    /// 属性式补丁（LoadTexturesPatch）要在任何 pawn 渲染之前就位，
    /// 刷新补丁（RefreshPatches）也要在主菜单之前挂好，否则开局就有窗口期。
    ///
    /// 数据注册不在这里做，见 EyeColorStartup（它需要等 Def 数据库就绪）。
    /// </summary>
    public class EyeColorPatchMod : Mod
    {
        public const string PackageId = "Estrella6066.universalEyeColorPatch";

        /// <summary>所有日志行的统一前缀。换模组名字时只改这一处。</summary>
        public const string LogPrefix = "[万用瞳色补丁] ";

        public EyeColorPatchMod(ModContentPack content) : base(content)
        {
            Harmony harmony = new Harmony(PackageId);
            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception e)
            {
                Log.Error(LogPrefix + "属性补丁失败：" + e);
            }

            RefreshPatches.Apply(harmony);
        }

        public static void Warn(string message)
        {
            Log.Warning(LogPrefix + message);
        }

        public static void Info(string message)
        {
            Log.Message(LogPrefix + message);
        }

        public static void Error(string message)
        {
            Log.Error(LogPrefix + message);
        }
    }
}
