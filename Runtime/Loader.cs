using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.ARSubsystems;

namespace SensorFlex.Player
{
    public class Loader : XRLoaderHelper
    {
        static List<XRCameraSubsystemDescriptor> cameraDescriptors = new();
        static List<XRSessionSubsystemDescriptor> sessionDescriptors = new();

        public override bool Initialize()
        {
            Debug.Log("[SF] Loader Initialize");

            SubsystemManager.GetSubsystemDescriptors(cameraDescriptors);
            SubsystemManager.GetSubsystemDescriptors(sessionDescriptors);

            CreateSubsystem<XRCameraSubsystemDescriptor, XRCameraSubsystem>(cameraDescriptors, "SensorFlex-Camera");
            CreateSubsystem<XRSessionSubsystemDescriptor, XRSessionSubsystem>(sessionDescriptors, "SensorFlex-Session");

            var cam = GetLoadedSubsystem<XRCameraSubsystem>();
            var ses = GetLoadedSubsystem<XRSessionSubsystem>();

            Debug.Log($"[SF] Created subsystems: Camera={(cam != null)}, Session={(ses != null)}");

            return cam != null && ses != null;
        }

        public override bool Start()
        {
            Debug.Log("[SF] Loader Start");
            StartSubsystem<XRCameraSubsystem>();
            StartSubsystem<XRSessionSubsystem>();
            return true;
        }

        public override bool Stop()
        {
            Debug.Log("[SF] Loader Stop");
            StopSubsystem<XRCameraSubsystem>();
            StopSubsystem<XRSessionSubsystem>();
            return true;
        }

        public override bool Deinitialize()
        {
            Debug.Log("[SF] Loader Deinitialize");
            DestroySubsystem<XRCameraSubsystem>();
            DestroySubsystem<XRSessionSubsystem>();
            return true;
        }
    }
}
