﻿// Copyright (c) Charlie Poole, Rob Prouse and Contributors. MIT License - see LICENSE.txt

using System.IO;
using NUnit.Framework;

namespace NUnit.ConsoleRunner.Tests
{
    public class MakeTestPackageTests
    {
        [Test]
        public void SingleAssembly()
        {
            var options = ConsoleMocks.Options("test.dll");
            var package = ConsoleRunner.MakeTestPackage(options);

            Assert.That(package.SubPackages.Count, Is.EqualTo(1));
            Assert.That(package.SubPackages[0].FullName, Is.EqualTo(Path.GetFullPath("test.dll")));
        }

        [Test]
        public void MultipleAssemblies()
        {
            var names = new [] { "test1.dll", "test2.dll", "test3.dll" };
            var options = ConsoleMocks.Options(names);
            var package = ConsoleRunner.MakeTestPackage(options);

            Assert.That(package.SubPackages.Count, Is.EqualTo(3));
            Assert.That(package.SubPackages[0].FullName, Is.EqualTo(Path.GetFullPath("test1.dll")));
            Assert.That(package.SubPackages[1].FullName, Is.EqualTo(Path.GetFullPath("test2.dll")));
            Assert.That(package.SubPackages[2].FullName, Is.EqualTo(Path.GetFullPath("test3.dll")));
        }

        [TestCase("--timeout=50", "DefaultTimeout", 50)]
        [TestCase("--dispose-runners", "DisposeRunners", true)]
        [TestCase("--config=Release", "ActiveConfig", "Release")]
        [TestCase("--trace=Error", "InternalTraceLevel", "Error")]
        [TestCase("--trace=error", "InternalTraceLevel", "Error")]
        [TestCase("--seed=1234", "RandomSeed", 1234)]
        [TestCase("--workers=3", "NumberOfTestWorkers", 3)]
        [TestCase("--workers=0", "NumberOfTestWorkers", 0)]
        [TestCase("--params:X=5;Y=7", "TestParameters", "X=5;Y=7")]
        [TestCase("--skipnontestassemblies", "SkipNonTestAssemblies", true)]
#if NETFRAMEWORK
        [TestCase("--x86", "RunAsX86", true)]
        [TestCase("--shadowcopy", "ShadowCopyFiles", true)]
        [TestCase("--process=Separate", "ProcessModel", "Separate")]
        [TestCase("--process=separate", "ProcessModel", "Separate")]
        [TestCase("--process=Single", "ProcessModel", "InProcess")]
        [TestCase("--process=InProcess", "ProcessModel", "InProcess")]
        [TestCase("--inprocess", "ProcessModel", "InProcess")]
        [TestCase("--domain=Multiple", "DomainUsage", "Multiple")]
        [TestCase("--domain=multiple", "DomainUsage", "Multiple")]
        [TestCase("--framework=net-4.0", "RequestedRuntimeFramework", "net-4.0")]
        [TestCase("--configfile=mytest.config", "ConfigurationFile", "mytest.config")]
        [TestCase("--agents=5", "MaxAgents", 5)]
        [TestCase("--debug", "DebugTests", true)]
        [TestCase("--pause", "PauseBeforeRun", true)]
        [TestCase("--set-principal-policy:UnauthenticatedPrincipal", "PrincipalPolicy", "UnauthenticatedPrincipal")]
#if DEBUG
        [TestCase("--debug-agent", "DebugAgent", true)]
#endif
#endif
        public void WhenOptionIsSpecified_PackageIncludesSetting(string option, string key, object val)
        {
            var options = ConsoleMocks.Options("test.dll", option);
            var package = ConsoleRunner.MakeTestPackage(options);

            Assert.That(package.Settings.ContainsKey(key), $"Setting not included for {option}");
            Assert.That(package.Settings[key], Is.EqualTo(val), "NumberOfTestWorkers not set correctly for {0}", option);
        }

#if NETFRAMEWORK
        [Test]
        public void WhenDebugging_NumberOfTestWorkersDefaultsToZero()
        {
            var options = ConsoleMocks.Options("test.dll", "--debug");
            var package = ConsoleRunner.MakeTestPackage(options);

            Assert.That(package.Settings["DebugTests"], Is.EqualTo(true));
            Assert.That(package.Settings["NumberOfTestWorkers"], Is.EqualTo(0));
        }

        [Test]
        public void WhenDebugging_NumberOfTestWorkersMayBeOverridden()
        {
            var options = ConsoleMocks.Options("test.dll", "--debug", "--workers=3");
            var package = ConsoleRunner.MakeTestPackage(options);

            Assert.That(package.Settings["DebugTests"], Is.EqualTo(true));
            Assert.That(package.Settings["NumberOfTestWorkers"], Is.EqualTo(3));
        }
#endif

        [Test]
        public void WhenNoOptionsAreSpecified_PackageContainsOnlyTwoSettings()
        {
            var options = ConsoleMocks.Options("test.dll");
            var package = ConsoleRunner.MakeTestPackage(options);

            Assert.That(package.Settings.Keys, Is.EquivalentTo(new string[] { "WorkDirectory", "DisposeRunners" }));
        }

    }
}
