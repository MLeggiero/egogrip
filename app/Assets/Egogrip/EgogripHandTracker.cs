using UnityEngine;
#if EGOGRIP_PICO_HANDS
using Unity.XR.PXR;
#endif

namespace Egogrip
{
    /// <summary>
    /// Reads PICO hand tracking (26 OpenXR joints per hand) for the recorder's Hands input mode.
    /// All PICO-SDK-specific symbols are quarantined in this one file so the rest of the project
    /// always compiles regardless of the installed SDK.
    ///
    /// ⚠ BUILD FLAG: the real PXR path is behind the <c>EGOGRIP_PICO_HANDS</c> scripting define
    /// (Project Settings ▸ Player ▸ Scripting Define Symbols). Without it this is an inert stub
    /// (<see cref="Available"/> = false) — so the app builds cleanly out of the box. To turn hand
    /// tracking on:
    ///   1. Enable Hand Tracking in the PICO project settings (PXR_ProjectSetting handTracking = 1).
    ///   2. Add <c>EGOGRIP_PICO_HANDS</c> to the Android scripting define symbols.
    ///   3. Confirm the PXR_HandTracking symbols below against your PICO SDK version (v3.4.0):
    ///      method name <c>GetJointLocations</c>, the <c>HandJointLocations</c>/<c>HandJointLocation</c>
    ///      structs, the <c>pose</c> field, and the <c>Posef</c> Position/Orientation field names.
    ///
    /// Poses are returned in the XR tracking frame (same space as controller devicePosition), so the
    /// recorder logs them raw and declares world_frame = "unity_y_up_lh" exactly like the controller
    /// path — no handedness math here.
    /// </summary>
    public class EgogripHandTracker
    {
        public const int JointCount = 26; // OpenXR XR_HAND_JOINT_COUNT_EXT
        public const int WristJoint = 1;  // OpenXR joint order: 0 = palm, 1 = wrist

        /// <summary>Per-frame snapshot of both hands (each an array of <see cref="JointCount"/> joint poses).</summary>
        public struct HandFrame
        {
            public bool leftTracked, rightTracked;
            public Pose[] left;   // length JointCount when leftTracked
            public Pose[] right;  // length JointCount when rightTracked
        }

        /// <summary>True only when compiled with EGOGRIP_PICO_HANDS — lets the HUD say "build flag off"
        /// vs. "not tracked".</summary>
#if EGOGRIP_PICO_HANDS
        public bool Compiled => true;
#else
        public bool Compiled => false;
#endif

        /// <summary>Hand tracking is on and at least one hand was tracked on the last poll.</summary>
        public bool Available { get; private set; }

        /// <summary>Poll both hands. Returns false (and Available=false) when neither hand is tracked
        /// or the build flag is off.</summary>
        public bool TryGetHands(out HandFrame f)
        {
            f = default;
#if EGOGRIP_PICO_HANDS
            var left = new Pose[JointCount];
            var right = new Pose[JointCount];
            bool lt = ReadHand(HandType.HandLeft, left);
            bool rt = ReadHand(HandType.HandRight, right);
            Available = lt || rt;
            if (!Available) return false;
            f = new HandFrame { leftTracked = lt, rightTracked = rt, left = left, right = right };
            return true;
#else
            Available = false;
            return false;
#endif
        }

        /// <summary>Wrist (index 1) pose of one hand — the 6-DoF fed to the derived gripper_pose.</summary>
        public bool TryGetWrist(in HandFrame f, bool rightHand, out Vector3 p, out Quaternion q)
        {
            p = Vector3.zero; q = Quaternion.identity;
            var arr = rightHand ? f.right : f.left;
            bool tracked = rightHand ? f.rightTracked : f.leftTracked;
            if (!tracked || arr == null || arr.Length <= WristJoint) return false;
            p = arr[WristJoint].position;
            q = arr[WristJoint].rotation;
            return true;
        }

#if EGOGRIP_PICO_HANDS
        private HandJointLocations _loc = new HandJointLocations();

        // CONFIRM these symbols against PICO SDK v3.4.0.
        private bool ReadHand(HandType hand, Pose[] outJoints)
        {
            if (!PXR_HandTracking.GetJointLocations(hand, ref _loc)) return false;
            var jls = _loc.jointLocations;
            if (jls == null || jls.Length < JointCount) return false;
            for (int i = 0; i < JointCount; i++)
            {
                var pose = jls[i].pose; // Posef: Position (PxrVector3f), Orientation (PxrVector4f)
                var pos = new Vector3(pose.Position.x, pose.Position.y, pose.Position.z);
                var rot = new Quaternion(pose.Orientation.x, pose.Orientation.y,
                                         pose.Orientation.z, pose.Orientation.w);
                outJoints[i] = new Pose(pos, rot);
            }
            return true;
        }
#endif
    }
}
