// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "pch.h"
#include <werapi.h>

using namespace TaefHostApp;

namespace
{
    constexpr wchar_t TestInfrastructureRegistryPath[] =
        L"Software\\Microsoft\\WinUITestInfrastructure";
    constexpr wchar_t UseSystemCompositionEngineValueName[] =
        L"UseSystemCompositionEngine";
#if defined(_WIN64)
    constexpr DWORD TestInfrastructureRegistryView =
        RRF_SUBKEY_WOW6464KEY;
#else
    constexpr DWORD TestInfrastructureRegistryView =
        RRF_SUBKEY_WOW6432KEY;
#endif
    constexpr wchar_t SwitcherCertificationEnvironmentVariable[] =
        L"WINUI_SWITCHER_SYSTEM_COMPOSITION_CERTIFIED";
    constexpr wchar_t SwitcherDumpRegistrationEnvironmentVariable[] =
        L"WINUI_SWITCHER_WER_LOCAL_DUMP_REGISTRATION";
    constexpr wchar_t SwitcherDumpRelativePath[] =
        L"SwitcherCrashDumps";
    bool systemCompositionConfigured = false;
    bool systemCompositionCertified = false;

    bool IsSystemCompositionRequested()
    {
        DWORD requested = 0;
        DWORD requestedSize = sizeof(requested);
        const LSTATUS status = RegGetValueW(
            HKEY_LOCAL_MACHINE,
            TestInfrastructureRegistryPath,
            UseSystemCompositionEngineValueName,
            RRF_RT_REG_DWORD | TestInfrastructureRegistryView,
            nullptr,
            &requested,
            &requestedSize);
        if (status != ERROR_SUCCESS)
        {
            if (status == ERROR_FILE_NOT_FOUND ||
                status == ERROR_PATH_NOT_FOUND)
            {
                return false;
            }

            throw Platform::Exception::CreateException(
                HRESULT_FROM_WIN32(status));
        }
        if (requested != 1)
        {
            throw Platform::Exception::CreateException(
                HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
        }

        return true;
    }

    bool SelectSystemCompositionIfRequested()
    {
        if (systemCompositionConfigured)
        {
            return true;
        }

        if (!IsSystemCompositionRequested())
        {
            return false;
        }

        if (!Microsoft::UI::Composition::CompositionEngine::TrySetProcessEngine(
                Microsoft::UI::Composition::CompositionEngineType::System))
        {
            throw Platform::Exception::CreateException(E_FAIL);
        }

        const HRESULT dumpRegistrationResult =
            WerRegisterAppLocalDump(SwitcherDumpRelativePath);
        wchar_t formattedDumpRegistrationResult[11]{};
        if (swprintf_s(
                formattedDumpRegistrationResult,
                L"0x%08X",
                static_cast<unsigned int>(dumpRegistrationResult)) < 0)
        {
            throw Platform::Exception::CreateException(E_UNEXPECTED);
        }
        if (!SetEnvironmentVariableW(
                SwitcherDumpRegistrationEnvironmentVariable,
                formattedDumpRegistrationResult))
        {
            throw Platform::Exception::CreateException(
                HRESULT_FROM_WIN32(GetLastError()));
        }

        systemCompositionConfigured = true;
        return true;
    }

    void CertifySystemComposition()
    {
        if (systemCompositionCertified)
        {
            return;
        }
        if (!systemCompositionConfigured)
        {
            throw Platform::Exception::CreateException(E_UNEXPECTED);
        }

        auto probeElement = ref new Microsoft::UI::Xaml::Controls::Grid();
        auto compositor =
            Microsoft::UI::Xaml::Hosting::ElementCompositionPreview::
                GetElementVisual(probeElement)->Compositor;
        Platform::Object^ systemCompositor =
            Microsoft::UI::Composition::CompositionEngine::GetForSystemEngine(
                compositor);
        if (dynamic_cast<Windows::UI::Composition::Compositor^>(
                systemCompositor) == nullptr)
        {
            throw Platform::Exception::CreateException(E_NOINTERFACE);
        }

        systemCompositionCertified = true;
    }
}

App::App()
{
    RequiresPointerMode = Microsoft::UI::Xaml::ApplicationRequiresPointerMode::WhenRequested;
    InitializeComponent();
}

void App::OnLaunched(Microsoft::UI::Xaml::LaunchActivatedEventArgs^ e)
{
    if (!SetEnvironmentVariableW(
            SwitcherCertificationEnvironmentVariable,
            nullptr))
    {
        const DWORD error = GetLastError();
        if (error != ERROR_ENVVAR_NOT_FOUND)
        {
            throw Platform::Exception::CreateException(
                HRESULT_FROM_WIN32(error));
        }
    }

    Microsoft::UI::Xaml::Window::Current->Activate();
    if (systemCompositionConfigured)
    {
        CertifySystemComposition();
        if (!SetEnvironmentVariableW(
                SwitcherCertificationEnvironmentVariable,
                L"native"))
        {
            const DWORD error = GetLastError();
            throw Platform::Exception::CreateException(
                HRESULT_FROM_WIN32(error));
        }
    }
    Microsoft::VisualStudio::TestPlatform::TestExecutor::WinRTCore::UnitTestClient::Run(e->Arguments);
}

int __cdecl main(Platform::Array<Platform::String^>^ arguments)
{
    (void)arguments;

    Microsoft::UI::Xaml::Application::Start(
        ref new Microsoft::UI::Xaml::ApplicationInitializationCallback(
            [](Microsoft::UI::Xaml::ApplicationInitializationCallbackParams^ parameters)
            {
                (void)parameters;
                SelectSystemCompositionIfRequested();
                ref new App();
            }));
}
