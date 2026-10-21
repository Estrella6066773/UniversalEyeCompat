using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

namespace UniversalEyeColorPatch
{
    /// <summary>
    /// 扫描 Def 数据库，找出「会改变角色眼部外观」的来源。
    ///
    /// 能识别多少种来源，就代表 FA 的瞳色着色能覆盖多少个第三方模组。
    /// 因此识别口径刻意做宽，每条来源都会记下「靠什么认出来的」（EyeSource.DetectReason）。
    ///
    /// 识别口径：
    /// A. 通用：任何 Def 类型，只要它的 renderNodeProperties 里含眼部节点；
    /// B. 特性：TraitDef.degreeDatas 里某个等级含眼睛节点，按等级逐条产出；
    /// C. 种族：ThingDef.race.renderTree 的节点树里含眼睛节点；
    /// D. 基因补充：exclusionTags 里有眼睛相关标签（这类基因靠互斥标签管瞳色，本身不画节点）；
    /// E. hediff 补充 1：某条 RecipeDef 写在眼球上且 addsHediff 指向它；
    /// F. hediff 补充 2：addedPartProps 非空且名字明显是眼睛。
    ///
    /// 只做只读扫描，不修改任何 Def；写操作全部集中在 EyeColorDefInjector。
    /// </summary>
    public static class EyeSourceDiscovery
    {
        /// <summary>某条 hediff 到底装在哪：配方说了算。</summary>
        private enum PartVerdict
        {
            /// <summary>没有任何配方给出部位信息。无证据 ≠ 反证。</summary>
            Unknown,

            /// <summary>有配方把它装在眼球上。</summary>
            Eye,

            /// <summary>有配方把它装在别的部位上（典型：面罩装在头/颅骨上）。</summary>
            Other
        }

        /// <summary>「renderNodeProperties」属性缓存，按 Def 类型存，避免每个 def 都反射一次。</summary>
        private static readonly Dictionary<Type, PropertyInfo> RenderPropertiesCache = new Dictionary<Type, PropertyInfo>();

        public static List<EyeSource> Discover(out int unsupportedTypes, out List<string> notes)
        {
            List<EyeSource> result = new List<EyeSource>();
            notes = new List<string>();
            int unsupported = 0;
            HashSet<string> seenHediffNames = new HashSet<string>();
            HashSet<string> seenGeneNames = new HashSet<string>();

            Dictionary<string, PartVerdict> partVerdicts = BuildHediffPartVerdicts();

            ScanRenderNodeDefs(result, seenGeneNames, seenHediffNames, partVerdicts, ref unsupported, notes);
            ScanTraits(result);
            ScanRaces(result, notes);
            ScanGenesByExclusionTag(result, seenGeneNames);
            ScanHediffsByRecipe(result, seenHediffNames);
            ScanHediffsByAddedPartName(result, seenHediffNames, notes);

            unsupportedTypes = unsupported;
            return result;
        }

        /// <summary>
        /// 先算「每条 hediff 装在哪」。
        ///
        /// FA 的 hediff 通道匹配条件是 hediff.Part 正好等于左右眼部件，
        /// 所以一条 hediff 值不值得注册成 EyeballColorDef，完全取决于它装在哪。
        /// 装错地方的定义永远不会生效 —— 是「静默漏色」，最坏的一种失败。
        ///
        /// 判定规则：
        /// - 有配方把它装在眼球上 → Eye；
        /// - 有配方明确把它装在别处（面罩装头、颅骨等）→ Other；
        /// - 配方没限定部位（模块化升级、removesHediff 换装之类）→ Unknown，按没有反证处理。
        /// </summary>
        private static Dictionary<string, PartVerdict> BuildHediffPartVerdicts()
        {
            Dictionary<string, PartVerdict> verdicts = new Dictionary<string, PartVerdict>();
            foreach (RecipeDef recipe in DefDatabase<RecipeDef>.AllDefs)
            {
                HediffDef added = recipe.addsHediff;
                if (added == null || recipe.appliedOnFixedBodyParts.NullOrEmpty())
                {
                    continue;
                }
                bool onEye = IsAppliedOnEye(recipe);
                PartVerdict verdict = onEye ? PartVerdict.Eye : PartVerdict.Other;

                if (verdicts.TryGetValue(added.defName, out PartVerdict existing))
                {
                    // 同一条 hediff 若有多种装法，只要有一种落在眼球上就按 Eye 算。
                    if (existing == PartVerdict.Eye)
                    {
                        continue;
                    }
                    verdicts[added.defName] = onEye ? PartVerdict.Eye : PartVerdict.Other;
                }
                else
                {
                    verdicts.Add(added.defName, verdict);
                }
            }
            return verdicts;
        }

