using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// The menu uses TextMeshPro (Daydream font). TMP needs its "Essential Resources" (shaders + TMP Settings) inside Assets/.
// This imports them the first time the project opens, and BuildSetup.PreExport calls it for Unity Cloud Build.
// Done by reflection so it never breaks compilation if the TMP version changes.
[InitializeOnLoad]
public static class TmpSetup
{
    static TmpSetup()
    {
        EditorApplication.delayCall += Ensure;
    }

    public static void Ensure()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (File.Exists("Assets/TextMesh Pro/Resources/TMP Settings.asset")) return;
        try
        {
            Type t = Type.GetType("TMPro.TMP_PackageResourceImporter, Unity.TextMeshPro.Editor");
            if (t == null) { Debug.LogWarning("[GtagTest] TMP importer not found; the menu will use the default font."); return; }
            var m = t.GetMethod("ImportResources", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (m != null) m.Invoke(null, new object[] { true, false, false });
            AssetDatabase.Refresh();
            Debug.Log("[GtagTest] Imported TextMeshPro essentials.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[GtagTest] Could not import TMP essentials: " + e.Message);
        }
    }
}
