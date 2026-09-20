// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

using WEX.Logging.Interop;
using WEX.TestExecution;
using WEX.TestExecution.Markup;

namespace Microsoft.UI.Xaml.Tests.MUXControls.InteractionTests.Infra
{
    internal sealed class SwitcherLaunchRequest : IDisposable
    {
        private const string RequestDirectoryName = ".winui-switcher";
        private const string TokenFileName = "system-backend";
        private const string RequestIdFileName = "request-id";
        private const string CertificationFileName = "system-backend.certified";
        private const string FailureFileName = "system-backend.error";
        private const int MaximumRequestValueSize = 64 * 1024;

        private static readonly object CertifiedProcessesLock = new object();
        private static readonly HashSet<int> CertifiedProcesses = new HashSet<int>();

        private readonly string requestId;
        private readonly string certificationPath;
        private readonly string failurePath;

        private SwitcherLaunchRequest(string requestDirectory, string requestId)
        {
            this.requestId = requestId;
            this.certificationPath = Path.Combine(
                requestDirectory,
                CertificationFileName);
            this.failurePath = Path.Combine(
                requestDirectory,
                FailureFileName);
        }

        internal static bool IsEnabled(TestContext testContext)
        {
            bool switcherRequested = testContext != null &&
                testContext.Properties.Contains("SwitcherMode") &&
                IsTrue(Convert.ToString(
                    testContext.Properties["SwitcherMode"],
                    CultureInfo.InvariantCulture));
            bool switcherExpected =
                (testContext != null &&
                    testContext.Properties.Contains("SwitcherLafToken")) ||
                !string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable(
                        "SWITCHER_LAF_TOKEN",
                        EnvironmentVariableTarget.Process));
            if (switcherExpected && !switcherRequested)
            {
                throw new InvalidOperationException(
                    "The interaction test process did not receive SwitcherMode.");
            }

            return switcherRequested;
        }

        internal static SwitcherLaunchRequest Prepare(
            TestContext testContext,
            bool isPackaged,
            string packageFamilyName,
            string unpackagedExecutablePath)
        {
            if (!IsEnabled(testContext))
            {
                return null;
            }

            string requestDirectory;
            if (isPackaged)
            {
                if (string.IsNullOrWhiteSpace(packageFamilyName))
                {
                    throw new InvalidOperationException(
                        "A packaged Switcher test application requires a package family name.");
                }

                requestDirectory = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "Packages",
                    packageFamilyName,
                    "LocalState",
                    RequestDirectoryName);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(unpackagedExecutablePath))
                {
                    throw new InvalidOperationException(
                        "An unpackaged Switcher test application requires an executable path.");
                }

                string executablePath = Path.Combine(
                    Path.GetDirectoryName(AssemblyLocation),
                    "UnpackagedApps",
                    unpackagedExecutablePath);
                requestDirectory = Path.Combine(
                    Path.GetDirectoryName(executablePath),
                    RequestDirectoryName);
            }

            string tokenPath = Path.Combine(requestDirectory, TokenFileName);
            if (!File.Exists(tokenPath))
            {
                throw new FileNotFoundException(
                    "The Switcher launch request token was not provisioned.",
                    tokenPath);
            }

            long tokenLength = new FileInfo(tokenPath).Length;
            if (tokenLength <= 0 || tokenLength > MaximumRequestValueSize)
            {
                throw new InvalidDataException(
                    "The Switcher launch request token has an invalid size.");
            }

            string requestId = ReadBoundedText(
                Path.Combine(requestDirectory, RequestIdFileName));
            Guid parsedRequestId;
            if (!Guid.TryParse(requestId, out parsedRequestId))
            {
                throw new InvalidDataException(
                    "The Switcher launch request identifier is invalid.");
            }

            var request = new SwitcherLaunchRequest(
                requestDirectory,
                parsedRequestId.ToString("D"));
            request.DeleteResponse();
            return request;
        }

        internal static bool IsCertifiedProcess(int processId)
        {
            lock (CertifiedProcessesLock)
            {
                return CertifiedProcesses.Contains(processId);
            }
        }

        internal void Verify(int processId, string processName)
        {
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(certificationPath) &&
                !File.Exists(failurePath) &&
                timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(100);
            }

            if (File.Exists(failurePath))
            {
                throw new InvalidOperationException(
                    processName +
                    " failed before System composition certification: " +
                    ReadBoundedText(failurePath));
            }
            if (!File.Exists(certificationPath))
            {
                throw new InvalidOperationException(
                    processName +
                    " did not certify System composition before creating its window.");
            }

            string[] certification = File.ReadAllLines(
                certificationPath,
                new UTF8Encoding(false, true));
            if (certification.Length != 2 ||
                !string.Equals(
                    certification[0],
                    requestId,
                    StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(
                    certification[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int certifiedProcessId) ||
                certifiedProcessId != processId)
            {
                throw new InvalidDataException(
                    processName +
                    " produced an invalid System composition certification response.");
            }

            lock (CertifiedProcessesLock)
            {
                CertifiedProcesses.Add(processId);
            }

            Log.Comment(
                "SwitcherMode: {0} process {1} selected and certified System composition.",
                processName,
                processId);
        }

        internal void ThrowIfFailed(string processName)
        {
            if (File.Exists(failurePath))
            {
                throw new InvalidOperationException(
                    processName +
                    " failed before System composition certification: " +
                    ReadBoundedText(failurePath));
            }
        }

        public void Dispose()
        {
            DeleteResponse();
        }

        private static string AssemblyLocation
        {
            get
            {
                return typeof(SwitcherLaunchRequest).Assembly.Location;
            }
        }

        private static bool IsTrue(string value)
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
                        "A Switcher launch request value has an invalid size.");
                }

                return reader.ReadToEnd();
            }
        }

        private void DeleteResponse()
        {
            DeleteIfPresent(certificationPath);
            DeleteIfPresent(certificationPath + ".tmp");
            DeleteIfPresent(failurePath);
            DeleteIfPresent(failurePath + ".tmp");
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
