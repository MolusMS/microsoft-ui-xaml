// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Storage;

namespace Microsoft.UI.Xaml.Tests.Common
{
    internal static class SwitcherComposition
    {
        private const string TestInfrastructureRegistryPath =
            @"Software\Microsoft\WinUITestInfrastructure";
        private const string UseSystemCompositionEngineValueName =
            "UseSystemCompositionEngine";
        private const string RequestDirectoryName = ".winui-switcher";
        private const string RequestIdFileName = "request-id";
        private const string CertificationFileName = "certified-response";
        private const string FailureFileName = "failure-response";
        private const int MaximumRequestValueLength = 4096;
        private const int MaximumResponseLength = 4096;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorInvalidData = 13;
        private const uint RrfRtRegDword = 0x00000010;
        private const uint RrfSubKeyWow6464Key = 0x00010000;
        private const uint RrfSubKeyWow6432Key = 0x00020000;
        private static readonly IntPtr HKeyLocalMachine =
            new IntPtr(unchecked((int)0x80000002));
        private static readonly object SyncRoot = new object();
        private static bool switcherRequested;
        private static bool systemCompositionSelected;
        private static bool systemCompositionCertified;
        private static string pendingRequestId;
        private static string pendingCertificationPath;
        private static string pendingFailurePath;

        [DllImport(
            "api-ms-win-core-registry-l1-1-0.dll",
            CharSet = CharSet.Unicode,
            EntryPoint = "RegGetValueW")]
        private static extern int RegGetValue(
            IntPtr key,
            string subKey,
            string value,
            uint flags,
            out uint type,
            out uint data,
            ref uint dataSize);

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

        internal static bool ConfigureFromRegistryRequest()
        {
            lock (SyncRoot)
            {
                if (systemCompositionSelected)
                {
                    return true;
                }

                if (!IsSystemCompositionRequested())
                {
                    return false;
                }

                switcherRequested = true;
                PrepareCertificationResponse();

                try
                {
                    if (!CompositionEngine.TrySetProcessEngine(
                            CompositionEngineType.System))
                    {
                        throw new InvalidOperationException(
                            "TrySetProcessEngine(System) did not engage in the test application process.");
                    }
                }
                catch (Exception exception)
                {
                    WritePendingFailure("selection", exception);
                    throw;
                }

                systemCompositionSelected = true;
                return true;
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
                    var probeElement = new Grid();
                    Compositor compositor =
                        ElementCompositionPreview.GetElementVisual(
                            probeElement).Compositor;
                    object systemCompositor =
                        CompositionEngine.GetForSystemEngine(compositor);
                    if (!(systemCompositor is
                        global::Windows.UI.Composition.Compositor))
                    {
                        throw new InvalidOperationException(
                            "GetForSystemEngine did not return a Windows.UI.Composition.Compositor.");
                    }

                    WritePendingCertification();
                    systemCompositionCertified = true;
                }
                catch (Exception exception)
                {
                    WritePendingFailure("certification", exception);
                    throw;
                }
            }
        }

        internal static bool IsTrue(string value)
        {
            return string.Equals(
                       value,
                       "true",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "1", StringComparison.Ordinal);
        }

        private static bool IsSystemCompositionRequested()
        {
            uint type;
            uint requested;
            uint requestedSize = sizeof(uint);
            uint registryView =
                IntPtr.Size == 8
                    ? RrfSubKeyWow6464Key
                    : RrfSubKeyWow6432Key;
            int status = RegGetValue(
                HKeyLocalMachine,
                TestInfrastructureRegistryPath,
                UseSystemCompositionEngineValueName,
                RrfRtRegDword | registryView,
                out type,
                out requested,
                ref requestedSize);
            if (status == ErrorFileNotFound || status == ErrorPathNotFound)
            {
                return false;
            }
            if (status != 0)
            {
                throw Marshal.GetExceptionForHR(HResultFromWin32(status));
            }
            if (requested != 1)
            {
                throw Marshal.GetExceptionForHR(
                    HResultFromWin32(ErrorInvalidData));
            }

            return true;
        }

