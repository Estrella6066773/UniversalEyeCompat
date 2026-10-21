using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 与 [NL] Facial Animation 的全部接触面。
    ///
    /// 为什么全程用反射而不是直接引用 FacialAnimation.dll：
    /// FA 以独立程序集发行，直接引用会让本模组在 FA 缺席或改名时连编译好的副本都加载不了。
    /// 反射 + AccessTools.TypeByName 只要「运行时能找到类型」即可，找不到就整体停用，不会抛异常。
    ///
    /// 关键类型：
    /// - FacialAnimation.EyeballColorDef：瞳色定义，字段 geneDef(string) / hediffDef(HediffDef) / eyeballColor(Color)
    /// - FacialAnimation.EyeballControllerComp：瞳孔控制器，属性 FaceColor / FaceSecondColor，
    ///   方法 SetDirty / LoadTextures / CheckLoadTextures
    /// - FacialAnimation.DrawFaceGraphicsComp：决定「这张脸到底画不画」的组件（internal，只能反射）
    /// - FacialAnimation.FAHelper.ShouldDrawPawn(Pawn)：FA 自己的判定，本模组直接复用，保证口径一致
    /// </summary>
    internal static class FacialAnimationBridge
    {
        public const string EyeballColorDefTypeName = "FacialAnimation.EyeballColorDef";
        public const string EyeballControllerCompTypeName = "FacialAnimation.EyeballControllerComp";
        public const string DrawFaceCompTypeName = "FacialAnimation.DrawFaceGraphicsComp";
        public const string HelperTypeName = "FacialAnimation.FAHelper";

        public static readonly Type EyeballColorDefType = AccessTools.TypeByName(EyeballColorDefTypeName);
        public static readonly Type EyeballControllerCompType = AccessTools.TypeByName(EyeballControllerCompTypeName);
        public static readonly Type DrawFaceCompType = AccessTools.TypeByName(DrawFaceCompTypeName);

        private static readonly Type HelperType = AccessTools.TypeByName(HelperTypeName);
        private static readonly MethodInfo ShouldDrawPawnMethod =
            HelperType?.GetMethod("ShouldDrawPawn", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Pawn) }, null);

        private static readonly FieldInfo GeneDefField = FindField(EyeballColorDefType, "geneDef");
        private static readonly FieldInfo HediffDefField = FindField(EyeballColorDefType, "hediffDef");
        private static readonly FieldInfo EyeballColorField = FindField(EyeballColorDefType, "eyeballColor");
        private static readonly FieldInfo DefNameField = FindField(EyeballColorDefType, "defName");
        private static readonly FieldInfo LabelField = FindField(EyeballColorDefType, "label");
        private static readonly PropertyInfo AllDefsProperty = FindStaticProperty(EyeballColorDefType, "AllDefsListForReading");
        private static readonly MethodInfo AddMethod = FindAddMethod(EyeballColorDefType);

        private static readonly FieldInfo PawnField = FindField(EyeballControllerCompType, "pawn");
        private static readonly FieldInfo ColorField = FindField(EyeballControllerCompType, "color");
        private static readonly FieldInfo SecondColorField = FindField(EyeballControllerCompType, "secondColor");
        private static readonly PropertyInfo FaceColorProperty = FindProperty(EyeballControllerCompType, "FaceColor");
        private static readonly PropertyInfo FaceSecondColorProperty = FindProperty(EyeballControllerCompType, "FaceSecondColor");
        private static readonly MethodInfo SetDirtyMethod = FindMethod(EyeballControllerCompType, "SetDirty");
        private static readonly MethodInfo LoadTexturesMethod = FindMethod(EyeballControllerCompType, "LoadTextures");
        private static readonly MethodInfo CheckLoadTexturesMethod = FindMethod(EyeballControllerCompType, "CheckLoadTextures");

        private static readonly PropertyInfo FaceCompInitializedProperty = DrawFaceCompType?.GetProperty("isInitialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo FaceCompEnableDrawingProperty = DrawFaceCompType?.GetProperty("EnableDrawing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>能否读写瞳色：优先公开属性，属性不全时退回字段。</summary>
        private static bool CanReadWriteColors =>
            (FaceColorProperty != null && FaceSecondColorProperty != null) ||
            (ColorField != null && SecondColorField != null);

        /// <summary>FA 是否在场并且接口齐全。为假时本模组整体不工作，只打一条日志。</summary>
        public static bool Available =>
            EyeballColorDefType != null &&
            EyeballControllerCompType != null &&
            GeneDefField != null &&
            HediffDefField != null &&
            EyeballColorField != null &&
            AddMethod != null &&
            AllDefsProperty != null &&
            PawnField != null &&
            CanReadWriteColors;

        public static MethodInfo LoadTextures => LoadTexturesMethod;

        /// <summary>「要不要重载瞳孔贴图」的判定方法。</summary>
        public static MethodInfo CheckLoadTextures => CheckLoadTexturesMethod;

        /// <summary>
        /// 这张脸会不会被 FA 画出来。摘掉叠加之前必须问一次，否则会给尸体 / 被关闭的种族摘成
        /// 一张没有眼睛的脸。
        ///
        /// 优先直接调用 FA 自己的 FAHelper.ShouldDrawPawn：
        /// 它同时检查性别开关、种族/异型开关、DrawFaceGraphicsComp 是否初始化并启用、渲染模式是否为尸体。
        /// 找不到这个方法时退回等价的手工判定。
        ///
        /// <paramref name="determined"/> 表示「我们是否真的问到了」。
        /// 问不到时调用方应保守处理（保留叠加）。
        /// </summary>
        public static bool ShouldDrawFace(Pawn pawn, out bool determined)
        {
            determined = false;
            if (pawn == null)
            {
                return false;
            }

            if (ShouldDrawPawnMethod != null)
            {
                try
                {
                    object result = ShouldDrawPawnMethod.Invoke(null, new object[] { pawn });
                    determined = true;
                    return result is bool drawing && drawing;
                }
                catch (Exception)
                {
                    // 落到下面的手工判定。
                }
            }

            object faceComp = GetFaceComp(pawn);
            if (faceComp == null)
            {
                return false;
            }

            try
            {
                // 尸体 / 腐烂阶段：FA 的 CompRenderNodes 会直接返回 null，不建任何人脸节点。
                if (pawn.Drawer?.renderer != null && (int)pawn.Drawer.renderer.CurRotDrawMode == 4)
                {
                    determined = true;
                    return false;
                }
                bool initialized = FaceCompInitializedProperty == null
                    || (bool)FaceCompInitializedProperty.GetValue(faceComp, null);
                bool enabled = FaceCompEnableDrawingProperty != null
                    && (bool)FaceCompEnableDrawingProperty.GetValue(faceComp, null);
                determined = true;
                return initialized && enabled;
            }
            catch (Exception)
            {
                determined = false;
                return false;
            }
        }

        /// <summary>取 FA 的「画不画脸」组件；没有就返回 null。</summary>
        public static object GetFaceComp(Pawn pawn)
        {
            object comp;
            return TryGetComp(pawn, DrawFaceCompType, out comp) ? comp : null;
        }

        /// <summary>
        /// 读出所有已存在的瞳色定义（含 FA 本体与 Experimentals / 重绘包 XML 写的）。
        /// 用于「已经有人管这个来源了吗」的判断。
        /// </summary>
        public static List<EyeballColorEntry> ReadExistingEntries()
        {
            List<EyeballColorEntry> result = new List<EyeballColorEntry>();
            if (AllDefsProperty == null)
            {
                return result;
            }
            object raw;
            try
            {
                raw = AllDefsProperty.GetValue(null, null);
            }
            catch (Exception)
            {
                return result;
            }
            if (!(raw is IEnumerable defs))
            {
                return result;
            }
            foreach (object def in defs)
            {
                if (def == null)
                {
                    continue;
                }
                EyeballColorEntry entry = new EyeballColorEntry
                {
                    DefName = DefNameField?.GetValue(def) as string,
                    GeneDefName = GeneDefField?.GetValue(def) as string,
                    HediffDef = HediffDefField?.GetValue(def) as HediffDef
                };
                result.Add(entry);
            }
            return result;
        }

        /// <summary>启动期注册一条合成瞳色定义。返回是否成功。</summary>
        public static bool TryAddEyeballColorDef(string defName, string label, string geneDefName, HediffDef hediffDef, Color color)
        {
            if (AddMethod == null || EyeballColorDefType == null)
            {
                return false;
            }
            try
            {
                object def = Activator.CreateInstance(EyeballColorDefType);
                DefNameField.SetValue(def, defName);
                if (LabelField != null && !string.IsNullOrEmpty(label))
                {
                    LabelField.SetValue(def, label);
                }
                GeneDefField.SetValue(def, geneDefName);
                HediffDefField.SetValue(def, hediffDef);
                EyeballColorField.SetValue(def, color);
                AddMethod.Invoke(null, new[] { def });
                return true;
            }
            catch (Exception e)
            {
                EyeColorPatchMod.Warn("注册瞳色定义 " + defName + " 失败：" + e.Message);
                return false;
            }
        }

        /// <summary>取出 pawn 身上的 FA 瞳孔控制器；没有就返回 null。</summary>
        public static object GetEyeballComp(Pawn pawn)
        {
            object comp;
            return TryGetComp(pawn, EyeballControllerCompType, out comp) ? comp : null;
        }

        private static bool TryGetComp(Pawn pawn, Type compType, out object comp)
        {
            comp = null;
            if (pawn == null || compType == null)
            {
                return false;
            }
            List<ThingComp> comps = pawn.AllComps;
            if (comps == null)
            {
                return false;
            }
            for (int i = 0; i < comps.Count; i++)
            {
                ThingComp candidate = comps[i];
                if (candidate != null && compType.IsInstanceOfType(candidate))
                {
                    comp = candidate;
                    return true;
                }
            }
            return false;
        }

        /// <summary>把控制器标脏，下一次重建渲染树时会重新载入瞳孔贴图。</summary>
        public static void MarkCompDirty(object comp)
        {
            if (comp == null || SetDirtyMethod == null)
            {
                return;
            }
            try
            {
                SetDirtyMethod.Invoke(comp, null);
            }
            catch (Exception)
            {
            }
        }

        public static Pawn GetPawn(object comp)
        {
            if (comp == null || PawnField == null)
            {
                return null;
            }
            try
            {
                return PawnField.GetValue(comp) as Pawn;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool TryGetOverrideColors(object comp, out Color first, out Color second)
        {
            first = Color.white;
            second = Color.white;
            if (comp == null || !CanReadWriteColors)
            {
                return false;
            }
            try
            {
                if (FaceColorProperty != null && FaceSecondColorProperty != null)
                {
                    first = (Color)FaceColorProperty.GetValue(comp, null);
                    second = (Color)FaceSecondColorProperty.GetValue(comp, null);
                    return true;
                }
                first = (Color)ColorField.GetValue(comp);
                second = (Color)SecondColorField.GetValue(comp);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void SetColors(object comp, Color first, Color second)
        {
            if (comp == null || !CanReadWriteColors)
            {
                return;
            }
            try
            {
                if (FaceColorProperty != null && FaceSecondColorProperty != null && FaceColorProperty.CanWrite && FaceSecondColorProperty.CanWrite)
                {
                    FaceColorProperty.SetValue(comp, first, null);
                    FaceSecondColorProperty.SetValue(comp, second, null);
                    return;
                }
                ColorField.SetValue(comp, first);
                SecondColorField.SetValue(comp, second);
            }
            catch (Exception)
            {
            }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            if (type == null)
            {
                return null;
            }
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field;
                }
            }
            return null;
        }

        private static MethodInfo FindMethod(Type type, string name)
        {
            if (type == null)
            {
                return null;
            }
            for (Type current = type; current != null; current = current.BaseType)
            {
                MethodInfo method = current.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    return method;
                }
            }
            return null;
        }

        /// <summary>
        /// 沿继承链找实例属性。FaceColor 定义在泛型基类 ControllerBaseComp&lt;T,S&gt; 上，
        /// 所以必须逐层向上找，不能只看 EyeballControllerComp 自己。
        /// </summary>
        private static PropertyInfo FindProperty(Type type, string name)
        {
            if (type == null)
            {
                return null;
            }
            for (Type current = type; current != null; current = current.BaseType)
            {
                PropertyInfo property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && property.PropertyType == typeof(Color))
                {
                    return property;
                }
            }
            return null;
        }

        private static PropertyInfo FindStaticProperty(Type type, string name)
        {
            if (type == null)
            {
                return null;
            }
            PropertyInfo property = typeof(DefDatabase<>).MakeGenericType(type)
                .GetProperty(name, BindingFlags.Static | BindingFlags.Public);
            return property;
        }

        private static MethodInfo FindAddMethod(Type defType)
        {
            if (defType == null)
            {
                return null;
            }
            return typeof(DefDatabase<>).MakeGenericType(defType)
                .GetMethod("Add", BindingFlags.Static | BindingFlags.Public, null, new[] { defType }, null);
        }
    }

    /// <summary>已存在的 EyeballColorDef 的摘要，只保留我们关心的三个键。</summary>
    public sealed class EyeballColorEntry
    {
        public string DefName;
        public string GeneDefName;
        public HediffDef HediffDef;
    }
}
