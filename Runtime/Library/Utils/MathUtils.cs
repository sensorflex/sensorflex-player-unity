// MathUtils.cs — XR camera math utilities.
//
// ConvertToUnityPose    — converts a camera-to-world matrix from a source coordinate
//                         system to a Unity Pose via the symmetric formula M_unity = C·M·C.
// ComputeProjectionMatrix — builds an off-centre Unity projection matrix from pinhole
//                         intrinsics (fx, fy, cx, cy).

using UnityEngine;

namespace SensorFlex.Player.Library
{
    internal static class MathUtils
    {
        /// <summary>
        /// Converts a camera-to-world matrix from a source coordinate system to a Unity
        /// <see cref="Pose"/> using the symmetric formula M_unity = C * M_source * C.
        /// Pass <paramref name="useNegativeZForwardOpticalAxis"/> = true for -Z-forward cameras.
        /// </summary>
        internal static Pose ConvertToUnityPose(Matrix4x4 source, Matrix4x4 c, bool useNegativeZForwardOpticalAxis = false)
        {
            var m        = c * source * c;
            var position = new Vector3(m.m03, m.m13, m.m23);
            var forward  = useNegativeZForwardOpticalAxis
                ? new Vector3(-m.m02, -m.m12, -m.m22)
                : new Vector3( m.m02,  m.m12,  m.m22);
            var up       = useNegativeZForwardOpticalAxis
                ? new Vector3(-m.m01, -m.m11, -m.m21)
                : new Vector3( m.m01,  m.m11,  m.m21);
            if (forward == Vector3.zero || up == Vector3.zero)
                return new Pose(position, Quaternion.identity);
            return new Pose(position, Quaternion.LookRotation(forward, up));
        }

        /// <summary>
        /// Builds a Unity projection matrix from pinhole camera intrinsics
        /// (fx, fy, cx, cy) for an image of the given width and height.
        /// Image-space origin is assumed at top-left.
        /// </summary>
        internal static Matrix4x4 ComputeProjectionMatrix(
            Vector4 intrinsics, int imageWidth, int imageHeight,
            float nearClipPlane, float farClipPlane)
        {
            float fx = intrinsics.x, fy = intrinsics.y;
            float cx = intrinsics.z, cy = intrinsics.w;

            if (fx <= 0f || fy <= 0f || imageWidth <= 0 || imageHeight <= 0)
                return Matrix4x4.Perspective(60f, (float)imageWidth / Mathf.Max(1, imageHeight), nearClipPlane, farClipPlane);

            float left   = -cx * nearClipPlane / fx;
            float right  = (imageWidth  - cx) * nearClipPlane / fx;
            float top    =  cy * nearClipPlane / fy;
            float bottom = -(imageHeight - cy) * nearClipPlane / fy;

            return PerspectiveOffCenter(left, right, bottom, top, nearClipPlane, farClipPlane);
        }

        static Matrix4x4 PerspectiveOffCenter(float left, float right, float bottom, float top, float near, float far)
        {
            float x = 2f * near / (right - left);
            float y = 2f * near / (top - bottom);
            float a = (right + left) / (right - left);
            float b = (top + bottom) / (top - bottom);
            float c = -(far + near) / (far - near);
            float d = -(2f * far * near) / (far - near);

            var m = new Matrix4x4();
            m[0, 0] = x;   m[0, 1] = 0f;  m[0, 2] = a;   m[0, 3] = 0f;
            m[1, 0] = 0f;  m[1, 1] = y;   m[1, 2] = b;   m[1, 3] = 0f;
            m[2, 0] = 0f;  m[2, 1] = 0f;  m[2, 2] = c;   m[2, 3] = d;
            m[3, 0] = 0f;  m[3, 1] = 0f;  m[3, 2] = -1f; m[3, 3] = 0f;
            return m;
        }
    }
}
