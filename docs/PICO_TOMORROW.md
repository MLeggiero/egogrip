# PICO setup runbook — get recording fast

Goal: with the PICO for a **brief** window tomorrow, sideload the native capture app and record
a real episode from the **UVC camera + RP2040 serial** through the **powered USB-C hub**.

You said *nothing is installed yet* — so the make-or-break move is **do everything in "Tonight"
below before tomorrow**, especially the **first Gradle build** (it downloads ~1 GB of Gradle +
Android SDK + dependencies the first time). If you walk in tomorrow with the app already built
once, tomorrow is just *plug in → install → record*.

---

## ⚠️ The single-port problem (read this first)

The PICO has **one USB-C port**. Tomorrow it's occupied by the **hub** (camera + RP2040 +
charging). So you **cannot** also use that port for `adb install` at the same time.
**Solution: wireless ADB.** Set it up tonight; then tomorrow you install/iterate over Wi-Fi
while the hub stays plugged in. Steps are in "Tonight → 4".

---

## Tonight (do all of this)

### 1. Install Android Studio + SDK  (~30–60 min, mostly download)
- Install **Android Studio** (latest stable). On first run let it install the **Android SDK**,
  **SDK Platform 34**, and **Platform-Tools** (gives you `adb`).
- Verify `adb`:
  ```bash
  adb version
  ```
  If not on PATH, it's at `~/Android/Sdk/platform-tools/adb` (Linux/Mac).

### 2. Open the egogrip native app + build once  (CRITICAL — pre-downloads everything)
- In Android Studio: **Open** → select `egogrip/app-native`.
- Let it sync Gradle (it will download Gradle 8.7 + AGP + dependencies — this is the big one).
- **Build → Make Project** (or `./gradlew assembleDebug` from `app-native/`). Get a green build
  **tonight**. If a dependency version fails to resolve, see
  [app-native/README.md](../app-native/README.md#troubleshooting).

### 3. Put the PICO in developer mode + enable USB debugging
- PICO: **Settings → General → About → Software version**, tap it repeatedly to unlock
  **Developer**. (Enterprise units may enable it via the admin console / ArborXR instead.)
- **Settings → Developer** → enable **USB Debugging**.
- Plug the PICO into your PC with its USB-C cable, put the headset on, **Allow USB debugging**
  when prompted. Confirm:
  ```bash
  adb devices          # should list the PICO as "device"
  ```

### 4. Set up wireless ADB (so the hub can stay plugged tomorrow)
With the PICO still on USB:
```bash
adb tcpip 5555
adb shell ip route          # note the headset's Wi-Fi IP (same Wi-Fi as your PC)
adb connect <PICO_IP>:5555  # now adb works over Wi-Fi
adb devices                 # should show <PICO_IP>:5555  device
```
Unplug USB — `adb devices` should still show it. (Android 11+ also has **Wireless debugging**
with pairing codes under Developer settings; either path is fine.) **Test installing tonight:**
```bash
adb install -r app-native/app/build/outputs/apk/debug/app-debug.apk
```
The app **egogrip Capture** should appear in the PICO library and launch (as a 2D panel).

### 5. Flash the RP2040 (no toolchain needed — CircuitPython)
See [firmware/rp2040-gripper/circuitpython/README.md](../firmware/rp2040-gripper/circuitpython/README.md):
1. Hold **BOOTSEL**, plug the RP2040 into your PC → a `RPI-RP2` drive appears.
2. Drag the **CircuitPython UF2** onto it (downloaded once from circuitpython.org).
3. It remounts as `CIRCUITPY`; copy our [`code.py`](../firmware/rp2040-gripper/circuitpython/code.py)
   onto it. It starts streaming immediately.
- Sanity check on your PC: open the RP2040's serial port (115200) in any serial monitor — you'll
  see framed binary (or set `DEBUG_ASCII=True` in `code.py` to print readable lines).

### 6. Pre-stage the hub
- Confirm the **powered** hub actually charges the headset *and* exposes devices: plug hub →
  PICO, plug RP2040 + a USB drive into the hub, and:
  ```bash
  adb shell dumpsys usb | grep -i device   # or check the app's device list
  ```
  (You can do this tonight over wireless adb with the hub on the headset.)

---

## Tomorrow (the brief window)

1. Power the **powered hub**, plug it into the PICO (headset charges through it).
2. Plug the **RP2040** and the **UVC camera** into the hub.
3. `adb connect <PICO_IP>:5555` (reconnect if needed).
4. Launch **egogrip Capture**. You should see the **device list** populate (CDC serial = RP2040;
   UVC = camera). Grant the USB permission prompts.
5. Hit **Start**, move things for ~20–30 s, hit **Stop**.
6. Pull the episode:
   ```bash
   adb pull /sdcard/Android/data/org.egogrip.capture/files/episodes ./pulled
   ```
7. Validate + (optionally) export on your PC:
   ```bash
   cd egogrip/pipeline && pip install -e .
   egogrip-validate ../pulled/<episode_id>
   ```
   Note: native episodes tomorrow have **no gripper pose** yet (pose needs the Unity/controller
   phase), so `egogrip-export` (which needs a pose stream) won't run on them — that's expected.
   Tomorrow is about proving **capture**: serial + camera + on-device episode writing.

---

## Success criteria for tomorrow
- [ ] App lists the RP2040 (CDC) and the UVC camera.
- [ ] Start/Stop writes an episode folder on the headset.
- [ ] `gripper_state.csv` / `tactile.csv` fill with RP2040 samples (with `mcu_micros`).
- [ ] `wrist0.mp4` + `wrist0_frames.csv` exist (camera; stretch — serial alone is still a win).
- [ ] `egogrip-validate` passes on the pulled episode.

If the camera fights you, **don't burn the window on it** — serial + episode writing on real
hardware is the core result. UVC is marked optional in the app for exactly this reason.
