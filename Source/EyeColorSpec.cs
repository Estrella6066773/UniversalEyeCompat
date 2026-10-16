using UnityEngine;
using Verse;

namespace UniversalEyeCompat
{
    /// <summary>一条来源的颜色该怎么算。</summary>
    public enum EyeColorMode
    {
        /// <summary>固定颜色，直接写进 FA 的 EyeballColorDef。</summary>
        Fixed,

        /// <summary>跟随发色（原版 PawnRenderNodeProperties.colorType = Hair）。</summary>
        Hair,

        /// <summary>跟随肤色（原版 PawnRenderNodeProperties.colorType = Skin）。</summary>
        Skin
    }

    /// <summary>
    /// 一条眼部来源的颜色说明。
    ///
    /// 为什么不是单纯的 Color：
    /// 原版渲染节点自己就支持「颜色跟随发色 / 肤色」（PawnRenderNodeProperties.colorType），
    /// 不少第三方模组正是这么定义眼睛的 —— 眼睛颜色跟着头发走。这类来源没有固定色值，
    /// 写不进 FA 的 EyeballColorDef（那是静态定义），只能在渲染前按 pawn 现算，
    /// 所以颜色必须能表达成「规则」而不是「数值」。本类型就是那条规则。
    ///
    /// PostFactor 对应原版 PawnRenderNodeProperties.colorRGBPostFactor：
    /// 原版是在 ColorFor 里「算完颜色再乘」的，所以固定色也存原始值、统一在 TryResolve 里相乘，
    /// 避免 Hair/Skin 两种模式漏乘、或者固定色被乘两次。
    ///
    /// 取值规则与 Verse.PawnRenderNode.ColorFor 保持一致（含雕像色），
    /// 这样「我们算给 FA 的颜色」与「原版自己渲染这颗眼球时用的颜色」是同一个值。
    /// </summary>
    public struct EyeColorSpec
    {
        public EyeColorMode Mode;

        /// <summary>Mode == Fixed 时的原始颜色（未乘 PostFactor）。</summary>
        public Color Fixed;

        public float PostFactor;

        public static EyeColorSpec FromFixed(Color color, float postFactor)
        {
            return new EyeColorSpec
            {
                Mode = EyeColorMode.Fixed,
                Fixed = color,
                PostFactor = postFactor <= 0f ? 1f : postFactor
            };
        }

        public static EyeColorSpec FollowHair(float postFactor)
        {
            return new EyeColorSpec
            {
                Mode = EyeColorMode.Hair,
                PostFactor = postFactor <= 0f ? 1f : postFactor
            };
        }

        public static EyeColorSpec FollowSkin(float postFactor)
        {
            return new EyeColorSpec
            {
                Mode = EyeColorMode.Skin,
                PostFactor = postFactor <= 0f ? 1f : postFactor
            };
        }

        /// <summary>按 pawn 现有状态算出实际瞳色；算不出来返回 false。</summary>
        public bool TryResolve(Pawn pawn, out Color color)
        {
            color = Color.white;
            switch (Mode)
            {
                case EyeColorMode.Fixed:
                    color = Fixed;
                    break;

                case EyeColorMode.Hair:
                    // 原版这里是 pawn.story.HairColor，story 为空时原版打一条 ErrorOnce 并退回白色。
                    color = pawn?.story != null ? pawn.story.HairColor : Color.white;
                    break;

                case EyeColorMode.Skin:
                    // 原版优先用雕像色（CompStatue 造出来的假 pawn），其次才是肤色。
                    Color? statueColor = pawn?.Drawer?.renderer?.StatueColor;
                    color = statueColor ?? (pawn?.story != null ? pawn.story.SkinColor : Color.white);
                    break;

                default:
                    return false;
            }

            if (PostFactor != 1f)
            {
                color *= PostFactor;
            }
            color.a = 1f;
            return true;
        }

        /// <summary>日志用描述。固定色给出数值，规则色给出「跟随什么」。</summary>
        public string Describe()
        {
            switch (Mode)
            {
                case EyeColorMode.Hair:
                    return PostFactor == 1f ? "跟随发色" : "跟随发色×" + PostFactor.ToString("0.###");
                case EyeColorMode.Skin:
                    return PostFactor == 1f ? "跟随肤色" : "跟随肤色×" + PostFactor.ToString("0.###");
                default:
                    return "RGB(" + Fixed.r.ToString("0.000") + "," + Fixed.g.ToString("0.000") + "," + Fixed.b.ToString("0.000") + ")"
                        + (PostFactor == 1f ? string.Empty : "×" + PostFactor.ToString("0.###"));
            }
        }
    }
}