        private static void PrepareCertificationResponse()
        {
            string requestDirectory = GetRequestDirectory();
            string requestIdPath =
                Path.Combine(requestDirectory, RequestIdFileName);
            if (!File.Exists(requestIdPath))
            {
                return;
            }

            string requestId = ReadBoundedText(requestIdPath, RequestIdFileName);
            Guid parsedRequestId;
            if (!Guid.TryParse(requestId, out parsedRequestId) ||
                parsedRequestId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "The Switcher launch request ID is invalid.");
            }

            pendingRequestId = parsedRequestId.ToString("D");
            pendingCertificationPath =
                Path.Combine(requestDirectory, CertificationFileName);
            pendingFailurePath =
                Path.Combine(requestDirectory, FailureFileName);
            DeleteIfPresent(pendingCertificationPath);
            DeleteIfPresent(pendingFailurePath);
        }

        private static string GetRequestDirectory()
        {
            try
            {
                return Path.Combine(
                    ApplicationData.Current.LocalFolder.Path,
                    RequestDirectoryName);
            }
            catch (InvalidOperationException)
            {
                return Path.Combine(
                    AppContext.BaseDirectory,
                    RequestDirectoryName);
            }
            catch (COMException)
            {
                return Path.Combine(
                    AppContext.BaseDirectory,
                    RequestDirectoryName);
            }
        }

        private static string ReadBoundedText(
            string path,
            string description)
        {
            FileInfo file = new FileInfo(path);
            if (file.Length <= 0 || file.Length > MaximumRequestValueLength)
            {
                throw new InvalidOperationException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "The Switcher {0} file has an invalid length.",
                        description));
            }

            return File.ReadAllText(path, Encoding.UTF8).Trim();
        }

        private static void WritePendingCertification()
        {
            if (string.IsNullOrEmpty(pendingCertificationPath))
            {
                return;
            }

            WriteResponse(
                pendingCertificationPath,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "request-id={0}{1}pid={2}{1}",
                    pendingRequestId,
                    Environment.NewLine,
                    GetCurrentProcessId()));
            DeleteIfPresent(pendingFailurePath);
        }

        private static void WritePendingFailure(
            string stage,
            Exception exception)
        {
            if (string.IsNullOrEmpty(pendingFailurePath))
            {
                return;
            }

            string prefix = string.Format(
                CultureInfo.InvariantCulture,
                "request-id={0}{1}pid={2}{1}stage={3}{1}error=",
                pendingRequestId,
                Environment.NewLine,
                GetCurrentProcessId(),
                stage);
            int maximumErrorByteCount =
                MaximumResponseLength -
                Encoding.UTF8.GetByteCount(prefix) -
                Encoding.UTF8.GetByteCount(Environment.NewLine);
            WriteResponse(
                pendingFailurePath,
                prefix +
                TruncateUtf8(
                    exception.ToString(),
                    Math.Max(0, maximumErrorByteCount)) +
                Environment.NewLine);
            DeleteIfPresent(pendingCertificationPath);
        }

        private static string TruncateUtf8(
            string value,
            int maximumByteCount)
        {
            var encoding = new UTF8Encoding(false);
            if (maximumByteCount <= 0 ||
                string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            if (encoding.GetByteCount(value) <= maximumByteCount)
            {
                return value;
            }

            char[] characters = value.ToCharArray();
            byte[] bytes = new byte[maximumByteCount];
            int charactersUsed;
            int bytesUsed;
            bool completed;
            encoding.GetEncoder().Convert(
                characters,
                0,
                characters.Length,
                bytes,
                0,
                bytes.Length,
                true,
                out charactersUsed,
                out bytesUsed,
                out completed);
            return encoding.GetString(bytes, 0, bytesUsed);
        }

        private static void WriteResponse(string path, string contents)
        {
            string temporaryPath = path + ".tmp";
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    contents,
                    new UTF8Encoding(false));
                DeleteIfPresent(path);
                File.Move(temporaryPath, path);
            }
            finally
            {
                DeleteIfPresent(temporaryPath);
            }
        }

        private static void DeleteIfPresent(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static int HResultFromWin32(int error)
        {
            return error <= 0
                ? error
                : unchecked((int)(0x80070000u | ((uint)error & 0xffffu)));
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();
    }
}
