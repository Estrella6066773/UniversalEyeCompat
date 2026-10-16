using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace UniversalEyeCompat
{
    /// <summary>
    /// 眼睛叠加贴图的取色器：把一张眼贴图压成一个能用的瞳色。
    ///
    /// 数据流向：EyeColorResolver 给出 texPath → 本类型找文件 → 解像素 → 交给 DominantColor 求主色。
    ///
    /// 两条取像素的路：
    /// 1. 先试 ContentFinder 拿 Texture2D 再 GetPixels32，这条路在部分平台/压缩设置下会失败；
    /// 2. 失败后退回自己找模组目录里的 .png 文件并用 PngReader 解码。
    /// 两条都失败才返回 false，由调用方改用名称关键词兜底。
    ///
    /// 为什么要挑主色而不是直接求平均：
    /// 眼贴图里通常同时有白色眼白、黑色瞳孔、抗锯齿边缘，直接求平均会把虹膜色拉淡拉灰
    /// （黄色眼会算成土黄）。主色算法见 DominantColor，那个文件不依赖游戏类型，可离线测试。
    ///
    /// 注意事项：
    /// - 贴图路径形如 Things/Pawn/Humanlike/HeadAttachments/RedEyes/Male/RedEyes_Male，不含扩展名；
    ///   文件一定位于某个启用模组的 Textures/ 目录下（Core 在 Data/Core/Textures）。
    /// - 原版 1.7 的贴图已经打进 AssetBundle，没有散装文件，采样会失败并自动落到名称关键词兜底。
    /// - 本类型只做「取主色」；提饱和度由 EyeColorResolver 统一做一次，避免重复叠加。
    /// </summary>
    internal static class TextureColorSampler
    {
        private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".dds", ".psd" };

        private static readonly Dictionary<string, Color?> Cache = new Dictionary<string, Color?>();

        /// <summary>对一组贴图路径取色；全部失败返回 false。</summary>
        public static bool TrySample(IEnumerable<string> texPaths, out Color color)
        {
            color = Color.white;
            List<Color> colors = new List<Color>();
            if (texPaths != null)
            {
                foreach (string texPath in texPaths)
                {
                    if (string.IsNullOrEmpty(texPath))
                    {
                        continue;
                    }
                    foreach (string candidate in ExpandPlaceholder(texPath))
                    {
                        if (TrySampleSingle(candidate, out Color sampled))
                        {
                            colors.Add(sampled);
                            break;
                        }
                    }
                }
            }
            if (colors.Count == 0)
            {
                return false;
            }
            // 男 / 女两张贴图先各自取主色，再求平均；提饱和度交给 EyeColorResolver。
            float r = 0f;
            float g = 0f;
            float b = 0f;
            for (int i = 0; i < colors.Count; i++)
            {
                r += colors[i].r;
                g += colors[i].g;
                b += colors[i].b;
            }
            color = new Color(r / colors.Count, g / colors.Count, b / colors.Count, 1f);
            return true;
        }

        /// <summary>
        /// FA 风格的贴图路径里带 {0} 占位（朝向或部件名），这里只试几种最常见的展开。
        /// </summary>
        private static IEnumerable<string> ExpandPlaceholder(string texPath)
        {
            if (texPath.IndexOf("{0}", StringComparison.Ordinal) < 0)
            {
                yield return texPath;
                yield break;
            }
            yield return texPath.Replace("{0}", "south");
            yield return texPath.Replace("{0}", "normal");
            yield return texPath.Replace("{0}", "Male");
        }

        private static bool TrySampleSingle(string texPath, out Color color)
        {
            color = Color.white;
            if (Cache.TryGetValue(texPath, out Color? cached))
            {
                if (cached.HasValue)
                {
                    color = cached.Value;
                    return true;
                }
                return false;
            }

            Color? result = TrySampleFromGraphic(texPath);
            if (!result.HasValue)
            {
                result = TrySampleFromFile(texPath);
            }
            Cache[texPath] = result;
            if (result.HasValue)
            {
                color = result.Value;
                return true;
            }
            return false;
        }

        /// <summary>第一条路：走游戏已加载的贴图。失败很正常，不要报错。</summary>
        private static Color? TrySampleFromGraphic(string texPath)
        {
            try
            {
                Texture2D texture = ContentFinder<Texture2D>.Get(texPath, false);
                if (texture == null)
                {
                    return null;
                }
                Color32[] pixels = texture.GetPixels32();
                if (pixels == null || pixels.Length == 0)
                {
                    return null;
                }
                // DominantColor 只吃 RGBA 字节流，这里把 Color32 摊平过去，避免维护两份一样的算法。
                int count = pixels.Length;
                byte[] rgba = new byte[count * 4];
                for (int i = 0; i < count; i++)
                {
                    Color32 pixel = pixels[i];
                    int offset = i * 4;
                    rgba[offset] = pixel.r;
                    rgba[offset + 1] = pixel.g;
                    rgba[offset + 2] = pixel.b;
                    rgba[offset + 3] = pixel.a;
                }
                return FromDominant(rgba, count);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>第二条路：自己找 .png 并解码。</summary>
        private static Color? TrySampleFromFile(string texPath)
        {
            string file = FindTextureFile(texPath);
            if (file == null || !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            if (!PngReader.TryRead(file, out int width, out int height, out byte[] rgba))
            {
                return null;
            }
            return FromDominant(rgba, width * height);
        }

        private static Color? FromDominant(byte[] rgba, int pixelCount)
        {
            if (!DominantColor.TryFind(rgba, pixelCount, out float r, out float g, out float b))
            {
                return null;
            }
            return new Color(r, g, b, 1f);
        }

        /// <summary>
        /// 在所有启用模组的 Textures/ 目录里找贴图文件。
        ///
        /// 用 ModContentPack.foldersToLoadDescendingOrder 而不是 RootDir：
        /// 许多模组把内容放在版本子目录（1.6/、1.7/）或 Common/ 下，只有这份有序列表才是游戏真正会读的位置。
        /// Core 同样是列表中的一项（指向 Data/Core），所以原版贴图也能命中 —— 不过 1.7 起原版贴图已打进
        /// AssetBundles，通常找不到散装文件，这种情况会自动落到名称关键词兜底。
        /// </summary>
        private static string FindTextureFile(string texPath)
        {
            string relative = texPath.Replace('/', Path.DirectorySeparatorChar);
            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            for (int i = 0; i < mods.Count; i++)
            {
                ModContentPack mod = mods[i];
                List<string> folders = mod.foldersToLoadDescendingOrder;
                if (folders == null)
                {
                    continue;
                }
                for (int f = 0; f < folders.Count; f++)
                {
                    for (int e = 0; e < Extensions.Length; e++)
                    {
                        string candidate = Path.Combine(folders[f], "Textures", relative + Extensions[e]);
                        try
                        {
                            if (File.Exists(candidate))
                            {
                                return candidate;
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            return null;
        }
    }
}
