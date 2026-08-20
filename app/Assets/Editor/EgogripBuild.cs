using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Egogrip.EditorTools
{
    /// <summary>
    /// Headless APK build, so the app can be built + deployed from a terminal (or by an agent) without
    /// opening the Editor UI. Unity refuses to open a project twice, so CLOSE the Editor first.
    ///
    ///   ~/Unity/Hub/Editor/6000.4.10f1/Editor/Unity -batchmode -nographics -quit \
    ///     -projectPath app -executeMethod Egogrip.EditorTools.EgogripBuild.BuildApk \
    ///     -androidKeystorePass '<pass>' -androidKeyaliasPass '<pass>' \
    ///     -logFile /tmp/egogrip-build.log
    ///
    /// ⚠ The keystore passwords are REQUIRED. The project signs with {inproject}: user.keystore, and
    /// Unity only holds those passwords in the Editor UI session — batchmode without them fails at
    /// "Prepare For Build" with "Can not sign the application / please provide passwords!", which
    /// BuildPipeline reports as a normal failed build rather than a signing-specific error.
    ///
    /// Writes app/Build/egogrip.apk. Development Build + script debugging stay ON: the HUD and the
    /// ego-camera probe log through Debug.Log, which only reaches logcat (tag "Unity") in a dev build.
    /// </summary>
    public static class EgogripBuild
    {
        private const string Scene = "Assets/Scenes/Capture.unity";
        private const string OutDir = "Build";
        private const string ApkName = "egogrip.apk";

        public static void BuildApk()
        {
            string outPath = Path.Combine(OutDir, ApkName);
            Directory.CreateDirectory(OutDir);

            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            EditorUserBuildSettings.development = true;
            EditorUserBuildSettings.allowDebugging = true;
            EditorUserBuildSettings.buildAppBundle = false;

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = outPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.Development | BuildOptions.AllowDebugging,
            };

            Debug.Log($"egogrip-build: building {outPath} (dev build)");
            BuildReport report = BuildPipeline.BuildPlayer(opts);
            var s = report.summary;
            Debug.Log($"egogrip-build: result={s.result} errors={s.totalErrors} warnings={s.totalWarnings} " +
                      $"size={s.totalSize / (1024 * 1024)}MB time={s.totalTime}");

            if (s.result != BuildResult.Succeeded)
            {
                foreach (var step in report.steps)
                    foreach (var msg in step.messages)
                        if (msg.type == LogType.Error || msg.type == LogType.Exception)
                            Debug.LogError($"egogrip-build: {step.name}: {msg.content}");
                EditorApplication.Exit(1);
            }

            Debug.Log("egogrip-build: OK -> " + Path.GetFullPath(outPath));
            EditorApplication.Exit(0);
        }
    }
}
