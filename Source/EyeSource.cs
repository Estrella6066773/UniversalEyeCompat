using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace UniversalEyeCompat
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
    /// EyeEffectDiscovery 扫描 Def 数据库生成本对象 →
    /// EyeColorResolver 决定颜色（EyeColorSpec）→
    /// EyeballColorDefInjector 按「来源种类 + 颜色是否固定」分流到
    /// FA 的 EyeballColorDef 或本模组的运行期临时上色表。
    /// </summary>
    public sealed class EyeSource
    {
        public EyeSourceKind Kind;

        /// <summary>原始定义：GeneDef / HediffDef / TraitDef / ThingDef。</summary>
        public Def SourceDef;

        /// <summary>
        /// 查表用的键：基因与 hediff 用 defName（要与 FA 的 EyeballColorDef 字段对齐），
        /// 特性用 EyeCompatRegistry.TraitKey(defName, degree)，种族用 defName（只作日志用）。
        /// </summary>
        public string Key;

        /// <summary>
        /// 仅对特性有意义：命中眼部节点的那个等级。
        ///
        /// 同一条 TraitDef 的不同等级可以各自带或不带眼睛节点（异象的「肉体掌控」就是这样），
        /// 所以特性必须按「定义 + 等级」两条键登记，否则只有某个等级该上色时，
        /// 其它等级的角色也会被一起改掉瞳色。
        /// </summary>
        public int Degree;

        /// <summary>眼睛叠加贴图路径（男/女），用于取色采样，可能为空。</summary>
        public List<string> TexturePaths = new List<string>();

        /// <summary>
        /// 仅对基因有意义：基因图标路径（GeneDef.iconPath）。
        ///
        /// 用途：没有颜色声明、名字里也没有颜色词的基因，图标是最后一处视觉意图
        /// （例：VRE 的复眼/单眼基因），取色顺序里排在最后，见 EyeColorResolver。
        /// </summary>
        public string IconPath;

        /// <summary>
        /// 来源自己声明的颜色（原版渲染节点的 color / colorType）。
        /// 有值时优先于贴图采样与关键词猜色，见 EyeNodeScanner。
        /// </summary>
        public EyeColorSpec? Declared;

        /// <summary>
        /// 仅对 hediff 有意义：该状态是否会挂在眼球部件上。
        ///
        /// FA 的 hediff 通道写法是「hediff 实例的 Part 必须等于左右眼 BodyPartRecord」，
        /// 所以只有确定装在眼球上的来源才值得注册成 EyeballColorDef。
        /// 判定依据是配方（见 EyeEffectDiscovery.BuildHediffPartVerdicts）：
        /// - 配方明确写在眼球上 → true（原版义眼、FSFVBE 的义眼）；
        /// - 配方明确写在头/脸上的（典型：机械面罩）→ false —— 注册了也匹配不到，是静默漏色；
        /// - 配方没写部位的：只有「加装部件」（addedPartProps 非空，必然绑定某个 Part）才按 true 处理，
        ///   因为那是「没有反证」而不是「反证」；不是加装部件的则是全身状态，
        ///   典型是异象的「虚空之触」——它 Part 为 null，注册了也永远匹配不到 → false。
        ///
        /// 为 false 的来源改走 EyeCompatRegistry.EyeHediffOverrides / GlobalHediffColors，
        /// 在渲染前临时上色 —— 那条路不检查部位，装在头/脸上或全身都同样生效。
        /// </summary>
        public bool AppliesToEyePart;

        /// <summary>
        /// 仅对 hediff 有意义：这是「植入体/装置」还是「疾病类状态」。
        ///
        /// 只影响运行期临时上色的优先级档位（装置比状态更具体），以及日志里挂哪个标签。
        /// 例：机械面罩是装置，异象的「虚空之触」是状态。
        /// </summary>
        public bool IsImplant;

        /// <summary>最终颜色规则，由 EyeColorResolver 填充。</summary>
        public EyeColorSpec Spec;

        /// <summary>颜色来源，仅用于日志：declared / table / texture / keyword。</summary>
        public string ColorOrigin;

        /// <summary>识别依据，仅用于日志：节点 / 配方 / 排除标签 / 名称关键词。</summary>
        public string DetectReason;

        /// <summary>
        /// 这条来源是靠启发式认出来的（名称含眼、配方落在眼球等），而不是靠明确的渲染节点。
        /// 单独标出来是为了让开发者能一眼核查误判，也是在启动日志里汇报「哪部分覆盖是靠猜的」。
        /// </summary>
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
