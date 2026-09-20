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
        private const string RunIdFileName = "run-id";
        private const string ExpirationFileName = "expires-at";
        private const string CertificationFileName = "system-backend.certified";
        private const int MaximumRequestValueSize = 64 * 1024;

        private static readonly object SyncRoot = new object();
        private static bool systemCompositionConfigured;
        private static bool switcherRequested;
        private static string configuredLafToken;
        private static string configuredRunId;

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
            return ConfigureAndCertifyFromLaunchRequest(
                requestDirectory,
                null,
                null,
                true);
        }

        internal static bool ConfigureAndCertifyFromPackagedLaunchRequest()
        {
            // TAEF removes package data before activation, so this request must live outside LocalState.
            string programData =
                global::Windows.Storage.SystemDataPaths.GetDefault().ProgramData;
            if (string.IsNullOrEmpty(programData))
            {
                throw new InvalidOperationException(
                    "The ProgramData directory is unavailable.");
            }

            string requestDirectory = Path.Combine(
                programData,
                "Microsoft",
                "WinUI",
                "CompositionSwitcher",
                "Requests",
                Package.Current.Id.FamilyName,
                RequestDirectoryName);
            return ConfigureAndCertifyFromLaunchRequest(
                requestDirectory,
                RunIdFileName,
                ExpirationFileName,
                false);
        }

        internal static bool IsConfiguredFor(string lafToken, string runId)
        {
            lock (SyncRoot)
            {
                return systemCompositionConfigured &&
                    string.Equals(
                        configuredLafToken,
                        lafToken,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        configuredRunId,
                        runId,
                        StringComparison.Ordinal);
            }
        }

        private static bool ConfigureAndCertifyFromLaunchRequest(
            string requestDirectory,
            string runIdFileName,
            string expirationFileName,
            bool writeCertification)
        {
            if (!Directory.Exists(requestDirectory))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(expirationFileName))
            {
                string expirationPath = Path.Combine(
                    requestDirectory,
                    expirationFileName);
                string expiration = File.Exists(expirationPath)
                    ? ReadBoundedText(expirationPath)
                    : null;
                DateTimeOffset expirationTime;
                if (!DateTimeOffset.TryParseExact(
                        expiration,
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out expirationTime) ||
                    expirationTime <= DateTimeOffset.UtcNow)
                {
                    return false;
                }
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
            string runId = string.IsNullOrEmpty(runIdFileName)
                ? null
                : ReadBoundedText(
                    Path.Combine(requestDirectory, runIdFileName));
            string certificationPath = Path.Combine(
                requestDirectory,
                CertificationFileName);
            if (writeCertification)
            {
                File.WriteAllText(
                    certificationPath,
                    string.Empty,
                    new UTF8Encoding(false));
            }

            ConfigureAndCertify(lafToken, runId);

            if (writeCertification)
            {
                string certification =
                    parsedRequestId.ToString("D", CultureInfo.InvariantCulture) +
                    Environment.NewLine +
                    GetCurrentProcessId().ToString(CultureInfo.InvariantCulture);
                File.WriteAllText(
                    certificationPath,
                    certification,
                    new UTF8Encoding(false));
            }
            return true;
        }

        internal static void ConfigureAndCertify(string lafToken)
        {
            ConfigureAndCertify(lafToken, null);
        }

        private static void ConfigureAndCertify(
            string lafToken,
            string runId)
        {
            lock (SyncRoot)
            {
                switcherRequested = true;
                if (systemCompositionConfigured)
                {
                    if (!string.Equals(
                            configuredLafToken,
                            lafToken,
                            StringComparison.Ordinal) ||
                        (!string.IsNullOrEmpty(runId) &&
                            !string.Equals(
                                configuredRunId,
                                runId,
                                StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            "Composition switcher configuration changed after selection.");
                    }
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
                    if (!(systemCompositor is global::Windows.UI.Composition.Compositor))
                    {
                        throw new InvalidOperationException(
                            "GetForSystemEngine did not return a Windows.UI.Composition.Compositor.");
                    }
                }

                systemCompositionConfigured = true;
                configuredLafToken = lafToken;
                configuredRunId = runId;
            }
        }

        internal static bool IsTrue(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "1", StringComparison.Ordinal);
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
