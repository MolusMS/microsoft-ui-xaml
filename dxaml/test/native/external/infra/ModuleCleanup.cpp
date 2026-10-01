// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include <RpcServer.h>
#include <ResourcesPriHelper.h>
#include <WexTestClass.h>

#include <crtdbg.h>
#include <stdlib.h>
#include <stdio.h>
#include <Windows.h>
#include <vector>
#include <string>
#include <sstream>
#include <filesystem>

#include <Versioning.h>

#define LOG_OUTPUT(fmt, ...) WEX::Logging::Log::Comment(WEX::Common::String().Format(fmt, __VA_ARGS__))

// These methods will be called from the out-of-proc loaded instance of
// this DLL. They will register our RPC server.

BEGIN_MODULE()
    MODULE_PROPERTY(L"RunFixtureAs:Module", L"ElevatedUserOrSystem")
    MODULE_PROPERTY(L"EnsureLoggedOnUser:UserCount", L"1")
    MODULE_PROPERTY(L"DeploymentItem", L"..\\EtwProcessor.dll")
    MODULE_PROPERTY(L"ThreadingModel[@HostingMode='WPF']", L"STA")
    MODULE_PROPERTY(L"UAP:Host[@HostingMode='Win32Explicit']", L"PackagedCwa")
    MODULE_PROPERTY(L"UAP:Host[@HostingMode='WPF']", L"PackagedCwa")
    MODULE_PROPERTY(L"UAP:AppXManifest[@HostingMode='Win32Explicit']", APPXMANIFEST_WINDOWS_VERSION_CURRENT_CENTENNIAL)
    MODULE_PROPERTY(L"UAP:AppXManifest[@HostingMode='WPF']", APPXMANIFEST_WINDOWS_VERSION_CURRENT_CENTENNIAL)
    MODULE_PROPERTY(L"UAP:AppXManifest[default]", APPXMANIFEST_WINDOWS_VERSION_CURRENT)
    MODULE_PROPERTY(L"UAP:WaitForXamlWindowActivation", L"false")
    MODULE_PROPERTY(L"Hosting:Mode", L"WPF")
    MODULE_PROPERTY(L"TestPass:MinOSVer", WINDOWS_OS_VERSION_RS4)
    MODULE_PROPERTY(L"HelixWorkItemCreation", L"CreateWorkItemPerTestClass")
END_MODULE()

MODULE_SETUP(ModuleSetup);
MODULE_CLEANUP(ModuleCleanup);

// Nice helper functions to split string
// https://stackoverflow.com/questions/236129/most-elegant-way-to-split-a-string
namespace StringHelpers
{
    template<typename Out>
    void Split(const std::string &s, char delim, Out result) {
        std::stringstream ss;
        ss.str(s);
        std::string item;
        while (std::getline(ss, item, delim)) {
            *(result++) = item;
        }
    }

    std::vector<std::string> Split(const std::string &s, char delim) {
        std::vector<std::string> elems;
        Split(s, delim, std::back_inserter(elems));
        return elems;
    }
}

// In checked/debug builds, suppress process-error dialog boxes that hang
// automated test runs. TAEF spawns multiple Te.ProcessHost.exe processes;
// ModuleSetup only runs in one of them. A static constructor runs in ALL
// processes that load this DLL, so the suppression is always active.
#if defined(_DEBUG) || defined(DBG)
namespace {
#if defined(_DEBUG)
    // The UCRT compiles report-hook support out unless _DEBUG selects the debug CRT.
    int __cdecl LogCrtFailure(int reportType, wchar_t* message, int* returnValue)
    {
        if (reportType == _CRT_ASSERT || reportType == _CRT_ERROR)
        {
            wchar_t buf[512]{};
            swprintf_s(buf, L"*** CRT %s: %s\n",
                reportType == _CRT_ASSERT ? L"ASSERT" : L"ERROR",
                message ? message : L"(no message)");
            OutputDebugStringW(buf);
            fwprintf(stderr, L"%s", buf);
            fflush(stderr);

            if (returnValue)
            {
                *returnValue = 0;
            }

            if (IsDebuggerPresent()) __debugbreak();
            return TRUE;
        }
        return FALSE;
    }
#endif

    void ApplyCrtSuppression()
    {
        _set_error_mode(_OUT_TO_STDERR);
#if defined(_DEBUG)
        _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_DEBUG);
        _CrtSetReportMode(_CRT_ERROR,  _CRTDBG_MODE_DEBUG);
        _CrtSetReportMode(_CRT_WARN,   _CRTDBG_MODE_DEBUG);
        _CrtSetReportHookW2(_CRT_RPTHOOK_INSTALL, LogCrtFailure);
#endif
        _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
        SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    }

    struct CrtSuppressor {
        CrtSuppressor() { ApplyCrtSuppression(); }
    } g_crtSuppressor;
}
#endif

bool ModuleSetup()
{
#if defined(_DEBUG) || defined(DBG)
    // Re-apply: other DLLs loaded since the static constructor may
    // have overridden our failure-reporting settings.
    ApplyCrtSuppression();
#endif

    VERIFY_SUCCEEDED(ConfigureResourcesPri(false /* configureManaged */));
    VERIFY_SUCCEEDED(RpcServerStart());
    return true;
}

bool ModuleCleanup()
{
    VERIFY_SUCCEEDED(RpcServerStop());
    return true;
}
