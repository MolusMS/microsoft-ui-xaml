// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;

namespace Private.Infrastructure.Hosting
{
    internal static class CompositionSwitcher
    {
        private static readonly object SyncRoot = new object();
        private static bool systemCompositionConfigured;
        private static bool systemCompositionCertified;
        private static bool xamlCompositionCertified;

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

        internal static void Configure()
        {
            lock (SyncRoot)
            {
                if (systemCompositionConfigured)
                {
                    return;
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
                    Certify(compositor);
                }
                systemCompositionCertified = true;
            }
        }

        internal static void Certify(Microsoft.UI.Xaml.UIElement xamlElement)
        {
            if (xamlElement == null)
            {
                throw new ArgumentNullException(nameof(xamlElement));
            }

            lock (SyncRoot)
            {
                if (xamlCompositionCertified)
                {
                    return;
                }

                if (!systemCompositionConfigured)
                {
                    throw new InvalidOperationException(
                        "System composition must be configured before it can be certified.");
                }

                Certify(
                    Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                        .GetElementVisual(xamlElement)
                        .Compositor);
                systemCompositionCertified = true;
                xamlCompositionCertified = true;
            }
        }

        internal static void ConfigureAndCertify()
        {
            Configure();
            Certify();
        }

        private static void Certify(
            Microsoft.UI.Composition.Compositor compositor)
        {
            var systemCompositor =
                Microsoft.UI.Composition.CompositionEngine.GetForSystemEngine(
                    compositor);
            if (!(systemCompositor is Windows.UI.Composition.Compositor))
            {
                throw new InvalidOperationException(
                    "GetForSystemEngine did not return a Windows.UI.Composition.Compositor.");
            }
        }
    }
}