        private static PartVerdict GetVerdict(Dictionary<string, PartVerdict> verdicts, HediffDef hediff)
        {
            if (hediff != null && verdicts.TryGetValue(hediff.defName, out PartVerdict verdict))
            {
                return verdict;
            }
            return PartVerdict.Unknown;
        }

        /// <summary>
        /// A. 通用扫描：遍历所有「有 Def 数据库」的类型，凡是带 renderNodeProperties 属性的，
        /// 就把它的 def 拿出来看有没有眼睛节点。
        ///
        /// 用 GenDefDatabase.AllDefTypesWithDatabases 而不是写死 GeneDef / HediffDef：
        /// 写死的话，第三方模组自定义的 Def 类型（自带眼部节点的那些）永远扫不到。
        /// </summary>
        private static void ScanRenderNodeDefs(
            List<EyeSource> result,
            HashSet<string> seenGeneNames,
            HashSet<string> seenHediffNames,
            Dictionary<string, PartVerdict> partVerdicts,
            ref int unsupportedTypes,
            List<string> notes)
        {
            foreach (Type defType in GenDefDatabase.AllDefTypesWithDatabases())
            {
                PropertyInfo renderProperties = GetRenderPropertiesProperty(defType);
                if (renderProperties == null)
                {
                    continue;
                }

                bool reportedUnsupported = false;
                foreach (Def def in GenDefDatabase.GetAllDefsInDatabaseForDef(defType))
                {
                    List<PawnRenderNodeProperties> nodes = ReadRenderProperties(renderProperties, def);
                    if (!EyeNodeScanner.HasEyeNode(nodes))
                    {
                        continue;
                    }

                    if (def is GeneDef gene)
                    {
                        if (seenGeneNames.Add(gene.defName))
                        {
                            EyeColorSpec? declared = null;
                            EyeSource source = new EyeSource
                            {
                                Kind = EyeSourceKind.Gene,
                                SourceDef = gene,
                                Key = gene.defName,
                                IconPath = gene.iconPath,
                                DetectReason = "眼部渲染节点"
                            };
                            EyeNodeScanner.CollectEyeData(nodes, source.TexturePaths, ref declared);
                            source.Declared = declared;
                            source.Elf = EyeColorTables.IsElfSource(gene.defName, source.TexturePaths);
                            result.Add(source);
                        }
                        continue;
                    }

                    if (def is HediffDef hediff)
                    {
                        if (seenHediffNames.Add(hediff.defName))
                        {
                            EyeColorSpec? declared = null;
                            PartVerdict verdict = GetVerdict(partVerdicts, hediff);
                            bool isAddedPart = hediff.addedPartProps != null;

                            // 只有「能确定这条 hediff 会挂在眼球部件上」才敢注册成 FA 的 EyeballColorDef，
                            // 否则改走运行期临时上色 —— 那条路不看部位。
                            bool onEye = verdict == PartVerdict.Eye
                                || (verdict == PartVerdict.Unknown && isAddedPart);

                            EyeSource source = new EyeSource
                            {
                                Kind = EyeSourceKind.Hediff,
                                SourceDef = hediff,
                                Key = hediff.defName,
                                AppliesToEyePart = onEye,
                                IsImplant = isAddedPart,
                                DetectReason = verdict == PartVerdict.Other
                                    ? "眼部渲染节点（配方显示装在非眼球部位）"
                                    : "眼部渲染节点"
                            };
                            EyeNodeScanner.CollectEyeData(nodes, source.TexturePaths, ref declared);
                            source.Declared = declared;
                            source.Elf = EyeColorTables.IsElfSource(hediff.defName, source.TexturePaths);
                            result.Add(source);

                            if (!onEye)
                            {
                                notes.Add("hediff " + hediff.defName + " 有眼部叠加节点，但"
                                    + (verdict == PartVerdict.Other ? "配方把它装在非眼球部位" : "它不是加装部件（可能在身上，Part 为 null）")
                                    + "，已改走运行期临时上色（FA 的 hediff 通道要求 hediff 挂在眼球部件上）。");
                            }
                        }
                        continue;
                    }

                    // 别的 Def 类型也带眼睛节点，但没有「装在某个人身上」的语义，
                    // 无法按 pawn 匹配，只能记一笔，方便以后有人反馈漏色时知道该往哪扩。
                    if (!reportedUnsupported)
                    {
                        reportedUnsupported = true;
                        unsupportedTypes++;
                        notes.Add("未支持的来源类型 " + defType.FullName + "（首个例子：" + def.defName + "）");
                    }
                }
            }
        }

