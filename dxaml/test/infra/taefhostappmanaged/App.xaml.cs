// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Private.Infrastructure.Hosting;
using Windows.ApplicationModel;

namespace TaefHostAppManaged
{
    sealed partial class App : Application
    {
        private const string SwitcherCertificationEnvironmentVariable =
            "WINUI_SWITCHER_SYSTEM_COMPOSITION_CERTIFIED";

        public App()
        {
            RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Environment.SetEnvironmentVariable(
                SwitcherCertificationEnvironmentVariable,
                null,
                EnvironmentVariableTarget.Process);

            var switcherLafToken = Program.ReadSwitcherLafToken();
            if (switcherLafToken != null)
            {
                // Select before window activation, matching the native packaged host.
                CompositionSwitcher.Configure(switcherLafToken);
            }

            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (e.UWPLaunchActivatedEventArgs.PrelaunchActivated == false)
            {
                Window.Current.Activate();

                if (CompositionSwitcher.IsConfigured)
                {
                    CompositionSwitcher.Certify();
                    Environment.SetEnvironmentVariable(
                        SwitcherCertificationEnvironmentVariable,
                        "managed",
                        EnvironmentVariableTarget.Process);
                }

                if (e.Arguments.Length > 1)
                {
                    Microsoft.VisualStudio.TestPlatform.TestExecutor.UnitTestClient.Run(e.Arguments);
                }
                else if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }
            }
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }
    }
}
