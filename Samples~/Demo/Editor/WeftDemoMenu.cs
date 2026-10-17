using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Weft.Samples
{
    // Weft > 打开演示: a new scene with the demo in it, in play mode.
    static class WeftDemoMenu
    {
        [MenuItem("Weft/打开演示（新场景并进入 Play）")]
        static void Open()
        {
            if (EditorApplication.isPlaying || !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            new GameObject("Weft Demo").AddComponent<WeftDemo>();
            EditorApplication.EnterPlaymode();
        }

        [MenuItem("Weft/打开演示（新场景并进入 Play）", true)]
        static bool CanOpen()
        {
            return !EditorApplication.isPlaying;
        }
    }
}