        /// <summary>
        /// C. 种族自带的眼睛：写在 ThingDef.race.renderTree 里的眼睛节点，全族通用。
        ///
        /// 原版人类没有种族级眼睛节点（眼睛是头部贴图的一部分），
        /// 所以这里命中的基本全是第三方种族模组。
        /// </summary>
        private static void ScanRaces(List<EyeSource> result, List<string> notes)
        {
            foreach (ThingDef thing in DefDatabase<ThingDef>.AllDefs)
            {
                PawnRenderTreeDef tree = thing.race?.renderTree;
                if (tree == null || !EyeNodeScanner.HasEyeNodeInTree(tree.root))
                {
                    continue;
                }
                EyeColorSpec? declared = null;
                EyeSource source = new EyeSource
                {
                    Kind = EyeSourceKind.Race,
                    SourceDef = thing,
                    Key = thing.defName,
                    DetectReason = "种族渲染树眼部节点"
                };
                // 连根节点一起丢进去：万一有模组把眼睛直接放在渲染树根上，也不会漏掉它自己的颜色声明。
                EyeNodeScanner.CollectEyeData(new List<PawnRenderNodeProperties> { tree.root }, source.TexturePaths, ref declared);
                source.Declared = declared;
                result.Add(source);

                if (!declared.HasValue)
                {
                    notes.Add("种族 " + thing.defName + " 有眼部节点但没声明颜色，已跳过（不按贴图取色，"
                        + "否则会把该种族每只 pawn 的瞳色统一覆盖成贴图主色）。");
                }
            }
        }

        /// <summary>
        /// D. 基因：靠互斥标签认出来的瞳色基因。原版 GeneEyeColor 这类基因用标签管互斥，
        /// 本身不一定画眼睛节点，光看节点会漏。
        /// </summary>
        private static void ScanGenesByExclusionTag(List<EyeSource> result, HashSet<string> seenGeneNames)
        {
            if (!ModsConfig.BiotechActive)
            {
                return;
            }
            foreach (GeneDef gene in DefDatabase<GeneDef>.AllDefs)
            {
                if (gene.exclusionTags == null || !HasEyeTag(gene.exclusionTags))
                {
                    continue;
                }
                if (!seenGeneNames.Add(gene.defName))
                {
                    continue;
                }
                EyeSource source = new EyeSource
                {
                    Kind = EyeSourceKind.Gene,
                    SourceDef = gene,
                    Key = gene.defName,
                    IconPath = gene.iconPath,
                    DetectReason = "眼睛相关互斥标签",
                    Heuristic = true
                };
                source.Elf = EyeColorTables.IsElfSource(gene.defName, source.TexturePaths);
                result.Add(source);
            }
        }

