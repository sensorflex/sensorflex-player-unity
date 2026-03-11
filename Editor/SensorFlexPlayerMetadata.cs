using System.Collections.Generic;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEditor;

namespace SensorFlexPlayer.Editor
{
    class SensorFlexPlayerMetadata : IXRPackage
    {
        class LoaderMetadata : IXRLoaderMetadata
        {
            public string loaderName { get; set; }
            public string loaderType { get; set; }
            public List<BuildTargetGroup> supportedBuildTargets { get; set; }
        }
        class PackageMetadata : IXRPackageMetadata
        {
            public string packageName { get; set; }
            public string packageId { get; set; }
            public string settingsType { get; set; }
            public List<IXRLoaderMetadata> loaderMetadata { get; set; }
        }

        static readonly IXRPackageMetadata s_Metadata = new PackageMetadata
        {
            packageName = "SensorFlex Player",
            packageId = "com.sensorflex.player.unity",
            settingsType = "SensorFlex.Player.SensorFlexSettings",

            loaderMetadata = new List<IXRLoaderMetadata>
            {
                new LoaderMetadata
                {
                    loaderName = "SensorFlex Player",
                    loaderType = "SensorFlex.Player.Loader",

                    supportedBuildTargets = new List<BuildTargetGroup>
                    {
                        BuildTargetGroup.Standalone
                    }
                }
            }
        };


        public IXRPackageMetadata metadata => s_Metadata;

        public bool PopulateNewSettingsInstance(ScriptableObject obj)
        {
            return false;
        }
    }
}
