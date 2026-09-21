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
        private const string FailureFileName = "system-backend.error";
        private const int MaximumRequestValueSize = 64 * 1024;

        private static readonly object SyncRoot = new object();
        private static bool systemCompositionSelected;
        private static bool systemCompositionCertified;
        private static bool switcherRequested;
        private static string configuredLafToken;
        private static string configuredRunId;
        private static string pendingCertificationPath;
        private static string pendingFailurePath;
        private static string pendingRequestId;

        internal static bool IsConfigured
        {
            get
            {
                lock (SyncRoot)
                {
                    return systemCompositionCertified;
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

        internal static bool ConfigureFromLaunchRequest()
        {
            string requestDirectory = Path.Combine(
                AppContext.BaseDirectory,
                RequestDirectoryName);
            return ConfigureFromLaunchRequest(
                requestDirectory,
                null,
                null,
                true);
        }

        internal static bool ConfigureFromPackagedLaunchRequest()
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
            return ConfigureFromLaunchRequest(
                requestDirectory,
                RunIdFileName,
                ExpirationFileName,
                false);
        }

        internal static bool IsConfiguredFor(string lafToken, string runId)
        {
            lock (SyncRoot)
            {
                return systemCompositionCertified &&
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

        private static bool ConfigureFromLaunchRequest(
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
            string failurePath = Path.Combine(
                requestDirectory,
                FailureFileName);
            if (writeCertification)
            {
                if (File.Exists(certificationPath))
                {
                    File.Delete(certificationPath);
                }
                if (File.Exists(failurePath))
                {
                    File.Delete(failurePath);
                }
            }

            lock (SyncRoot)
            {
                pendingCertificationPath = writeCertification
                    ? certificationPath
                    : null;
                pendingFailurePath = writeCertification
                    ? failurePath
                    : null;
                pendingRequestId = parsedRequestId.ToString(
                    "D",
                    CultureInfo.InvariantCulture);
            }

            try
            {
                Configure(lafToken, runId);
            }
            catch (Exception exception)
            {
                WritePendingFailure(
                    "selection",
                    exception,
                    lafToken);
                throw;
            }

            return true;
        }

        internal static void Configure(string lafToken)
        {
            Configure(lafToken, null);
        }

        private static void Configure(
            string lafToken,
            string runId)
        {
            lock (SyncRoot)
            {
                switcherRequested = true;
                if (systemCompositionSelected)
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

                LimitedAccessFeatureRequestResult unlockResult;
                try
                {
                    unlockResult = LimitedAccessFeatures.TryUnlockFeature(
                        FeatureId,
                        lafToken,
                        Attestation);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "Composition switcher LAF authorization API failed " +
                        "(HRESULT=0x" +
                        exception.HResult.ToString(
                            "X8",
                            CultureInfo.InvariantCulture) +
                        ").",
                        exception);
                }
                if (unlockResult.Status != LimitedAccessFeatureStatus.Available)
                {
                    throw new InvalidOperationException(
                        "Composition switcher LAF authorization failed " +
                        "(status=" + unlockResult.Status + ").");
                }

                bool systemEngineSelected;
                try
                {
                    systemEngineSelected =
                        Microsoft.UI.Composition.CompositionEngine.TrySetProcessEngine(
                            Microsoft.UI.Composition.CompositionEngineType.System);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "Composition switcher System engine selection API failed " +
                        "(HRESULT=0x" +
                        exception.HResult.ToString(
                            "X8",
                            CultureInfo.InvariantCulture) +
                        ").",
                        exception);
                }
                if (!systemEngineSelected)
                {
                    throw new InvalidOperationException(
                        "TrySetProcessEngine(System) did not engage in the test application process.");
                }

                systemCompositionSelected = true;
                configuredLafToken = lafToken;
                configuredRunId = runId;
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
                if (!systemCompositionSelected)
                {
                    throw new InvalidOperationException(
                        "System composition must be selected before it can be certified.");
                }

                try
                {
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

                    if (!string.IsNullOrEmpty(pendingCertificationPath))
                    {
                        string certification =
                            pendingRequestId +
                            Environment.NewLine +
                            GetCurrentProcessId().ToString(
                                CultureInfo.InvariantCulture);
                        WriteResponse(
                            pendingCertificationPath,
                            certification);
                    }

                    systemCompositionCertified = true;
                }
                catch (Exception exception)
                {
                    WritePendingFailure(
                        "certification",
                        exception,
                        configuredLafToken);
                    throw;
                }
            }
        }

        private static void WritePendingFailure(
            string phase,
            Exception exception,
            string lafToken)
        {
            string failurePath = pendingFailurePath;
            if (string.IsNullOrEmpty(failurePath))
            {
                return;
            }

            string failure =
                "Composition switcher " +
                phase +
                " failed: " +
                exception.GetType().FullName +
                " (HRESULT=0x" +
                exception.HResult.ToString("X8", CultureInfo.InvariantCulture) +
                "): " +
                exception.Message +
                Environment.NewLine +
                exception.StackTrace;
            if (!string.IsNullOrEmpty(lafToken))
            {
                failure = failure.Replace(
                    lafToken,
                    "***");
            }
            WriteResponse(
                failurePath,
                failure);
        }

        private static void WriteResponse(
            string path,
            string value)
        {
            string temporaryPath = path + ".tmp";
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    value,
                    new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(
                    temporaryPath,
                    path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
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
