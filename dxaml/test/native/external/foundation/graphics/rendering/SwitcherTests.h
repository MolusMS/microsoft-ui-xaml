// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

#include <Versioning.h>
#include <WUCRenderingScopeGuard.h>
#include <RuntimeEnabledFeatureOverride.h>

namespace Microsoft { namespace UI { namespace Xaml { namespace Tests { namespace Foundation { namespace Graphics {

class SwitcherTests : public WEX::TestClass<SwitcherTests>
{
public:
    BEGIN_TEST_CLASS(SwitcherTests)
        TEST_CLASS_PROPERTY(L"BinaryUnderTest", L"Microsoft.UI.Xaml.dll")
        TEST_CLASS_PROPERTY(L"RunAs", L"UAP")
        TEST_CLASS_PROPERTY(L"Classification", L"Integration")
        TEST_CLASS_PROPERTY(L"VelocityTestPass:OneCoreStrict", L"Desktop")
        TEST_CLASS_PROPERTY(L"HelixWorkItemCreation", L"CreateWorkItemPerTestClass")
        // Requires the dedicated private MockDComp setup and packaged launch path.
        TEST_CLASS_PROPERTY(L"Ignore", L"TRUE")
    END_TEST_CLASS()

    TEST_CLASS_SETUP(ClassSetup)
    TEST_CLASS_CLEANUP(ClassCleanup)
    TEST_METHOD_SETUP(TestSetup)
    TEST_METHOD_CLEANUP(TestCleanup)

    BEGIN_TEST_METHOD(VerifyLiftedSystemCompositionPath)
        TEST_METHOD_PROPERTY(L"Description", L"Engagement certifier: uses CompositionEngine::GetForSystemEngine to verify the lifted compositor's system-engine equivalent is Windows.UI.Composition.Compositor (proves lifted->system routing, not a silent no-op). Because the backend flip is process-wide, this one proof certifies the whole SwitcherMode run.")
    END_TEST_METHOD()

    BEGIN_TEST_METHOD(VerifyManagedPackagedHostSystemCompositionPath)
        TEST_METHOD_PROPERTY(L"Description", L"Engagement certifier for the managed packaged entry point: runs the native proof inside XamlManagedTAEFTests and verifies lifted-to-system composition routing.")
        TEST_METHOD_PROPERTY(L"UAP:AppXManifest", L"AppXManifest.managed.current.xml")
        TEST_METHOD_PROPERTY(L"UAP:Praid", L"XamlManagedTAEFTests")
    END_TEST_METHOD()

private:
    void VerifyLiftedSystemCompositionPathImpl();

    inline Platform::String^ GetResourcesPath() const;
};

} } } } } }
