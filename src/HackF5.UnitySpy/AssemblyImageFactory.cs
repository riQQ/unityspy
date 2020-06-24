namespace HackF5.UnitySpy
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Text.RegularExpressions;
    using HackF5.UnitySpy.Detail;
    using HackF5.UnitySpy.Util;
    using JetBrains.Annotations;

    /// <summary>
    /// A factory that creates <see cref="IAssemblyImage"/> instances that provides access into a Unity application's
    /// managed memory.
    /// SEE: https://github.com/Unity-Technologies/mono.
    /// </summary>
    [PublicAPI]
    public static class AssemblyImageFactory
    {
        /// <summary>
        /// Creates an <see cref="IAssemblyImage"/> that provides access into a Unity application's managed memory.
        /// </summary>
        /// <param name="processId">
        /// The id of the Unity process to be inspected.
        /// </param>
        /// <param name="assemblyName">
        /// The name of the assembly to be inspected. The default setting of 'Assembly-CSharp' is probably what you want.
        /// </param>
        /// <returns>
        /// An <see cref="IAssemblyImage"/> that provides access into a Unity application's managed memory.
        /// </returns>
        public static IAssemblyImage Create(int processId, string assemblyName = "Assembly-CSharp")
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new InvalidOperationException(
                    "This library reads data directly from a process's memory, so is platform specific "
                    + "and only runs under Windows. It might be possible to get it running under macOS, but...");
            }

            var process = new ProcessFacade(processId);
            var monoModule = AssemblyImageFactory.GetMonoModule(process);
            if (monoModule == null)
            {
                throw new Exception("Couldn't find mono module");
            }
            var moduleDump = process.ReadModule(monoModule);
            var rootDomainFunctionAddress = AssemblyImageFactory.GetRootDomainFunctionAddress(moduleDump, monoModule);

            return AssemblyImageFactory.GetAssemblyImage(process, assemblyName, rootDomainFunctionAddress);
        }

        private static AssemblyImage GetAssemblyImage(ProcessFacade process, string name, int rootDomainFunctionAddress)
        {
            var domainAddress = process.ReadPtr((uint)rootDomainFunctionAddress + 1);
            // pointer to struct of type _MonoDomain
            var domain = process.ReadPtr(domainAddress);

            uint assemblyArrayPointer = GetAssemblyListAddress(process, domain);
            var bla = domain + 108;
            byte[] bytes = process.ReadByteArray(domain, (int)MonoLibraryOffsets.ReferencedAssemblies + 30);
            // pointer to array of structs of type _MonoAssembly
            var assemblyArrayAddress = process.ReadPtr(assemblyArrayPointer);

            if (assemblyArrayAddress == 0)
            {
                throw new Exception("Wrong starting point for referenced assemblies search");
            }

            for (var assemblyAddress = assemblyArrayAddress;
                assemblyAddress != Constants.NullPtr;
                assemblyAddress = process.ReadPtr(assemblyAddress + 0x4))
            {
                var assembly = process.ReadPtr(assemblyAddress);
                var assemblyNameAddress = process.ReadPtr(assembly + 0x8);
                var assemblyName = process.ReadAsciiString(assemblyNameAddress);
                if (assemblyName == name)
                {
                    return new AssemblyImage(process, process.ReadPtr(assembly + MonoLibraryOffsets.AssemblyImage + 4), assembly);
                }
            }

            throw new InvalidOperationException($"Unable to find assembly '{name}'");
        }

        private static uint GetAssemblyListAddress(ProcessFacade process, uint domain)
        {
            const string unityRootDomain = "Unity Root Domain";
            int unityRootDomainStringLength = unityRootDomain.Length + 1;
            // 18 pointers + 3 * 32 bit integer + 8 offset von address list zu friendly_name
            var domainNameStartAddress = domain + 92;

            for (var domainNameAddress = domainNameStartAddress; domainNameAddress < domainNameStartAddress + 200; domainNameAddress += 0x4)
            {
                var assembly = process.ReadPtr(domainNameAddress);
                if (assembly == 0)
                {
                    continue;
                }

                var assemblyName = process.ReadAsciiString(assembly, unityRootDomainStringLength);
                if (assemblyName == "Unity Root Domain")
                {
                    // the assembly list pointer is 8 byte before the domain name pointer
                    return domainNameAddress - 8;
                }
            }

            throw new Exception("Couldn't determine assembly list");
        }

        // https://stackoverflow.com/questions/36431220/getting-a-list-of-dlls-currently-loaded-in-a-process-c-sharp
        private static ModuleInfo GetMonoModule(ProcessFacade process)
        {
            var modulePointers = Native.GetProcessModulePointers(process);

            // Collect modules from the process
            var modules = new List<ModuleInfo>();
            foreach (var modulePointer in modulePointers)
            {
                var moduleFilePath = new StringBuilder(1024);
                var errorCode = Native.GetModuleFileNameEx(
                    process.Process.Handle,
                    modulePointer,
                    moduleFilePath,
                    (uint)moduleFilePath.Capacity);

                if (errorCode == 0)
                {
                    throw new COMException("Failed to get module file name.", Marshal.GetLastWin32Error());
                }

                var moduleName = Path.GetFileName(moduleFilePath.ToString());
                Native.GetModuleInformation(
                    process.Process.Handle,
                    modulePointer,
                    out var moduleInformation,
                    (uint)(IntPtr.Size * modulePointers.Length));

                // Convert to a normalized module and add it to our list
                var module = new ModuleInfo(moduleName, moduleInformation.BaseOfDll, moduleInformation.SizeInBytes);
                modules.Add(module);
            }

            return modules.FirstOrDefault(module => Regex.IsMatch(module.ModuleName, @"mono.*\.dll"));
        }

        private static int GetRootDomainFunctionAddress(byte[] moduleDump, ModuleInfo monoModuleInfo)
        {
            // offsets taken from https://docs.microsoft.com/en-us/windows/desktop/Debug/pe-format
            // ReSharper disable once CommentTypo
            var startIndex = moduleDump.ToInt32(0x3c); // lfanew

            var exportDirectoryIndex = startIndex + 0x78;
            var exportDirectory = moduleDump.ToInt32(exportDirectoryIndex);

            var numberOfFunctions = moduleDump.ToInt32(exportDirectory + 0x14);
            var functionAddressArrayIndex = moduleDump.ToInt32(exportDirectory + 0x1c);
            var functionNameArrayIndex = moduleDump.ToInt32(exportDirectory + 0x20);

            var rootDomainFunctionAddress = Constants.NullPtr;
            for (var functionIndex = Constants.NullPtr;
                functionIndex < (numberOfFunctions * Constants.SizeOfPtr);
                functionIndex += (int)Constants.SizeOfPtr)
            {
                var functionNameIndex = moduleDump.ToInt32(functionNameArrayIndex + functionIndex);
                var functionName = moduleDump.ToAsciiString(functionNameIndex);
                if (functionName == "mono_get_root_domain")
                {
                    rootDomainFunctionAddress = monoModuleInfo.BaseAddress.ToInt32()
                        + moduleDump.ToInt32(functionAddressArrayIndex + functionIndex);

                    break;
                }
            }

            if (rootDomainFunctionAddress == Constants.NullPtr)
            {
                throw new InvalidOperationException("Failed to find mono_get_root_domain function.");
            }

            return rootDomainFunctionAddress;
        }
    }
}