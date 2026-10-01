// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "pch.h"
#include "SwitcherComposition.h"

#include <Microsoft.UI.Composition.h>
#include <winrt/Microsoft.UI.Composition.h>
#include <wil/resource.h>
#include <wil/result.h>
#include <wrl/client.h>
#include <wrl/wrappers/corewrappers.h>

#include <array>
#include <string>

namespace
{
    constexpr wchar_t TestInfrastructureRegistryPath[] =
        L"Software\\Microsoft\\WinUITestInfrastructure";
    constexpr wchar_t UseSystemCompositionEngineValueName[] =
        L"UseSystemCompositionEngine";
    constexpr wchar_t RequestDirectoryName[] = L".winui-switcher";
    constexpr wchar_t RequestIdFileName[] = L"request-id";
    constexpr wchar_t CertificationFileName[] = L"certified-response";
    constexpr wchar_t FailureFileName[] = L"failure-response";
    constexpr DWORD MaximumRequestValueSize = 4096;

    bool systemCompositionConfigured = false;
    bool systemCompositionCertified = false;
    std::wstring certificationPath;
    std::wstring failurePath;
    std::string configuredRequestId;

    void DeleteIfPresent(const std::wstring& path);

    bool IsSystemCompositionRequested()
    {
        DWORD requested = 0;
        DWORD requestedSize = sizeof(requested);
        DWORD valueType = 0;
        const DWORD registryView =
#ifdef _WIN64
            RRF_SUBKEY_WOW6464KEY;
#else
            RRF_SUBKEY_WOW6432KEY;
#endif
        const LSTATUS status = RegGetValueW(
            HKEY_LOCAL_MACHINE,
            TestInfrastructureRegistryPath,
            UseSystemCompositionEngineValueName,
            RRF_RT_REG_DWORD | registryView,
            &valueType,
            &requested,
            &requestedSize);
        if (status == ERROR_FILE_NOT_FOUND ||
            status == ERROR_PATH_NOT_FOUND)
        {
            return false;
        }

        THROW_IF_WIN32_ERROR(status);
        THROW_HR_IF(
            HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
            requested != 1);
        return true;
    }

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

        const auto lastCharacter =
            value.find_last_not_of(" \t\r\n");
        THROW_HR_IF(
            HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
            lastCharacter == std::string::npos);
        value.resize(lastCharacter + 1);
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

