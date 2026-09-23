// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using Windows.ApplicationModel;

namespace Private.Infrastructure.Hosting
{
    internal static class CompositionSwitcher
    {
        private const string FeatureId = "com.microsoft.windows.composition.engine";
        private const string Attestation =
            "8wekyb3d8bbwe has registered their use of com.microsoft.windows.composition.engine " +
            "with Microsoft and agrees to the terms of use.";

        private static readonly object SyncRoot = new object();
        private static bool systemCompositionConfigured;
        private static bool systemCompositionCertified;

        internal static bool IsConfigured
        {
            get
            {
                lock (SyncRoot)
                {
                    return systemCompositionConfigured;
                }
            }
        }

        internal static void Configure(string lafToken)
        {
            lock (SyncRoot)
            {
                if (systemCompositionConfigured)
                {
                    return;
                }

                if (string.IsNullOrEmpty(lafToken))
                {
                    throw new InvalidOperationException(
                        "Composition switcher tests require a non-empty LAF token.");
                }

                var unlockResult = LimitedAccessFeatures.TryUnlockFeature(
                    FeatureId,
                    lafToken,
                    Attestation);
                if (unlockResult.Status != LimitedAccessFeatureStatus.Available)
                {
                    throw new InvalidOperationException(
                        "Composition switcher LAF authorization failed " +
                        $"(status={unlockResult.Status}).");
                }

                if (!Microsoft.UI.Composition.CompositionEngine.TrySetProcessEngine(
                    Microsoft.UI.Composition.CompositionEngineType.System))
                {
                    throw new InvalidOperationException(
                        "TrySetProcessEngine(System) did not engage in the Win32 XAML host process.");
                }

                systemCompositionConfigured = true;
            }
        }

        internal static void Certify()
        {
            lock (SyncRoot)
            {
                if (systemCompositionCertified)
                {
                    return;
                }

                if (!systemCompositionConfigured)
                {
                    throw new InvalidOperationException(
                        "System composition must be configured before it can be certified.");
                }

                using (var compositor = new Microsoft.UI.Composition.Compositor())
                {
                    var systemCompositor =
                        Microsoft.UI.Composition.CompositionEngine.GetForSystemEngine(compositor);
                    if (!(systemCompositor is Windows.UI.Composition.Compositor))
                    {
                        throw new InvalidOperationException(
                            "GetForSystemEngine did not return a Windows.UI.Composition.Compositor.");
                    }
                }

                systemCompositionCertified = true;
            }
        }

        internal static void ConfigureAndCertify(string lafToken)
        {
            Configure(lafToken);
            Certify();
        }
    }
}
