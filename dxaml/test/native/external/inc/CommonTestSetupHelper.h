// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

#include <XamlTailored.h>
#include <RuntimeParameters.h>

namespace Microsoft { namespace UI { namespace Xaml { namespace Tests { namespace Common {

    class CommonTestSetupHelper
    {
    public:
        static void CommonTestClassSetup()
        {
            EnsureSwitcherMode();

            if(!_isInitialized)
            {
                if(IsInWPFHostingMode())
                {
                    ConfigureWin32Host();
                }
                _isInitialized = true;
            }
            test_infra::TestServices::EnsureInitialized();
        }
    private:
        inline static bool _isInitialized = false;
        inline static bool _isSwitcherInitialized = false;

        static void EnsureSwitcherMode()
        {
            if (_isSwitcherInitialized)
            {
                return;
            }

            WEX::Common::String switcherMode;
            if (FAILED(WEX::TestExecution::RuntimeParameters::TryGetValue(L"SwitcherMode", switcherMode))
                || (switcherMode.CompareNoCase(L"true") != 0 && switcherMode != L"1"))
            {
                return;
            }

            WEX::Common::String switcherLafToken;
            if (SUCCEEDED(WEX::TestExecution::RuntimeParameters::TryGetValue(L"SwitcherLafToken", switcherLafToken))
                && !switcherLafToken.IsEmpty())
            {
                try
                {
                    auto unlockResult = ::Windows::ApplicationModel::LimitedAccessFeatures::TryUnlockFeature(
                        ref new Platform::String(L"com.microsoft.windows.composition.engine"),
                        ref new Platform::String(static_cast<const wchar_t*>(switcherLafToken)),
                        ref new Platform::String(
                            L"8wekyb3d8bbwe has registered their use of "
                            L"com.microsoft.windows.composition.engine with Microsoft and agrees to the terms of use."));
                    WEX::Logging::Log::Comment(WEX::Common::String().Format(
                        L"SwitcherMode: LAF TryUnlockFeature status=%d",
                        static_cast<int>(unlockResult->Status)));
                }
                catch (Platform::Exception^ ex)
                {
                    WEX::Logging::Log::Comment(WEX::Common::String().Format(
                        L"SwitcherMode: LAF TryUnlockFeature threw hr=0x%08x",
                        ex->HResult));
                }
            }

            bool engaged = false;
            try
            {
                engaged = Microsoft::UI::Composition::CompositionEngine::TrySetProcessEngine(
                    Microsoft::UI::Composition::CompositionEngineType::System);
            }
            catch (Platform::Exception^ ex)
            {
                WEX::Logging::Log::Error(WEX::Common::String().Format(
                    L"SwitcherMode=true requested but CompositionEngine activation failed (hr=0x%08x).",
                    ex->HResult));
                throw;
            }

            if (!engaged)
            {
                WEX::Logging::Log::Error(
                    L"SwitcherMode=true requested but TrySetProcessEngine(System) did not engage.");
                throw ref new Platform::FailureException();
            }

            _isSwitcherInitialized = true;
            WEX::Logging::Log::Comment(
                L"SwitcherMode=true: switcher engaged in the hosted test process.");
        }

        static bool IsInWPFHostingMode()
        {
            WEX::Common::String hostingMode;
            WEX::TestExecution::RuntimeParameters::TryGetValue(L"HostingMode", hostingMode);
            return hostingMode.CompareNoCase(L"WPF") == 0;
        }

        static void ConfigureWin32Host()
        {
            auto hostingSetupHelper = ref new test_infra::Hosting::HostingHelpers::HostingSetupHelper();
            hostingSetupHelper->InitializeWPFHostFactory();
        }
    };


} } } } }