// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Text;
using Private.Infrastructure.Hosting;
using Windows.ApplicationModel;

namespace TaefHostAppManaged
{
    static class Program
    {
        private const int MaximumSwitcherLafTokenSize = 64 * 1024;

        static void Main(string[] args)
        {
            var switcherLafToken = ReadSwitcherLafToken();
            if (switcherLafToken != null)
            {
                CompositionSwitcher.Configure(switcherLafToken);
            }

            XamlGeneratedProgram.XamlGeneratedMain();
        }

        private static string ReadSwitcherLafToken()
        {
            var requestPath = Path.Combine(
                Package.Current.InstalledLocation.Path,
                ".winui-switcher",
                "system-backend");
            try
            {
                using (var stream = File.Open(
                        requestPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite))
                using (var reader = new StreamReader(
                    stream,
                    new UTF8Encoding(false, true),
                    false))
                {
                    if (stream.Length <= 0 ||
                        stream.Length > MaximumSwitcherLafTokenSize)
                    {
                        throw new InvalidDataException(
                            "The composition switcher LAF token has an invalid size.");
                    }

                    return reader.ReadToEnd();
                }
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }
    }
}
