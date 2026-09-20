// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel;

namespace Microsoft.UI.Xaml.Tests.Common
{
    internal static class SwitcherComposition
    {
        private const string FeatureId = "com.microsoft.windows.composition.engine";
        private const string Attestation =
            "8wekyb3d8bbwe has registered their use of com.microsoft.windows.composition.engine " +
            "with Microsoft and agrees to the terms of use.";
        private const string RequestDirectoryName = ".winui-switcher";
        private const string TokenFileName = "system-backend";
        private const string RequestIdFileName = "request-id";
        private const string CertificationFileName = "system-backend.certified";
        private const int MaximumRequestValueSize = 64 * 1024;

        private static readonly object SyncRoot = new object();
        private static bool systemCompositionConfigured;
        private static bool switcherRequested;

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

        internal static bool IsRequested
        {
            get
            {
                lock (SyncRoot)
                {
                    return switcherRequested;
                }
            }
        }

        internal static bool ConfigureAndCertifyFromLaunchRequest()
        {
            string requestDirectory = Path.Combine(
                AppContext.BaseDirectory,
                RequestDirectoryName);
            if (!Directory.Exists(requestDirectory))
            {
                return false;
            }

            string requestId = ReadBoundedText(
                Path.Combine(requestDirectory, RequestIdFileName));
            Guid parsedRequestId;
            if (!Guid.TryParse(requestId, out parsedRequestId))
            {
                throw new InvalidDataException(
                    "The composition switcher launch request identifier is invalid.");
            }

            string lafToken = ReadBoundedText(
                Path.Combine(requestDirectory, TokenFileName));
            ConfigureAndCertify(lafToken);

            string certification =
                parsedRequestId.ToString("D", CultureInfo.InvariantCulture) +
                Environment.NewLine +
                GetCurrentProcessId().ToString(CultureInfo.InvariantCulture);
            File.WriteAllText(
                Path.Combine(requestDirectory, CertificationFileName),
                certification,
                new UTF8Encoding(false));
            return true;
        }

        internal static bool ConfigureAndCertifyFromActivationArguments(
            string arguments)
        {
            string switcherMode = GetActivationParameter(arguments, "SwitcherMode");
            string lafToken = GetActivationParameter(arguments, "SwitcherLafToken");
            bool hasSwitcherMode = !string.IsNullOrEmpty(switcherMode);
            bool hasLafToken = !string.IsNullOrEmpty(lafToken);
            if (!hasSwitcherMode && !hasLafToken)
            {
                return false;
            }
            if (!IsTrue(switcherMode))
            {
                throw new InvalidOperationException(
                    "Composition switcher activation requires SwitcherMode=true.");
            }

            ConfigureAndCertify(lafToken);
            return true;
        }

        internal static void ConfigureAndCertify(string lafToken)
        {
            lock (SyncRoot)
            {
                switcherRequested = true;
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
                        "(status=" + unlockResult.Status + ").");
                }

                if (!Microsoft.UI.Composition.CompositionEngine.TrySetProcessEngine(
                        Microsoft.UI.Composition.CompositionEngineType.System))
                {
                    throw new InvalidOperationException(
                        "TrySetProcessEngine(System) did not engage in the test application process.");
                }

                using (var compositor = new Microsoft.UI.Composition.Compositor())
                {
                    object systemCompositor =
                        Microsoft.UI.Composition.CompositionEngine.GetForSystemEngine(
                            compositor);
                    if (!(systemCompositor is Windows.UI.Composition.Compositor))
                    {
                        throw new InvalidOperationException(
                            "GetForSystemEngine did not return a Windows.UI.Composition.Compositor.");
                    }
                }

                systemCompositionConfigured = true;
            }
        }

        internal static bool IsTrue(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "1", StringComparison.Ordinal);
        }

        private static string GetActivationParameter(
            string arguments,
            string parameterName)
        {
            if (string.IsNullOrEmpty(arguments))
            {
                return null;
            }

            string prefix = "/p:" + parameterName + "=";
            int parameterIndex = arguments.IndexOf(
                prefix,
                StringComparison.OrdinalIgnoreCase);
            if (parameterIndex < 0)
            {
                return null;
            }

            int valueStart = parameterIndex + prefix.Length;
            if (valueStart < arguments.Length && arguments[valueStart] == '"')
            {
                valueStart++;
                int closingQuote = arguments.IndexOf('"', valueStart);
                if (closingQuote < 0)
                {
                    throw new InvalidOperationException(
                        "Composition switcher activation contains an unterminated quoted parameter.");
                }

                return arguments.Substring(
                    valueStart,
                    closingQuote - valueStart);
            }

            int valueEnd = arguments.IndexOfAny(
                new[] { ' ', '\t', '\r', '\n' },
                valueStart);
            return valueEnd < 0
                ? arguments.Substring(valueStart)
                : arguments.Substring(valueStart, valueEnd - valueStart);
        }

        private static string ReadBoundedText(string path)
        {
            using (var stream = File.Open(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))
            using (var reader = new StreamReader(
                stream,
                new UTF8Encoding(false, true),
                false))
            {
                if (stream.Length <= 0 || stream.Length > MaximumRequestValueSize)
                {
                    throw new InvalidDataException(
                        "A composition switcher launch request value has an invalid size.");
                }

                return reader.ReadToEnd();
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();
    }
}
