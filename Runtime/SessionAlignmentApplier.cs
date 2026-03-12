using Unity.XR.CoreUtils;
using UnityEngine;

namespace SensorFlex.Player
{
    internal static class SessionAlignmentApplier
    {
        internal static void ApplyToActiveXROrigin(SensorFlexSettings settings)
        {
            if (settings == null)
            {
                Debug.LogWarning("[SF] Session alignment skipped because SensorFlexSettings is null.");
                return;
            }

            var alignment = settings?.sessionAlignment;
            if (alignment == null)
            {
                Debug.LogWarning("[SF] Session alignment skipped because the sessionAlignment block is missing.");
                return;
            }

            if (!alignment.enabled)
            {
                Debug.Log("[SF] Session alignment is disabled in SensorFlexSettings.");
                return;
            }

            var origins = Object.FindObjectsByType<XROrigin>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (origins == null || origins.Length == 0)
            {
                Debug.LogWarning("[SF] Session alignment is enabled, but no XROrigin was found in the scene.");
                return;
            }

            XROrigin fallbackOrigin = null;
            foreach (var origin in origins)
            {
                if (origin == null)
                    continue;

                fallbackOrigin ??= origin;
                if (!origin.gameObject.activeInHierarchy)
                    continue;

                ApplyLocalTransform(origin.transform, alignment);
                Debug.Log($"[SF] Applied session alignment to XROrigin '{origin.name}': " +
                          $"position={alignment.positionOffset} rotation={alignment.rotationEuler} scale={alignment.uniformScale}");
                return;
            }

            if (fallbackOrigin != null)
            {
                ApplyLocalTransform(fallbackOrigin.transform, alignment);
                Debug.Log($"[SF] Applied session alignment to inactive XROrigin '{fallbackOrigin.name}' before activation: " +
                          $"position={alignment.positionOffset} rotation={alignment.rotationEuler} scale={alignment.uniformScale}");
                return;
            }

            Debug.LogWarning("[SF] Session alignment is enabled, but no usable XROrigin was found in the scene.");
        }

        static void ApplyLocalTransform(Transform target, SensorFlexSettings.SessionAlignmentSettings alignment)
        {
            target.localPosition = alignment.positionOffset;
            target.localRotation = Quaternion.Euler(alignment.rotationEuler);
            target.localScale = Vector3.one * alignment.uniformScale;
        }
    }
}
