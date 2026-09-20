// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Common;
using System.Diagnostics;

using WEX.TestExecution;
using WEX.TestExecution.Markup;
using WEX.Logging.Interop;
using MUXControlsTestApp.Utilities;
using Microsoft.UI.Xaml.Tests.Common;

namespace MUXControlsTestApp
{
    // This is marked as a test class to make sure our AssemblyInitialize and
    // AssemblyCleanup fixtures get executed.  It won't actually host any tests.
    [TestClass]
    public class ApiTestAssemblyHandling
    {
        [AssemblyInitialize]
        [TestProperty("CoreClrProfile", "localDotNet")]
        [TestProperty("Classification", "Integration")]
        [TestProperty("TestPass:IncludeOnlyOn", "Desktop")] //24049610
        [TestProperty("TestPass:MinOSVer", WindowsOSVersion.RS4)]
        [TestProperty("HelixWorkItemCreation", "CreateWorkItemPerTestClass")]
        [TestProperty("IsolationLevel", "Class")]
        [TestProperty("RunAs", "UAP")]
        [TestProperty("UAP:Host", "PackagedCWA")]
        public static void AssemblyInitialize(TestContext testContext)
        {
            bool switcherRequested =
                testContext.Properties.Contains("SwitcherMode") &&
                SwitcherComposition.IsTrue(
                    Convert.ToString(testContext.Properties["SwitcherMode"]));
            bool switcherExpected =
                testContext.Properties.Contains("SwitcherLafToken") ||
                !string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable(
                        "SWITCHER_LAF_TOKEN",
                        EnvironmentVariableTarget.Process));
            if (switcherExpected)
            {
                Verify.IsTrue(
                    switcherRequested,
                    "MUXControls API tests must receive SwitcherMode when the pipeline provides its credential.");
            }

            string switcherLafToken = switcherRequested &&
                testContext.Properties.Contains("SwitcherLafToken")
                ? Convert.ToString(testContext.Properties["SwitcherLafToken"])
                : null;

            if (testContext.Properties.Contains("WaitForDebugger") || testContext.Properties.Contains("WaitForAppDebugger"))
            {
                var processId = Windows.System.Diagnostics.ProcessDiagnosticInfo.GetForCurrentProcess().ProcessId;
                var waitEvent = new AutoResetEvent(false);

                while (!IsDebuggerPresent())
                {
                    Log.Comment(string.Format("Waiting for a debugger to attach (processId = {0})...", processId));
                    Windows.System.Threading.ThreadPoolTimer.CreateTimer((timer) => { waitEvent.Set(); }, TimeSpan.FromSeconds(1));
                    waitEvent.WaitOne();
                }

                DebugBreak();
            }

            // This is the entry point for API tests rather than Program.Main, so we'll call that on another thread
            // in order to initialize the XAML application for API testing.  It needs to be on its own thread because
            // it doesn't return - it contains the application loop.
#nullable enable
            Exception? appStartupException = null;
            var appStartupFailedEvent = new ManualResetEvent(false);
            _ = ThreadPool.QueueUserWorkItem((object? param) =>
            {
                try
                {
                    Program.Run(
                        Array.Empty<string>(),
                        switcherLafToken,
                        true);
                }
                catch (Exception exception)
                {
                    appStartupException = exception;
                    appStartupFailedEvent.Set();
                }
            });
#nullable restore

            int startupResult = WaitHandle.WaitAny(
                new WaitHandle[]
                {
                    App.AppLaunchedEvent,
                    appStartupFailedEvent
                });
            if (startupResult == 1)
            {
                throw new InvalidOperationException(
                    "MUXControlsTestApp API startup failed before XAML activation.",
                    appStartupException);
            }
            if (switcherRequested)
            {
                Verify.IsTrue(
                    SwitcherComposition.IsConfigured,
                    "MUXControlsTestApp API process must certify System composition before tests run.");
                Log.Comment(
                    "SwitcherMode: MUXControlsTestApp API process selected and certified System composition.");
            }
        }

        [DllImport("kernel32.dll")]
        private static extern bool IsDebuggerPresent();

        [DllImport("kernel32.dll")]
        private static extern void DebugBreak();

        [AssemblyCleanup]
        public static void AssemblyCleanup()
        {
            // Closing a desktop application using Application.Close() doesn't presently work, so we'll just kill the app instead.
            // TODO 27390753: Remove this function once the bug on this is fixed.
#nullable enable
            _ = ThreadPool.QueueUserWorkItem((object? param) => Process.GetCurrentProcess().Kill());
#nullable restore
        }
    }
}
