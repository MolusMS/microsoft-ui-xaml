// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "pch.h"
#include "SwitcherTestProcess.h"

#include <RuntimeParameters.h>
#include <mutex>
#include <string>
#include <thread>

namespace
{
    bool systemCompositionSelected = false;
    bool systemCompositionCertified = false;
    std::mutex systemCompositionMutex;

    bool IsTrue(WEX::Common::String& value)
    {
        const auto buffer = reinterpret_cast<const wchar_t*>(value.GetBuffer());
        return buffer != nullptr &&
            (_wcsicmp(buffer, L"true") == 0 || wcscmp(buffer, L"1") == 0);
    }

    void Fail(HRESULT hr, const wchar_t* message)
    {
        WEX::Logging::Log::Error(message);
        throw Platform::Exception::CreateException(hr);
    }
}

void SwitcherTestProcess::SelectSystemCompositionIfRequested()
{
    std::lock_guard<std::mutex> lock(systemCompositionMutex);

    WEX::Common::String switcherMode;
    const bool switcherRequested =
        SUCCEEDED(WEX::TestExecution::RuntimeParameters::TryGetValue(
            L"SwitcherMode",
            switcherMode)) &&
        IsTrue(switcherMode);
    WEX::Common::String switcherLafToken;
    const bool hasSwitcherLafToken =
        SUCCEEDED(WEX::TestExecution::RuntimeParameters::TryGetValue(
            L"SwitcherLafToken",
            switcherLafToken)) &&
        !switcherLafToken.IsEmpty();
    const bool switcherExpected = hasSwitcherLafToken ||
        GetEnvironmentVariableW(L"SWITCHER_LAF_TOKEN", nullptr, 0) > 0;
    if (switcherExpected && !switcherRequested)
    {
        Fail(
            E_INVALIDARG,
            L"Win32Explicit tests did not receive SwitcherMode.");
    }
    if (!switcherRequested)
    {
        return;
    }

    if (systemCompositionSelected)
    {
        return;
    }

    if (!hasSwitcherLafToken)
    {
        Fail(
            E_INVALIDARG,
            L"Win32Explicit Switcher mode requires a non-empty LAF token.");
    }

    const std::wstring lafToken(
        reinterpret_cast<const wchar_t*>(switcherLafToken.GetBuffer()));
    HRESULT selectionResult = E_UNEXPECTED;
    std::thread selectionThread([&]()
    {
        const HRESULT apartmentResult = RoInitialize(RO_INIT_MULTITHREADED);
        if (FAILED(apartmentResult))
        {
            selectionResult = apartmentResult;
            return;
        }

        try
        {
            auto unlockResult =
                ::Windows::ApplicationModel::LimitedAccessFeatures::TryUnlockFeature(
                    ref new Platform::String(L"com.microsoft.windows.composition.engine"),
                    ref new Platform::String(lafToken.c_str()),
                    ref new Platform::String(
                        L"8wekyb3d8bbwe has registered their use of "
                        L"com.microsoft.windows.composition.engine with Microsoft and agrees to the terms of use."));
            if (unlockResult->Status !=
                ::Windows::ApplicationModel::LimitedAccessFeatureStatus::Available)
            {
                selectionResult = E_ACCESSDENIED;
            }
            else if (!Microsoft::UI::Composition::CompositionEngine::TrySetProcessEngine(
                    Microsoft::UI::Composition::CompositionEngineType::System))
            {
                selectionResult = E_FAIL;
            }
            else
            {
                selectionResult = S_OK;
            }
        }
        catch (Platform::Exception^ exception)
        {
            selectionResult = exception->HResult;
        }
        catch (...)
        {
            selectionResult = E_FAIL;
        }

        RoUninitialize();
    });
    selectionThread.join();
    if (FAILED(selectionResult))
    {
        Fail(
            selectionResult,
            L"Win32Explicit System composition selection failed.");
    }

    systemCompositionSelected = true;
    WEX::Logging::Log::Comment(
        L"SwitcherMode: Win32Explicit test process selected System composition; certification is deferred to its initialized XAML thread.");
}

void SwitcherTestProcess::CertifySystemCompositionIfRequested()
{
    std::lock_guard<std::mutex> lock(systemCompositionMutex);

    if (!systemCompositionSelected ||
        systemCompositionCertified)
    {
        return;
    }

    HRESULT certificationResult = E_FAIL;
    try
    {
        WEX::Logging::Log::Comment(
            L"SwitcherMode: Win32Explicit test process is certifying System composition on its initialized XAML thread.");
        auto probeElement =
            ref new Microsoft::UI::Xaml::Controls::Grid();
        auto visual =
            Microsoft::UI::Xaml::Hosting::ElementCompositionPreview::
                GetElementVisual(probeElement);
        if (visual == nullptr)
        {
            certificationResult = E_NOINTERFACE;
        }
        else
        {
            auto compositor = visual->Compositor;
            Platform::Object^ systemCompositor =
                Microsoft::UI::Composition::CompositionEngine::
                    GetForSystemEngine(compositor);
            if (systemCompositor == nullptr)
            {
                certificationResult = E_NOINTERFACE;
            }
            else
            {
                auto systemInspectable =
                    reinterpret_cast<IInspectable*>(systemCompositor);
                wil::unique_hstring runtimeClassName;
                certificationResult =
                    systemInspectable->GetRuntimeClassName(
                        runtimeClassName.put());
                if (SUCCEEDED(certificationResult))
                {
                    UINT32 length = 0;
                    PCWSTR runtimeClassNameRaw =
                        WindowsGetStringRawBuffer(
                            runtimeClassName.get(),
                            &length);
                    const std::wstring actualRuntimeClassName(
                        runtimeClassNameRaw,
                        length);
                    WEX::Logging::Log::Comment(
                        WEX::Common::String().Format(
                            L"SwitcherMode: Win32Explicit system-engine object class name: '%s'.",
                            actualRuntimeClassName.c_str()));
                    certificationResult =
                        actualRuntimeClassName ==
                            L"Windows.UI.Composition.Compositor"
                        ? S_OK
                        : HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
                }
            }
        }
    }
    catch (Platform::Exception^ exception)
    {
        certificationResult = exception->HResult;
    }
    catch (...)
    {
        certificationResult = E_FAIL;
    }

    if (FAILED(certificationResult))
    {
        Fail(
            certificationResult,
            L"Win32Explicit System composition certification failed.");
    }

    systemCompositionCertified = true;
    WEX::Logging::Log::Comment(
        L"SwitcherMode: Win32Explicit test process selected and certified System composition.");
}