        private static bool HasEyeTag(List<string> tags)
        {
            for (int i = 0; i < tags.Count; i++)
            {
                string tag = tags[i];
                if (!string.IsNullOrEmpty(tag) && tag.IndexOf("eye", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// E. hediff：没有画贴图的眼部植入体，从配方反推。
        /// 配方写在眼球上，装出来一定挂在眼球部件，因此这条来源可以直接注册成 EyeballColorDef。
        /// </summary>
        private static void ScanHediffsByRecipe(List<EyeSource> result, HashSet<string> seenHediffNames)
        {
            foreach (RecipeDef recipe in DefDatabase<RecipeDef>.AllDefs)
            {
                HediffDef added = recipe.addsHediff;
                if (added == null || !IsAppliedOnEye(recipe))
                {
                    continue;
                }
                if (!seenHediffNames.Add(added.defName))
                {
                    continue;
                }
                result.Add(new EyeSource
                {
                    Kind = EyeSourceKind.Hediff,
                    SourceDef = added,
                    Key = added.defName,
                    AppliesToEyePart = true,
                    IsImplant = true,
                    DetectReason = "眼球手术配方",
                    Heuristic = true
                });
            }
        }

        /// <summary>
        /// F. hediff：既没有渲染节点、也没有配方信息的眼部植入体，用名字兜底。
        ///
        /// 判据是 addedPartProps 非空（确实是「替换部件」的植入体）加上名字里有 eye 之类的词。
        /// 名字匹配是启发式，会误判「名字带 eye 但装在别处」的植入体，
        /// 所以这条来源一律按「部位未证明」处理，改走运行期临时上色。
        /// </summary>
        private static void ScanHediffsByAddedPartName(List<EyeSource> result, HashSet<string> seenHediffNames, List<string> notes)
        {
            foreach (HediffDef hediff in DefDatabase<HediffDef>.AllDefs)
            {
                if (hediff.addedPartProps == null)
                {
                    continue;
                }
                if (!EyeNodeScanner.LooksLikeEyeName(hediff.defName) && !EyeNodeScanner.LooksLikeEyeName(hediff.label))
                {
                    continue;
                }
                if (!seenHediffNames.Add(hediff.defName))
                {
                    continue;
                }
                result.Add(new EyeSource
                {
                    Kind = EyeSourceKind.Hediff,
                    SourceDef = hediff,
                    Key = hediff.defName,
                    AppliesToEyePart = false,
                    IsImplant = true,
                    DetectReason = "植入体名称含眼",
                    Heuristic = true
                });
                notes.Add("hediff " + hediff.defName + " 按名称识别为眼部植入体（启发式，可能有误判）。");
            }
        }

        private static bool IsAppliedOnEye(RecipeDef recipe)
        {
            List<BodyPartDef> parts = recipe.appliedOnFixedBodyParts;
            if (parts == null)
            {
                return false;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                BodyPartDef part = parts[i];
                // 原版眼球是 BodyParts_General.xml 里的 Eye，左右眼共用同一定义；
                // 第三方种族常用 LeftEye / RightEye / EyeSingle 之类的命名，统一按名字含 eye 判定。
                if (part != null && EyeNodeScanner.LooksLikeEyeName(part.defName))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// B. 特性：逐等级扫描。
        ///
        /// 判据与 DynamicPawnRenderNodeSetup_Traits 对齐（它负责真正把这些节点挂上渲染树）：
        /// 先看 HasDefinedGraphicProperties，再看 RenderNodeProperties。
        /// </summary>
        private static void ScanTraits(List<EyeSource> result)
        {
            foreach (TraitDef trait in DefDatabase<TraitDef>.AllDefs)
            {
                List<TraitDegreeData> degrees = trait.degreeDatas;
                if (degrees == null)
                {
                    continue;
                }
                for (int i = 0; i < degrees.Count; i++)
                {
                    TraitDegreeData degree = degrees[i];
                    if (degree == null || !degree.HasDefinedGraphicProperties || !EyeNodeScanner.HasEyeNode(degree.RenderNodeProperties))
                    {
                        continue;
                    }
                    EyeColorSpec? declared = null;
                    EyeSource source = new EyeSource
                    {
                        Kind = EyeSourceKind.Trait,
                        SourceDef = trait,
                        Degree = degree.degree,
                        Key = EyeColorRegistry.TraitKey(trait.defName, degree.degree),
                        DetectReason = "特性等级眼部节点"
                    };
                    EyeNodeScanner.CollectEyeData(degree.RenderNodeProperties, source.TexturePaths, ref declared);
                    source.Declared = declared;
                    result.Add(source);
                }
            }
        }

        /// <summary>
        /// 找某个 Def 类型上的 renderNodeProperties 属性（含继承链）。
        /// 找不到就说明这种 Def 不可能带眼部节点，跳过。
        /// </summary>
        private static PropertyInfo GetRenderPropertiesProperty(Type defType)
        {
            if (defType == null)
            {
                return null;
            }
            if (RenderPropertiesCache.TryGetValue(defType, out PropertyInfo cached))
            {
                return cached;
            }
            PropertyInfo found = null;
            for (Type current = defType; current != null && current != typeof(Def); current = current.BaseType)
            {
                PropertyInfo property = current.GetProperty("RenderNodeProperties",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && typeof(List<PawnRenderNodeProperties>).IsAssignableFrom(property.PropertyType))
                {
                    found = property;
                    break;
                }
            }
            RenderPropertiesCache[defType] = found;
            return found;
        }

        private static List<PawnRenderNodeProperties> ReadRenderProperties(PropertyInfo property, Def def)
        {
            try
            {
                return property.GetValue(def, null) as List<PawnRenderNodeProperties>;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
