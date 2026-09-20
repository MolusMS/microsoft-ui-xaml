// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common;

using WEX.TestExecution;
using WEX.TestExecution.Markup;
using WEX.Logging.Interop;
using Microsoft.UI.Xaml.Tests.Common;

namespace MUXControlsTestApp
{
    // This is marked as a test class to make sure our AssemblyInitialize
    // fixture gets executed.  It won't actually host any tests.
    [TestClass]
    public class WaitForDebugger
    {
        [AssemblyInitialize]
        [TestProperty("HelixWorkItemCreation", "CreateWorkItemPerTestClass")]
        public static void AssemblyInitialize(TestContext testContext)
        {
            bool hasSwitcherMode =
                testContext.Properties.Contains("SwitcherMode");
            bool switcherModeEnabled =
                hasSwitcherMode &&
                SwitcherComposition.IsTrue(
                    Convert.ToString(testContext.Properties["SwitcherMode"]));
            bool hasSwitcherLafToken =
                testContext.Properties.Contains("SwitcherLafToken") &&
                !string.IsNullOrEmpty(
                    Convert.ToString(testContext.Properties["SwitcherLafToken"]));
            if (hasSwitcherMode || hasSwitcherLafToken)
            {
                Verify.IsTrue(
                    switcherModeEnabled,
                    "IXMP must receive SwitcherMode=true with its LAF credential.");
                Verify.IsTrue(
                    SwitcherComposition.IsRequested,
                    "IXMP must receive Switcher activation parameters.");
                Verify.IsTrue(
                    SwitcherComposition.IsConfigured,
                    "IXMP Switcher mode must be certified before tests run.");
                Log.Comment(
                    "SwitcherMode: IXMPTestApp process selected and certified System composition.");
            }

            if (testContext.Properties.Contains("WaitForDebugger") || testContext.Properties.Contains("WaitForAppDebugger"))
            {
                var processId = Windows.System.Diagnostics.ProcessDiagnosticInfo.GetForCurrentProcess().ProcessId;
                var waitEvent = new AutoResetEvent(false);

                while (!System.Diagnostics.Debugger.IsAttached)
                {
                    Log.Comment(string.Format("Waiting for a debugger to attach (processId = {0})...", processId));
                    Windows.System.Threading.ThreadPoolTimer.CreateTimer((timer) => { waitEvent.Set(); }, TimeSpan.FromSeconds(1));
                    waitEvent.WaitOne();
                }

                System.Diagnostics.Debugger.Break();
            }
        }
    }
}