    void WriteTextAtomically(
        const std::wstring& path,
        const std::string& contents)
    {
        const std::wstring temporaryPath = path + L".tmp";
        DeleteIfPresent(temporaryPath);

        wil::unique_hfile file{ CreateFileW(
            temporaryPath.c_str(),
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
            contents.data(),
            static_cast<DWORD>(contents.size()),
            &bytesWritten,
            nullptr));
        THROW_HR_IF(
            HRESULT_FROM_WIN32(ERROR_WRITE_FAULT),
            bytesWritten != static_cast<DWORD>(contents.size()));
        THROW_IF_WIN32_BOOL_FALSE(FlushFileBuffers(file.get()));
        file.reset();

        if (!MoveFileExW(
                temporaryPath.c_str(),
                path.c_str(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        {
            const DWORD error = GetLastError();
            DeleteFileW(temporaryPath.c_str());
            THROW_WIN32(error);
        }
    }

    void DeleteIfPresent(const std::wstring& path)
    {
        if (!DeleteFileW(path.c_str()))
        {
            const DWORD error = GetLastError();
            THROW_HR_IF(
                HRESULT_FROM_WIN32(error),
                error != ERROR_FILE_NOT_FOUND &&
                    error != ERROR_PATH_NOT_FOUND);
        }
    }

    void PrepareCertificationResponse()
    {
        const std::wstring requestDirectory =
            GetExecutableDirectory() + L"\\" + RequestDirectoryName;
        const DWORD attributes = GetFileAttributesW(requestDirectory.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES)
        {
            const DWORD error = GetLastError();
            if (error == ERROR_FILE_NOT_FOUND ||
                error == ERROR_PATH_NOT_FOUND)
            {
                return;
            }

            THROW_WIN32(error);
        }
        THROW_HR_IF(
            HRESULT_FROM_WIN32(ERROR_DIRECTORY),
            (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0);

        certificationPath =
            requestDirectory + L"\\" + CertificationFileName;
        failurePath =
            requestDirectory + L"\\" + FailureFileName;
        DeleteIfPresent(certificationPath);
        DeleteIfPresent(certificationPath + L".tmp");
        DeleteIfPresent(failurePath);
        DeleteIfPresent(failurePath + L".tmp");

        configuredRequestId = ReadBoundedUtf8(
            requestDirectory + L"\\" + RequestIdFileName);
        std::wstring bracedRequestId = L"{";
        bracedRequestId += Utf8ToWide(configuredRequestId);
        bracedRequestId += L"}";
        GUID parsedRequestId{};
        THROW_HR_IF(
            HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
            FAILED(IIDFromString(bracedRequestId.c_str(), &parsedRequestId)));

    }

    void WriteCertification()
    {
        if (certificationPath.empty())
        {
            return;
        }

        WriteTextAtomically(
            certificationPath,
            "request-id=" + configuredRequestId +
                "\r\npid=" + std::to_string(GetCurrentProcessId()) +
                "\r\n");
        DeleteIfPresent(failurePath);
    }

    void WriteFailure(
        const char* stage,
        HRESULT error)
    {
        if (failurePath.empty())
        {
            return;
        }

        std::array<char, 256> failure{};
        const int failureLength = sprintf_s(
            failure.data(),
            failure.size(),
            "request-id=%s\r\npid=%lu\r\nstage=%s\r\nerror=0x%08X\r\n",
            configuredRequestId.c_str(),
            GetCurrentProcessId(),
            stage,
            static_cast<unsigned int>(error));
        THROW_HR_IF(E_UNEXPECTED, failureLength <= 0);
        WriteTextAtomically(
            failurePath,
            std::string(failure.data(), failureLength));
        DeleteIfPresent(certificationPath);
    }
}

bool SwitcherComposition::ConfigureFromRegistryRequest()
{
    if (!IsSystemCompositionRequested())
    {
        return false;
    }

    try
    {
        PrepareCertificationResponse();

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

        systemCompositionConfigured = true;
        return true;
    }
    catch (const wil::ResultException& exception)
    {
        WriteFailure("selection", exception.GetErrorCode());
        throw;
    }
    catch (const winrt::hresult_error& exception)
    {
        WriteFailure("selection", exception.code().value);
        throw;
    }
    catch (...)
    {
        WriteFailure("selection", E_FAIL);
        throw;
    }
}

void SwitcherComposition::Certify()
{
    if (!systemCompositionConfigured ||
        systemCompositionCertified)
    {
        return;
    }

    try
    {
        Microsoft::WRL::ComPtr<
            ABI::Microsoft::UI::Composition::ICompositionEngineStatics>
            compositionEngineStatics;
        THROW_IF_FAILED(RoGetActivationFactory(
            Microsoft::WRL::Wrappers::HStringReference(
                RuntimeClass_Microsoft_UI_Composition_CompositionEngine).Get(),
            IID_PPV_ARGS(compositionEngineStatics.ReleaseAndGetAddressOf())));

        auto probeElement =
            winrt::Microsoft::UI::Xaml::Controls::Grid();
        auto visual =
            winrt::Microsoft::UI::Xaml::Hosting::ElementCompositionPreview::
                GetElementVisual(probeElement);
        THROW_HR_IF(E_NOINTERFACE, !visual);
        auto compositor = visual.Compositor();
        Microsoft::WRL::ComPtr<::IInspectable> systemCompositorAbi;
        THROW_IF_FAILED(compositionEngineStatics->GetForSystemEngine(
            reinterpret_cast<::IInspectable*>(winrt::get_abi(compositor)),
            systemCompositorAbi.ReleaseAndGetAddressOf()));
        THROW_HR_IF(E_NOINTERFACE, !systemCompositorAbi);

        wil::unique_hstring runtimeClassName;
        THROW_IF_FAILED(systemCompositorAbi->GetRuntimeClassName(
            runtimeClassName.put()));
        UINT32 runtimeClassNameLength = 0;
        PCWSTR runtimeClassNameRaw = WindowsGetStringRawBuffer(
            runtimeClassName.get(),
            &runtimeClassNameLength);
        THROW_HR_IF(
            E_FAIL,
            std::wstring(
                runtimeClassNameRaw,
                runtimeClassNameLength) !=
                L"Windows.UI.Composition.Compositor");

        WriteCertification();
        systemCompositionCertified = true;
    }
    catch (const wil::ResultException& exception)
    {
        WriteFailure("certification", exception.GetErrorCode());
        throw;
    }
    catch (const winrt::hresult_error& exception)
    {
        WriteFailure("certification", exception.code().value);
        throw;
    }
    catch (...)
    {
        WriteFailure("certification", E_FAIL);
        throw;
    }
}
