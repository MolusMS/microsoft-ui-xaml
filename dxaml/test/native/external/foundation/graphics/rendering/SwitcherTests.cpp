// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "pch.h"
#include "SwitcherTests.h"
#include <XamlTailored.h>
#include "FileLoader.h"
#include <wrl.h>

using namespace Microsoft::UI::Xaml;
using namespace Microsoft::UI::Xaml::Controls;
using namespace Microsoft::UI::Xaml::Tests::Common;

using namespace test_infra;

namespace Microsoft { namespace UI { namespace Xaml { namespace Tests { namespace Foundation { namespace Graphics {

Platform::String^ SwitcherTests::GetResourcesPath() const
{
    // Reuse the CompNodeTests resources
    return GetPackageFolder() + L"resources\\native\\external\\foundation\\graphics\\rendering\\";
}

bool SwitcherTests::ClassSetup()
{
    CommonTestSetupHelper::CommonTestClassSetup();
    return true;
}

bool SwitcherTests::ClassCleanup()
{
    return true;
}

bool SwitcherTests::TestSetup()
{
    test_infra::TestServices::WindowHelper->InitializeXaml();
    return true;
}

bool SwitcherTests::TestCleanup()
{
    test_infra::TestServices::WindowHelper->ShutdownXaml();
    TestServices::WindowHelper->VerifyTestCleanup();
    return true;
}

void SwitcherTests::VerifyLiftedSystemCompositionPath()
{
    VerifyLiftedSystemCompositionPathImpl();
}

void SwitcherTests::VerifyManagedPackagedHostSystemCompositionPath()
{
    VerifyLiftedSystemCompositionPathImpl();
}

void SwitcherTests::VerifyLiftedSystemCompositionPathImpl()
{
    // Public-API proof of the lifted->system path. CompositionEngine::GetForSystemEngine()
    // returns the underlying system-composition object for a lifted Microsoft.UI.Composition
    // object (see https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositionengine).
    // If the lifted compositor's system-engine equivalent reports its runtime class name as
    // "Windows.UI.Composition.Compositor", XAML's lifted compositor is backed by a real
    // system-composition compositor (lifted -> system path), not a pure-lifted/standalone one.
    auto wh = TestServices::WindowHelper;

    WUCRenderingScopeGuard wuc(
        DCompRendering::WUCCompleteSynchronousCompTree,
        /*resizeWindow*/true,
        /*injectMockDComp*/false,
        /*resetDevice*/false,
        /*resetWindowContent*/false);

    StackPanel^ root = safe_cast<StackPanel^>(LoadXamlFileOnUIThread(GetResourcesPath() + L"CompNode1.xaml"));
    RunOnUIThread([&]()
    {
        wh->WindowContent = root;
    });
    wh->WaitForIdle();

    RunOnUIThread([&]()
    {
        auto visual = Microsoft::UI::Xaml::Hosting::ElementCompositionPreview::GetElementVisual(root);
        auto compositor = visual->Compositor;

        // Step 1: public API - get the system-engine equivalent of the lifted compositor.
        // Returns null only if the system engine was never set or the object has no system equivalent.
        Platform::Object^ systemObject = Microsoft::UI::Composition::CompositionEngine::GetForSystemEngine(compositor);
        VERIFY_IS_NOT_NULL(
            systemObject,
            L"CompositionEngine::GetForSystemEngine returned null - lifted-only path, not lifted->system");

        // Step 2: verify the underlying system object identifies itself as Windows.UI.Composition.Compositor.
        // This is the conclusive lifted-system-composition signal.
        IInspectable* systemInspectable = reinterpret_cast<IInspectable*>(systemObject);
        wil::unique_hstring runtimeClassName;
        VERIFY_SUCCEEDED(systemInspectable->GetRuntimeClassName(runtimeClassName.put()));
        UINT32 length = 0;
        PCWSTR runtimeClassNameRaw = WindowsGetStringRawBuffer(runtimeClassName.get(), &length);
        WEX::Logging::Log::Comment(WEX::Common::String().Format(L"System-engine object class name: '%s'", runtimeClassNameRaw));
        VERIFY_ARE_EQUAL(
            std::wstring(L"Windows.UI.Composition.Compositor"),
            std::wstring(runtimeClassNameRaw, length),
            L"System-engine object is not Windows.UI.Composition.Compositor - lifted system-composition routing not active");
    });
}

} } } } } }
