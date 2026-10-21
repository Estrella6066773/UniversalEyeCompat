using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 眼部外观来源的种类。决定这条来源最终走哪条通道交给 FA：
    /// - Gene / Hediff 且颜色固定 → 注册 EyeballColorDef，交给 FA 原流程（覆盖所有角色）；
    /// - 颜色是「跟随发色/肤色」的规则 → 只能渲染前临时上色；
    /// - Trait / Race → FA 没有对应通道，只能渲染前临时上色。
    /// </summary>
    public enum EyeSourceKind
    {
        Gene,
        Hediff,

        /// <summary>特性（按等级区分，见 Degree）。</summary>
        Trait,

        /// <summary>种族自带的眼部节点（写在该种族渲染树里的眼睛）。</summary>
        Race
    }

    /// <summary>
    /// 一条「会改变角色眼部外观」的定义。
    ///
    /// 数据流向：
    /// EyeSourceDiscovery 扫描 Def 数据库生成本对象 →
    /// EyeColorResolver 决定颜色（EyeColorSpec）→
    /// EyeColorDefInjector 按「来源种类 + 颜色是否固定」分流到
    /// FA 的 EyeballColorDef 或本模组的运行期临时上色表。
    /// </summary>
    public sealed class EyeSource
    {
        public EyeSourceKind Kind;

        /// <summary>原始定义：GeneDef / HediffDef / TraitDef / ThingDef。</summary>
        public Def SourceDef;

        /// <summary>
        /// 查表用的键：基因与 hediff 用 defName（要与 FA 的 EyeballColorDef 字段对齐），
        /// 特性用 EyeColorRegistry.TraitKey(defName, degree)，种族用 defName（只作日志用）。
        /// </summary>
        public string Key;

        /// <summary>
        /// 仅对特性有意义：命中眼部节点的那个等级。
        ///
        /// 同一条 TraitDef 的不同等级可以各自带或不带眼睛节点（异象的「肉体掌控」就是这样），
        /// 所以特性必须按「定义 + 等级」两条键登记。
        /// </summary>
        public int Degree;

        /// <summary>眼睛叠加贴图路径（男/女），用于取色采样，可能为空。</summary>
        public List<string> TexturePaths = new List<string>();

        /// <summary>仅对基因有意义：基因图标路径（GeneDef.iconPath），取色顺序里排最后。</summary>
        public string IconPath;

        /// <summary>来源自己声明的颜色（原版渲染节点的 color / colorType）。</summary>
        public EyeColorSpec? Declared;

        /// <summary>
        /// 仅对 hediff 有意义：该状态是否会挂在眼球部件上。
        ///
        /// FA 的 hediff 通道写法是「hediff 实例的 Part 必须等于左右眼 BodyPartRecord」，
        /// 所以只有确定装在眼球上的来源才值得注册成 EyeballColorDef。
        /// 判定依据是配方（见 EyeSourceDiscovery.BuildHediffPartVerdicts）：
        /// - 配方明确写在眼球上 → true；
        /// - 配方没写部位的「加装部件」（addedPartProps 非空）→ true（无证据 ≠ 反证）；
        /// - 配方写在头/脸上、或压根不挂部位 → false，改走渲染前临时上色。
        /// </summary>
        public bool AppliesToEyePart;

        /// <summary>仅对 hediff 有意义：这是「植入体/装置」还是「疾病类状态」。</summary>
        public bool IsImplant;

        /// <summary>这条来源是不是精灵（Faction - Elves）瞳色。用于日志与兼容层说明。</summary>
        public bool Elf;

        /// <summary>最终颜色规则，由 EyeColorResolver 填充。</summary>
        public EyeColorSpec Spec;

        /// <summary>颜色来源，仅用于日志：declared / table / texture-path / texture / keyword / icon。</summary>
        public string ColorOrigin;

        /// <summary>识别依据，仅用于日志。</summary>
        public string DetectReason;

        /// <summary>这条来源是靠启发式认出来的，而不是靠明确的渲染节点。</summary>
        public bool Heuristic;

        public string Label
        {
            get
            {
                if (SourceDef == null)
                {
                    return Key;
                }
                string label = SourceDef.label;
                return string.IsNullOrEmpty(label) ? SourceDef.defName : label;
            }
        }

        public override string ToString()
        {
            return Kind + " " + Key;
        }
    }
}
