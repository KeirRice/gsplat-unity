// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Gsplat.Editor
{
    [ScriptedImporter(1, "ply")]
    public class GsplatImporter : ScriptedImporter
    {
        public CompressionMode Compression = CompressionMode.Spark;

        [Tooltip("The coordinate frame the source asset was authored in.\n\n" +
                 "Positions, rotations, and SH coefficients are converted to Unity (RUF) at import time.\n\n" +
                 "RUB  — standard output of 3DGS training tools, gsplat, nerfstudio, and Niantic SPZ.\n" +
                 "RDF  — OpenCV, COLMAP camera convention.\n" +
                 "LUF  — GLB, glTF.\n" +
                 "RUF  — already in Unity space; no conversion applied.")]
        public SourceCoordinates SourceCoordinates = SourceCoordinates.RUB;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            GsplatAsset gsplatAsset = Compression switch
            {
                CompressionMode.Uncompressed => ScriptableObject.CreateInstance<GsplatAssetUncompressed>(),
                CompressionMode.Spark => ScriptableObject.CreateInstance<GsplatAssetSpark>(),
                _ => throw new ArgumentOutOfRangeException()
            };

            try
            {
                gsplatAsset.LoadFromPly(ctx.assetPath,
                    (info, progress) => EditorUtility.DisplayProgressBar("Importing Gsplat Asset", info, progress),
                    SourceCoordinates);
            }
            catch (Exception e)
            {
                if (GsplatSettings.Instance.ShowImportErrors)
                    Debug.LogError($"{ctx.assetPath} import error: {e.Message}");
                return;
            }

            ctx.AddObjectToAsset("gsplatAsset", gsplatAsset);
            ctx.SetMainObject(gsplatAsset);
        }
    }


    public class GsplatReferenceRestorer : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            var plyReimported = importedAssets.Any(path => path.EndsWith(".ply", StringComparison.OrdinalIgnoreCase));
            if (!plyReimported) return;

            var renderers = UnityEngine.Object.FindObjectsByType<GsplatRenderer>(FindObjectsSortMode.None);
            foreach (var renderer in renderers)
            {
                if (renderer.GsplatAsset || string.IsNullOrEmpty(renderer.AssetGuid)) continue;
                var path = AssetDatabase.GUIDToAssetPath(renderer.AssetGuid);
                if (string.IsNullOrEmpty(path)) continue;
                var asset = AssetDatabase.LoadAssetAtPath<GsplatAsset>(path);
                if (!asset) continue;
                renderer.GsplatAsset = asset;
                renderer.ReloadAsset();
                EditorUtility.SetDirty(renderer);
            }
        }
    }
}