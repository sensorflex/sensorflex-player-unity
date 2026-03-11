using UnityEngine;
using UnityEngine.SubsystemsImplementation;
using UnityEngine.XR.ARSubsystems;

namespace SensorFlex.Player.Subsystem
{
    public sealed class SessionSubsystem : XRSessionSubsystem
    {
        const string SubsystemId = "SensorFlex-Session";

        new class Provider : XRSessionSubsystem.Provider
        {
            public override Promise<SessionAvailability> GetAvailabilityAsync()
                => Promise<SessionAvailability>.CreateResolvedPromise(SessionAvailability.Supported | SessionAvailability.Installed);

            public override Promise<SessionInstallationStatus> InstallAsync()
                => Promise<SessionInstallationStatus>.CreateResolvedPromise(SessionInstallationStatus.Success);

            public override TrackingState trackingState => TrackingState.Tracking;
            public override NotTrackingReason notTrackingReason => NotTrackingReason.None;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void RegisterDescriptor()
        {
            XRSessionSubsystemDescriptor.Register(new XRSessionSubsystemDescriptor.Cinfo
            {
                id = SubsystemId,
                providerType = typeof(Provider),
                subsystemTypeOverride = typeof(SessionSubsystem),
                supportsInstall = false,
                supportsMatchFrameRate = false
            });
            Debug.Log("[SF] Session Subsystem started successfully.");

        }
    }
}

