// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Runtime.InteropServices;
using Private.Infrastructure.Hosting;

namespace TaefHostAppManaged
{
    static class Program
    {
        private const string TestInfrastructureRegistryPath =
            @"Software\Microsoft\WinUITestInfrastructure";
        private const string UseSystemCompositionEngineValueName =
            "UseSystemCompositionEngine";
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorInvalidData = 13;
        private const uint RrfRtRegDword = 0x00000010;
        private const uint RrfSubKeyWow6464Key = 0x00010000;
        private const uint RrfSubKeyWow6432Key = 0x00020000;
        private static readonly IntPtr HKeyLocalMachine =
            new IntPtr(unchecked((int)0x80000002));

        [DllImport(
            "api-ms-win-core-registry-l1-1-0.dll",
            CharSet = CharSet.Unicode,
            EntryPoint = "RegGetValueW")]
        private static extern int RegGetValue(
            IntPtr key,
            string subKey,
            string value,
            uint flags,
            out uint type,
            out uint data,
            ref uint dataSize);

        static void Main(string[] args)
        {
            if (IsSystemCompositionRequested())
            {
                CompositionSwitcher.Configure();
            }

            XamlGeneratedProgram.XamlGeneratedMain();
        }

        internal static bool IsSystemCompositionRequested()
        {
            uint type;
            uint requested;
            uint requestedSize = sizeof(uint);
            uint registryView =
                IntPtr.Size == 8
                    ? RrfSubKeyWow6464Key
                    : RrfSubKeyWow6432Key;
            int status = RegGetValue(
                HKeyLocalMachine,
                TestInfrastructureRegistryPath,
                UseSystemCompositionEngineValueName,
                RrfRtRegDword | registryView,
                out type,
                out requested,
                ref requestedSize);
            if (status == ErrorFileNotFound || status == ErrorPathNotFound)
            {
                return false;
            }
            if (status != 0)
            {
                throw Marshal.GetExceptionForHR(HResultFromWin32(status));
            }
            if (requested != 1)
            {
                throw Marshal.GetExceptionForHR(
                    HResultFromWin32(ErrorInvalidData));
            }

            return true;
        }

        private static int HResultFromWin32(int error)
        {
            return error <= 0
                ? error
                : unchecked((int)(0x80070000u | ((uint)error & 0xffffu)));
        }
    }
}
