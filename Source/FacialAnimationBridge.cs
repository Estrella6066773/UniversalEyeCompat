using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace UniversalEyeCompat
{
    /// <summary>
    /// 与 [NL] Facial Animation 的全部接触面。
    ///
    /// 为什么全程用反射而不是直接引用 FacialAnimation.dll：
    /// FA 目前仍以 1.6 程序集发行，路径在创意工坊目录里，直接引用会让本模组在 FA 缺席或改名时连编译好的副本都加载不了。
    /// 反射 + AccessTools.TypeByName 只要「运行时能找到类型」即可，找不到就整体停用，不会抛异常。
    ///
    /// 关键类型：
    /// - FacialAnimation.EyeballColorDef：瞳色定义，字段 geneDef(string) / hediffDef(HediffDef) / eyeballColor(Color)
    /// - FacialAnimation.EyeballControllerComp：瞳孔控制器，属性 FaceColor(右眼) / FaceSecondColor(左眼)，
    ///   方法 SetDirty / LoadTextures / CheckLoadTextures
    ///
    /// 读写颜色优先走公开属性（FaceColor / FaceSecondColor），只有在属性缺失时才退回读写 protected 字段
    /// （color / secondColor）。属性改名的风险比字段小，而且属性本来就是给外部用的入口。
    /// </summary>
    internal static class FacialAnimationBridge
    {
        public const string EyeballColorDefTypeName = "FacialAnimation.EyeballColorDef";
        public const string EyeballControllerCompTypeName = "FacialAnimation.EyeballControllerComp";

        public static readonly Type EyeballColorDefType = AccessTools.TypeByName(EyeballColorDefTypeName);
        public static readonly Type EyeballControllerCompType = AccessTools.TypeByName(EyeballControllerCompTypeName);

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

        /// <summary>
        /// 「要不要重载瞳孔贴图」的判定方法。本模组在它返回 false 之后再复核一次有效颜色，
        /// 见 EyeCheckLoadTexturesPatches。
        /// </summary>
        public static MethodInfo CheckLoadTextures => CheckLoadTexturesMethod;

        /// <summary>
        /// 读出所有已存在的瞳色定义（含 FA 本体与 Experimentals / 重绘包 XML 写的）。
        /// 返回的每项包含 GeneDef 键、HediffDef 键，用于「已经有人管这个来源了吗」的判断。
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
                Log.Warning("[万能眼部兼容] 注册瞳色定义 " + defName + " 失败：" + e.Message);
                return false;
            }
        }

        /// <summary>取出 pawn 身上的 FA 瞳孔控制器；没有就返回 null。</summary>
        public static object GetEyeballComp(Pawn pawn)
        {
            if (pawn == null || EyeballControllerCompType == null)
            {
                return null;
            }
            List<ThingComp> comps = pawn.AllComps;
            if (comps == null)
            {
                return null;
            }
            for (int i = 0; i < comps.Count; i++)
            {
                ThingComp comp = comps[i];
                if (comp != null && EyeballControllerCompType.IsInstanceOfType(comp))
                {
                    return comp;
                }
            }
            return null;
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
