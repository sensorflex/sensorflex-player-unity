using UnityEngine;
using UnityEngine.XR.Management;

namespace SensorFlex.Player
{
    [XRConfigurationData("SensorFlex Player", "SensorFlex.Player.SensorFlexSettings")]
    [CreateAssetMenu(menuName = "SensorFlex Player/Settings", fileName = "SensorFlexSettings")]
    public class SensorFlexSettings : ScriptableObject { }
}
