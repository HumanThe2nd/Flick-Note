using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Creates the demo scene (menu: Glove > Build Rhythm Scene) and builds a
/// Windows player (Glove > Build Windows Player).
///
/// Also callable from the command line:
///   Unity.exe -batchmode -projectPath . -executeMethod SceneBuilder.BuildSceneBatch -quit
///   Unity.exe -batchmode -projectPath . -executeMethod SceneBuilder.BuildPlayerBatch -quit
/// </summary>
public static class SceneBuilder
{
    public const string ScenePath = "Assets/Scenes/FlickNote.unity";
    public const string PlayerPath = "Build/FlickNote.exe";

    [MenuItem("Glove/Build Rhythm Scene")]
    public static void BuildScene()
    {
        Directory.CreateDirectory("Assets/Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Camera at seated eye height, looking forward.
        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        var cam = camGo.AddComponent<Camera>();
        cam.transform.position = new Vector3(0f, 1.4f, 0f);
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Theme.Background;
        cam.nearClipPlane = 0.05f;
        cam.fieldOfView = 70f;
        camGo.AddComponent<AudioListener>();

        // Shoulder for the arm model: below, right of and slightly in front of the
        // camera (right hand), so the hand shows in the lower right of the view.
        var shoulder = new GameObject("Shoulder").transform;
        shoulder.SetParent(camGo.transform, false);
        shoulder.localPosition = new Vector3(0.27f, -0.22f, 0.25f);

        // A material asset referenced by the scene, so its shader is included in
        // builds (runtime-created objects copy and tint it).
        var material = CreateBaseMaterial();
        var unlit = CreateMaterial("Assets/Materials/GloveUnlit.mat", "GloveUnlit", "Unlit/Color");
        var glow = CreateMaterial("Assets/Materials/GloveGlow.mat", "GloveGlow",
                                  "Legacy Shaders/Particles/Additive", "Particles/Standard Unlit", "Sprites/Default");

        var light = new GameObject("Key Light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = Theme.KeyLight;
        light.intensity = 1.0f;
        light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);

        // Red light from in front and below, catching the glove's edges.
        var rim = new GameObject("Rim Light").AddComponent<Light>();
        rim.type = LightType.Directional;
        rim.color = Theme.RimLight;
        rim.intensity = 1.3f;
        rim.transform.rotation = Quaternion.Euler(-20f, 170f, 0f);

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = Theme.Ambient;

        var gloveGo = new GameObject("Glove");
        var receiver = gloveGo.AddComponent<GloveReceiver>();

        var handGo = new GameObject("Hand");
        var hand = handGo.AddComponent<GloveHand>();
        hand.glove = receiver;
        hand.shoulder = shoulder;
        hand.baseMaterial = material;
        hand.accentMaterial = unlit;

        var gameGo = new GameObject("RhythmGame");
        var game = gameGo.AddComponent<RhythmGame>();
        game.glove = receiver;
        game.hand = hand;
        game.baseMaterial = material;
        game.unlitMaterial = unlit;
        game.glowMaterial = glow;

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

        // Keep running (and receiving glove data) when the window isn't focused,
        // e.g. while you're looking at the bridge's terminal.
        PlayerSettings.runInBackground = true;
        PlayerSettings.productName = "Flick Note";
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;
        AssetDatabase.SaveAssets();
        Debug.Log($"SceneBuilder: saved {ScenePath}");
    }

    private static Material CreateBaseMaterial()
    {
        const string path = "Assets/Materials/GloveBase.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;
        Directory.CreateDirectory("Assets/Materials");
        var shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit")
                     ?? AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat").shader;
        var mat = new Material(shader) { name = "GloveBase", color = Color.white };
        mat.SetFloat("_Glossiness", 0.3f);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    // Material using the first of the given shaders that exists.
    private static Material CreateMaterial(string path, string name, params string[] shaderNames)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        Shader shader = null;
        foreach (var s in shaderNames)
            if ((shader = Shader.Find(s)) != null) break;
        if (shader == null) { Debug.LogError($"SceneBuilder: none of {string.Join(", ", shaderNames)} found"); return null; }
        var mat = new Material(shader) { name = name };
        AssetDatabase.CreateAsset(mat, path);
        Debug.Log($"SceneBuilder: {name} uses shader {shader.name}");
        return mat;
    }

    [MenuItem("Glove/Build Windows Player")]
    public static void BuildPlayer()
    {
        if (!File.Exists(ScenePath)) BuildScene();
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = PlayerPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        });
        Debug.Log($"SceneBuilder: player build {report.summary.result}, " +
                  $"{report.summary.totalErrors} errors -> {PlayerPath}");
        if (Application.isBatchMode && report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }

    public static void BuildSceneBatch() => BuildScene();

    public static void BuildPlayerBatch()
    {
        BuildScene();
        BuildPlayer();
    }
}
