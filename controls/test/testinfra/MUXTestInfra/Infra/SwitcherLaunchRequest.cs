// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
        private const string RequestIdFileName = "request-id";
        private const string CertificationFileName = "certified-response";
        private const string FailureFileName = "failure-response";
        private const int MaximumResponseLength = 4096;

        private static readonly object CertifiedProcessesLock = new object();
        private static readonly HashSet<int> CertifiedProcesses =
            new HashSet<int>();

        private readonly string requestDirectory;
        private readonly string requestId;
        private readonly string requestIdPath;
        private readonly string certificationPath;
        private readonly string failurePath;

        private SwitcherLaunchRequest(
            string requestDirectory,
            string requestId)
        {
            this.requestDirectory = requestDirectory;
            this.requestId = requestId;
            requestIdPath =
                Path.Combine(requestDirectory, RequestIdFileName);
            certificationPath =
                Path.Combine(requestDirectory, CertificationFileName);
            failurePath =
                Path.Combine(requestDirectory, FailureFileName);
        }

        internal static bool IsEnabled(TestContext testContext)
        {
            return testContext != null &&
                testContext.Properties.Contains("SwitcherMode") &&
                IsTrue(Convert.ToString(
                    testContext.Properties["SwitcherMode"],
                    CultureInfo.InvariantCulture));
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

            var request = new SwitcherLaunchRequest(
                requestDirectory,
                Guid.NewGuid().ToString("D"));
            request.Create();
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

            ThrowIfFailed(processName);
            if (!File.Exists(certificationPath))
            {
                throw new InvalidOperationException(
                    processName +
                    " did not certify System composition before creating its window.");
            }

            Dictionary<string, string> certification =
                ReadResponse(certificationPath);
            string responseRequestId;
            string responseProcessId;
            int certifiedProcessId;
            if (!certification.TryGetValue(
                    RequestIdFileName,
                    out responseRequestId) ||
                !string.Equals(
                    responseRequestId,
                    requestId,
                    StringComparison.OrdinalIgnoreCase) ||
                !certification.TryGetValue("pid", out responseProcessId) ||
                !int.TryParse(
                    responseProcessId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out certifiedProcessId) ||
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
            DeleteIfPresent(certificationPath);
            DeleteIfPresent(certificationPath + ".tmp");
            DeleteIfPresent(failurePath);
            DeleteIfPresent(failurePath + ".tmp");
            DeleteIfPresent(requestIdPath);

            if (Directory.Exists(requestDirectory) &&
                !Directory.EnumerateFileSystemEntries(requestDirectory).Any())
            {
                Directory.Delete(requestDirectory);
            }
        }

        private static string AssemblyLocation
        {
            get
            {
                return typeof(SwitcherLaunchRequest).Assembly.Location;
            }
        }

        private void Create()
        {
            Directory.CreateDirectory(requestDirectory);
            DeleteIfPresent(certificationPath);
            DeleteIfPresent(certificationPath + ".tmp");
            DeleteIfPresent(failurePath);
            DeleteIfPresent(failurePath + ".tmp");
            File.WriteAllText(
                requestIdPath,
                requestId,
                new UTF8Encoding(false));
        }

        private static bool IsTrue(string value)
        {
            return string.Equals(
                    value,
                    "true",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "1", StringComparison.Ordinal);
        }

        private static Dictionary<string, string> ReadResponse(string path)
        {
            var response = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string line in
                ReadBoundedText(path).Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                response[line.Substring(0, separator)] =
                    line.Substring(separator + 1);
            }

            return response;
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
                if (stream.Length <= 0 ||
                    stream.Length > MaximumResponseLength)
                {
                    throw new InvalidDataException(
                        "A Switcher launch response has an invalid size.");
                }

                return reader.ReadToEnd();
            }
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
