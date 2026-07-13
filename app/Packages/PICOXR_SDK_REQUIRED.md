# PICO XR SDK required here

This Unity project depends on the **PICO Unity Integration SDK** as an embedded package at:

    app/Packages/com.unity.xr.picoxr/

That folder is **gitignored** (~300 MB, proprietary), so it is **not** in a clone. Unzip the
downloaded SDK (**v3.4.0**) here so that `com.unity.xr.picoxr/package.json` exists, then open `app/`
in Unity **6000.4.10f1**. `Packages/manifest.json` and `packages-lock.json` both reference
`file:com.unity.xr.picoxr`.

Without this the Unity Editor opens in **Safe Mode** with PXR compile errors (the scene, `PXR_*`
assets, and the XR loader all depend on the package).

See [../../docs/UNITY_POSE_SETUP.md](../../docs/UNITY_POSE_SETUP.md) and [../README.md](../README.md)
("Open the cloned project").
