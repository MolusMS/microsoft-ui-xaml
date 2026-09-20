// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "pch.h"
#include "SwitcherComposition.h"

#include <Microsoft.UI.Composition.h>
#include <winrt/Microsoft.UI.Composition.h>
#include <winrt/Windows.ApplicationModel.h>
#include <wil/resource.h>
#include <wil/result.h>
#include <wrl/client.h>
#include <wrl/wrappers/corewrappers.h>

#include <array>
#include <string>

namespace
{
    constexpr wchar_t FeatureId[] = L"com.microsoft.windows.composition.engine";
    constexpr wchar_t Attestation[] =
        L"8wekyb3d8bbwe has registered their use of com.microsoft.windows.composition.engine "
        L"with Microsoft and agrees to the terms of use.";
    constexpr wchar_t RequestDirectoryName[] = L".winui-switcher";
    constexpr wchar_t TokenFileName[] = L"system-backend";
    constexpr wchar_t RequestIdFileName[] = L"request-id";
    constexpr wchar_t CertificationFileName[] = L"system-backend.certified";
    constexpr DWORD MaximumRequestValueSize = 64 * 1024;

    std::wstring GetExecutableDirectory()
    {
        std::array<wchar_t, 32768> path{};
        const DWORD pathLength = GetModuleFileNameW(
            nullptr,
            path.data(),
            static_cast<DWORD>(path.size()));
        if (pathLength == 0 ||
            pathLength == static_cast<DWORD>(path.size()))
        {
            winrt::throw_last_error();
        }

        std::wstring directory(path.data(), pathLength);
        const auto separator = directory.find_last_of(L"\\/");
        if (separator == std::wstring::npos)
        {
            throw winrt::hresult_error(
                E_UNEXPECTED,
                L"The test application executable path has no directory.");
        }

        directory.resize(separator);
        return directory;
    }

    std::string ReadBoundedUtf8(const std::wstring& path)
    {
        wil::unique_hfile file{ CreateFileW(
            path.c_str(),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr) };
        THROW_LAST_ERROR_IF(!file);

        LARGE_INTEGER fileSize{};
        THROW_IF_WIN32_BOOL_FALSE(GetFileSizeEx(file.get(), &fileSize));
        THROW_HR_IF(
            HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
            fileSize.QuadPart <= 0 ||
                fileSize.QuadPart > MaximumRequestValueSize);

        std::string value(static_cast<size_t>(fileSize.QuadPart), '\0');
        DWORD bytesRead = 0;
        THROW_IF_WIN32_BOOL_FALSE(ReadFile(
            file.get(),
            value.data(),
            static_cast<DWORD>(value.size()),
            &bytesRead,
            nullptr));
        THROW_HR_IF(
            HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
            bytesRead != static_cast<DWORD>(value.size()));
        return value;
    }

    std::wstring Utf8ToWide(const std::string& value)
    {
        const int characterCount = MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            nullptr,
            0);
        THROW_LAST_ERROR_IF(characterCount == 0);

        std::wstring converted(static_cast<size_t>(characterCount), L'\0');
        THROW_LAST_ERROR_IF(MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            converted.data(),
            characterCount) != characterCount);
        return converted;
    }

    void WriteCertification(
        const std::wstring& path,
        const std::string& requestId)
    {
        const std::string certification =
            requestId + "\r\n" + std::to_string(GetCurrentProcessId());
        wil::unique_hfile file{ CreateFileW(
            path.c_str(),
            GENERIC_WRITE,
            0,
            nullptr,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr) };
        THROW_LAST_ERROR_IF(!file);

        DWORD bytesWritten = 0;
        THROW_IF_WIN32_BOOL_FALSE(WriteFile(
            file.get(),
            certification.data(),
            static_cast<DWORD>(certification.size()),
            &bytesWritten,
            nullptr));
        THROW_HR_IF(
            HRESULT_FROM_WIN32(ERROR_WRITE_FAULT),
            bytesWritten != static_cast<DWORD>(certification.size()));
        THROW_IF_WIN32_BOOL_FALSE(FlushFileBuffers(file.get()));
    }
}

bool SwitcherComposition::ConfigureAndCertifyFromLaunchRequest()
{
    const std::wstring requestDirectory =
        GetExecutableDirectory() + L"\\" + RequestDirectoryName;
    const DWORD attributes = GetFileAttributesW(requestDirectory.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES)
    {
        const DWORD error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND)
        {
            return false;
        }

        THROW_WIN32(error);
    }
    THROW_HR_IF(
        HRESULT_FROM_WIN32(ERROR_DIRECTORY),
        (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0);

    const std::string requestId = ReadBoundedUtf8(
        requestDirectory + L"\\" + RequestIdFileName);
    std::wstring bracedRequestId = L"{";
    bracedRequestId += Utf8ToWide(requestId);
    bracedRequestId += L"}";
    GUID parsedRequestId{};
    THROW_HR_IF(
        HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
        FAILED(IIDFromString(bracedRequestId.c_str(), &parsedRequestId)));

    const std::wstring lafToken = Utf8ToWide(ReadBoundedUtf8(
        requestDirectory + L"\\" + TokenFileName));
    const auto unlockResult =
        winrt::Windows::ApplicationModel::LimitedAccessFeatures::TryUnlockFeature(
            FeatureId,
            lafToken,
            Attestation);
    THROW_HR_IF(
        E_ACCESSDENIED,
        unlockResult.Status() !=
            winrt::Windows::ApplicationModel::LimitedAccessFeatureStatus::Available);

    Microsoft::WRL::ComPtr<
        ABI::Microsoft::UI::Composition::ICompositionEngineStatics>
        compositionEngineStatics;
    THROW_IF_FAILED(RoGetActivationFactory(
        Microsoft::WRL::Wrappers::HStringReference(
            RuntimeClass_Microsoft_UI_Composition_CompositionEngine).Get(),
        IID_PPV_ARGS(compositionEngineStatics.ReleaseAndGetAddressOf())));

    boolean processEngineSet = false;
    THROW_IF_FAILED(compositionEngineStatics->TrySetProcessEngine(
        ABI::Microsoft::UI::Composition::CompositionEngineType_System,
        &processEngineSet));
    THROW_HR_IF(E_FAIL, !processEngineSet);

    auto compositor = winrt::Microsoft::UI::Composition::Compositor();
    Microsoft::WRL::ComPtr<::IInspectable> systemCompositorAbi;
    THROW_IF_FAILED(compositionEngineStatics->GetForSystemEngine(
        reinterpret_cast<::IInspectable*>(winrt::get_abi(compositor)),
        systemCompositorAbi.ReleaseAndGetAddressOf()));
    winrt::Windows::Foundation::IInspectable systemCompositor{
        systemCompositorAbi.Detach(),
        winrt::take_ownership_from_abi };
    THROW_HR_IF(
        E_FAIL,
        !systemCompositor.try_as<winrt::Windows::UI::Composition::Compositor>());

    WriteCertification(
        requestDirectory + L"\\" + CertificationFileName,
        requestId);
    return true;
}
