using System.Globalization;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Weft.Samples
{
    /// <summary>
    /// The demo: a full-screen WeftView with the demo page, a turning cube the page labels by an anchor, and the game's
    /// side of the page's messages (a click it acknowledges, a call it completes, its state as a view model). Clicks the
    /// page does not take (WeftView.PointerOverPage false) are the game's: the cube changes colour.
    /// </summary>
    public sealed class WeftDemo : MonoBehaviour
    {
        [Tooltip("The page's folder, relative to StreamingAssets (or absolute). Empty: the Page folder beside this script " +
                 "in the editor, StreamingAssets/WeftDemo in a player.")]
        [SerializeField] string page = string.Empty;

        [Tooltip("Device px per CSS px.")]
        [Min(0.25f)]
        [SerializeField] float scale = 1;

        WeftView view;
        Transform cube;
        Material cubeMaterial;
        int gameClicks;
        int frames;
        float counted;
        float fps;
        readonly WeftAnchor[] anchors = new WeftAnchor[1];

        void Start()
        {
            var view3d = Camera.main;
            if (!view3d)
            {
                view3d = new GameObject("Main Camera") {tag = "MainCamera"}.AddComponent<Camera>();
                view3d.transform.position = new Vector3(0, 1, -10);
            }
            // A cube of the built-in mesh, without a collider (the project may have no physics module), in the render
            // pipeline's default material.
            var cubeObject = new GameObject("Demo Cube");
            cubeObject.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            cubeMaterial = pipeline ? new Material(pipeline.defaultMaterial) : new Material(Shader.Find("Standard"));
            cubeObject.AddComponent<MeshRenderer>().sharedMaterial = cubeMaterial;
            cube = cubeObject.transform;
            cube.position = view3d.transform.position + view3d.transform.forward * 7 + view3d.transform.right * 3f;
            view = gameObject.AddComponent<WeftView>();
            view.Scale = scale;
            view.MessageReceived += OnMessage;
            view.Load(WeftPage.FromDirectory(PageDirectory()));
        }

        void Update()
        {
            cube.Rotate(new Vector3(15, 40, 0) * Time.deltaTime, Space.World);
            if (ClickedThisFrame() && !view.PointerOverPage)
            {
                ++gameClicks;
                cubeMaterial.color = Color.HSVToRGB(gameClicks * 0.17f % 1, 0.65f, 0.95f);
                view.Emit("gameClick", $"{{\"count\":{gameClicks}}}");
            }
            ++frames;
            counted += Time.unscaledDeltaTime;
            if (counted >= 0.25f)
            {
                fps = frames / counted;
                frames = 0;
                counted = 0;
                SendState();
            }
        }

        void LateUpdate()
        {
            // The label stands on the cube's top, wherever the camera sees it.
            var screen = Camera.main.WorldToScreenPoint(cube.position + Vector3.up * 0.9f);
            anchors[0] = new WeftAnchor(1, screen.x / view.Scale, (Screen.height - screen.y) / view.Scale, 1, screen.z,
                screen.z < 0 ? WeftAnchorFlags.Behind : WeftAnchorFlags.Visible);
            view.WriteAnchors(anchors);
        }

        void OnMessage(WeftMessage message)
        {
            switch (message.Name)
            {
                case "clicked":
                    // The page's button: acknowledged with its count.
                    view.Emit("ack", message.Json);
                    break;
                case "greet":
                    view.CompleteCall(message.CallId, true, Quote($"Unity {Application.unityVersion} 回答了"));
                    break;
            }
        }

        void SendState()
        {
            var json = string.Format(CultureInfo.InvariantCulture,
                "{{\"api\":{0},\"fps\":{1:0},\"scale\":{2},\"overPage\":{3},\"cursor\":{4},\"gameClicks\":{5}}}",
                Quote(SystemInfo.graphicsDeviceType.ToString()), fps, view.Scale, view.PointerOverPage ? "true" : "false", Quote(view.Cursor),
                gameClicks);
            view.SetModel("game", json);
        }

        string PageDirectory()
        {
            if (!string.IsNullOrEmpty(page))
            {
                return page;
            }
#if UNITY_EDITOR
            var script = AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(this));
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(script) ?? string.Empty, "Page"));
#else
            return "WeftDemo";
#endif
        }

        static bool ClickedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null && mouse.leftButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(0);
#endif
        }

        // A JSON string.
        static string Quote(string text)
        {
            return "\"" + (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
