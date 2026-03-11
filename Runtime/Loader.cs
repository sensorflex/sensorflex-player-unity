using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.ARSubsystems;

namespace SensorFlex.Player
{
    public class Loader : XRLoaderHelper
    {
        static readonly List<XRCameraSubsystemDescriptor> cameraDescriptors = new();
        static readonly List<XRSessionSubsystemDescriptor> sessionDescriptors = new();
        static readonly List<XROcclusionSubsystemDescriptor> occlusionDescriptors = new();

        public override bool Initialize()
        {
            Debug.Log("[SF] Loader Initialize");

            SubsystemManager.GetSubsystemDescriptors(cameraDescriptors);
            SubsystemManager.GetSubsystemDescriptors(sessionDescriptors);
            SubsystemManager.GetSubsystemDescriptors(occlusionDescriptors);

            CreateSubsystem<XRCameraSubsystemDescriptor, XRCameraSubsystem>(cameraDescriptors, "SensorFlex-Camera");
            CreateSubsystem<XRSessionSubsystemDescriptor, XRSessionSubsystem>(sessionDescriptors, "SensorFlex-Session");
            CreateSubsystem<XROcclusionSubsystemDescriptor, XROcclusionSubsystem>(occlusionDescriptors, "SensorFlex-Occlusion");

            var cam = GetLoadedSubsystem<XRCameraSubsystem>();
            var ses = GetLoadedSubsystem<XRSessionSubsystem>();
            var occ = GetLoadedSubsystem<XROcclusionSubsystem>();

            Debug.Log($"[SF] Created subsystems: Camera={cam != null}, Session={ses != null}, Occlusion={occ != null}");

            return cam != null && ses != null;
        }

        public override bool Start()
        {
            Debug.Log("[SF] Loader Start");
            StartSubsystem<XRCameraSubsystem>();
            StartSubsystem<XRSessionSubsystem>();
            StartSubsystem<XROcclusionSubsystem>();
            return true;
        }

        public override bool Stop()
        {
            Debug.Log("[SF] Loader Stop");
            StopSubsystem<XRCameraSubsystem>();
            StopSubsystem<XRSessionSubsystem>();
            StopSubsystem<XROcclusionSubsystem>();
            return true;
        }

        public override bool Deinitialize()
        {
            Debug.Log("[SF] Loader Deinitialize");
            DestroySubsystem<XRCameraSubsystem>();
            DestroySubsystem<XRSessionSubsystem>();
            DestroySubsystem<XROcclusionSubsystem>();
            return true;
        }
    }
}
