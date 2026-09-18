// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "pch.h"

#include <string>
#include <vector>
#include <wil\resource.h>

using namespace TaefHostApp;

namespace
{
    constexpr wchar_t CompositionEngineFeatureId[] =
        L"com.microsoft.windows.composition.engine";
    constexpr wchar_t CompositionEngineAttestation[] =
        L"8wekyb3d8bbwe has registered their use of com.microsoft.windows.composition.engine "
        L"with Microsoft and agrees to the terms of use.";
    constexpr wchar_t SwitcherLaunchRequestRelativePath[] =
        L".winui-switcher\\system-backend";
    constexpr LONGLONG MaximumSwitcherLafTokenSize = 64 * 1024;

    Platform::String^ ReadSwitcherLafToken()
    {
        std::vector<wchar_t> executablePath(32768);
        const DWORD executablePathLength = GetModuleFileNameW(
            nullptr,
            executablePath.data(),
            static_cast<DWORD>(executablePath.size()));
        if (executablePathLength == 0)
        {
            throw Platform::Exception::CreateException(
                HRESULT_FROM_WIN32(GetLastError()));
        }
        if (static_cast<size_t>(executablePathLength) >= executablePath.size())
        {
            throw Platform::Exception::CreateException(
                HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER));
        }

        std::wstring requestPath(executablePath.data(), executablePathLength);
        const size_t fileNameSeparator = requestPath.find_last_of(L'\\');
        if (fileNameSeparator == std::wstring::npos)
        {
            throw Platform::Exception::CreateException(E_UNEXPECTED);
        }
        requestPath.resize(fileNameSeparator + 1);
        requestPath.append(SwitcherLaunchRequestRelativePath);

        HANDLE requestFile = CreateFileW(
            requestPath.c_str(),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (requestFile == INVALID_HANDLE_VALUE)
        {
            const DWORD error = GetLastError();
            if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND)
            {
                return nullptr;
            }
            throw Platform::Exception::CreateException(HRESULT_FROM_WIN32(error));
        }
        wil::unique_hfile requestFileHandle(requestFile);

        LARGE_INTEGER tokenSize{};
        if (!GetFileSizeEx(requestFileHandle.get(), &tokenSize))
        {
            throw Platform::Exception::CreateException(
                HRESULT_FROM_WIN32(GetLastError()));
        }
        if (tokenSize.QuadPart <= 0 ||
            tokenSize.QuadPart > MaximumSwitcherLafTokenSize)
        {
            throw Platform::Exception::CreateException(E_ACCESSDENIED);
        }

        std::vector<char> tokenBytes(static_cast<size_t>(tokenSize.QuadPart));
        DWORD bytesRead = 0;
        if (!ReadFile(
                requestFileHandle.get(),
                tokenBytes.data(),
                static_cast<DWORD>(tokenBytes.size()),
                &bytesRead,
                nullptr))
        {
            throw Platform::Exception::CreateException(
                HRESULT_FROM_WIN32(GetLastError()));
        }
        if (bytesRead != static_cast<DWORD>(tokenBytes.size()))
        {
            throw Platform::Exception::CreateException(E_UNEXPECTED);
        }

        const int tokenCharacterCount = MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            tokenBytes.data(),
            static_cast<int>(tokenBytes.size()),
            nullptr,
            0);
        if (tokenCharacterCount <= 0)
        {
            throw Platform::Exception::CreateException(
                HRESULT_FROM_WIN32(GetLastError()));
        }

        std::vector<wchar_t> tokenCharacters(
            static_cast<size_t>(tokenCharacterCount) + 1);
        if (MultiByteToWideChar(
                CP_UTF8,
                MB_ERR_INVALID_CHARS,
                tokenBytes.data(),
                static_cast<int>(tokenBytes.size()),
                tokenCharacters.data(),
                tokenCharacterCount) != tokenCharacterCount)
        {
            throw Platform::Exception::CreateException(
                HRESULT_FROM_WIN32(GetLastError()));
        }
        tokenCharacters[tokenCharacterCount] = L'\0';
        return ref new Platform::String(tokenCharacters.data());
    }

    void SelectSystemCompositionIfRequested()
    {
        auto lafToken = ReadSwitcherLafToken();
        if (lafToken == nullptr)
        {
            return;
        }

        auto unlockResult = Windows::ApplicationModel::LimitedAccessFeatures::TryUnlockFeature(
            ref new Platform::String(CompositionEngineFeatureId),
            lafToken,
            ref new Platform::String(CompositionEngineAttestation));
        if (unlockResult->Status !=
            Windows::ApplicationModel::LimitedAccessFeatureStatus::Available)
        {
            throw Platform::Exception::CreateException(E_ACCESSDENIED);
        }

        if (!Microsoft::UI::Composition::CompositionEngine::TrySetProcessEngine(
                Microsoft::UI::Composition::CompositionEngineType::System))
        {
            throw Platform::Exception::CreateException(E_FAIL);
        }
    }
}

App::App()
{
    RequiresPointerMode = Microsoft::UI::Xaml::ApplicationRequiresPointerMode::WhenRequested;
    InitializeComponent();
}

void App::OnLaunched(Microsoft::UI::Xaml::LaunchActivatedEventArgs^ e)
{
    Microsoft::UI::Xaml::Window::Current->Activate();
    Microsoft::VisualStudio::TestPlatform::TestExecutor::WinRTCore::UnitTestClient::Run(e->Arguments);
}

int __cdecl main(Platform::Array<Platform::String^>^ arguments)
{
    (void)arguments;

    // Application::Start can create the first composition root. Select the backend
    // before entering XAML when the test runner supplied the launch request file.
    SelectSystemCompositionIfRequested();

    Microsoft::UI::Xaml::Application::Start(
        ref new Microsoft::UI::Xaml::ApplicationInitializationCallback(
            [](Microsoft::UI::Xaml::ApplicationInitializationCallbackParams^ parameters)
            {
                (void)parameters;
                ref new App();
            }));
}
