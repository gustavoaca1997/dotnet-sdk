// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using ManifestReaderTests;
using Microsoft.DotNet.Cli.NuGetPackageDownloader;
using Microsoft.DotNet.Workloads.Workload.Install;
using Microsoft.NET.Sdk.WorkloadManifestReader;
using Microsoft.NET.TestFramework;
using Microsoft.NET.TestFramework.Utilities;
using Xunit;
using Xunit.Abstractions;
using Microsoft.DotNet.Workloads.Workload.Install.InstallRecord;
using Microsoft.NET.TestFramework.Commands;
using Microsoft.NET.TestFramework.Assertions;


namespace Microsoft.DotNet.Cli.Workload.Install.Tests
{
    public class GivenDotnetWorkloadInstall : SdkTest
    {
        private readonly BufferedReporter _reporter;
        private readonly string _manifestPath;

        public GivenDotnetWorkloadInstall(ITestOutputHelper log) : base(log)
        {
            _reporter = new BufferedReporter();
            _manifestPath = Path.Combine(_testAssetsManager.GetAndValidateTestProjectDirectory("SampleManifest"), "Sample.json");
        }

        [Fact]
        public void GivenWorkloadInstallItErrorsOnFakeWorkloadName()
        {
            var command = new DotnetCommand(Log);
            command
                .WithEnvironmentVariable("DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR", string.Empty)
                .WithEnvironmentVariable("PATH", "fake")
                .Execute("workload", "install", "fake", "--skip-manifest-update")
                .Should()
                .Fail()
                .And
                .HaveStdOutContaining("Workload not found");
        }

        [Fact]
        public void GivenWorkloadInstallItCanInstallPacks()
        {
            var mockWorkloadIds = new WorkloadId[] { new WorkloadId("xamarin-android") };
            var parseResult = Parser.GetWorkloadsInstance.Parse(new string[] { "dotnet", "workload", "install", "xamarin-android", "--skip-manifest-update" });
            (_, var installManager, var installer, _, _, _) = GetTestInstallers(parseResult);

            installManager.InstallWorkloads(mockWorkloadIds, true);

            installer.GarbageCollectionCalled.Should().BeTrue();
            installer.CachePath.Should().BeNull();
            installer.InstallationRecordRepository.WorkloadInstallRecord.Should().BeEquivalentTo(mockWorkloadIds);
            installer.InstalledPacks.Count.Should().Be(8);
            installer.InstalledPacks.Where(pack => pack.Id.Contains("Android")).Count().Should().Be(8);
        }

        [Fact]
        public void GivenWorkloadInstallItCanRollBackPackInstallation()
        {
            var mockWorkloadIds = new WorkloadId[] { new WorkloadId("xamarin-android"), new WorkloadId("xamarin-android-build") };
            var parseResult = Parser.GetWorkloadsInstance.Parse(new string[] { "dotnet", "workload", "install", "xamarin-android", "xamarin-android-build", "--skip-manifest-update" });
            (_, var installManager, var installer, var workloadResolver, _, _) = GetTestInstallers(parseResult, failingWorkload: "xamarin-android-build");

            var exceptionThrown = Assert.Throws<Exception>(() => installManager.InstallWorkloads(mockWorkloadIds, true));
            exceptionThrown.Message.Should().Be("Failing workload: xamarin-android-build");
            var expectedPacks = mockWorkloadIds
                .SelectMany(workloadId => workloadResolver.GetPacksInWorkload(workloadId.ToString()))
                .Distinct()
                .Select(packId => workloadResolver.TryGetPackInfo(packId));
            installer.RolledBackPacks.ShouldBeEquivalentTo(expectedPacks);
            installer.InstallationRecordRepository.WorkloadInstallRecord.Should().BeEmpty();
        }

        [Fact]
        public void GivenWorkloadInstallOnFailingRollbackItDisplaysTopLevelError()
        {
            var mockWorkloadIds = new WorkloadId[] { new WorkloadId("xamarin-android"), new WorkloadId("xamarin-android-build") };
            var testDirectory = _testAssetsManager.CreateTestDirectory().Path;
            var dotnetRoot = Path.Combine(testDirectory, "dotnet");
            var installer = new MockPackWorkloadInstaller(failingWorkload: "xamarin-android-build", failingRollback: true);
            var workloadResolver = WorkloadResolver.CreateForTests(new MockManifestProvider(new[] { _manifestPath }), new string[] { dotnetRoot });
            var parseResult = Parser.GetWorkloadsInstance.Parse(new string[] { "dotnet", "workload", "install", "xamarin-android", "xamarin-android-build", "--skip-manifest-update" });
            var installManager = new WorkloadInstallCommand(parseResult, reporter: _reporter, workloadResolver: workloadResolver, workloadInstaller: installer, version: "6.0.100");

            var exceptionThrown = Assert.Throws<Exception>(() => installManager.InstallWorkloads(mockWorkloadIds, true));
            exceptionThrown.Message.Should().Be("Failing workload: xamarin-android-build");
            string.Join(" ", _reporter.Lines).Should().Contain("Rollback failure");

        }
		
		[Fact]
        public void GivenWorkloadInstallItCanUpdateAdvertisingManifests()
        {
            var parseResult = Parser.GetWorkloadsInstance.Parse(new string[] { "dotnet", "workload", "install", "xamarin-android" });
            (_, var installManager, var installer, _, var manifestUpdater, _) = GetTestInstallers(parseResult);

            installManager.InstallWorkloads(new List<WorkloadId>(), false); // Don't actually do any installs, just update manifests

            installer.InstalledManifests.Should().BeEmpty(); // Didn't try to alter any installed manifests
            manifestUpdater.CalculateManifestUpdatesCallParams.Should().BeEquivalentTo(new SdkFeatureBand[] { new SdkFeatureBand("6.0.100") });
            manifestUpdater.UpdateAdvertisingManifestsCallParams.Should().BeEquivalentTo(new SdkFeatureBand[] { new SdkFeatureBand("6.0.100") });
        }

        [Fact]
        public void GivenWorkloadInstallItCanUpdateInstalledManifests()
        {
            var parseResult = Parser.GetWorkloadsInstance.Parse(new string[] { "dotnet", "workload", "install", "xamarin-android" });
            var manifestsToUpdate = new (ManifestId, ManifestVersion, ManifestVersion)[] { };
            (_, var installManager, var installer, _, _, _) = GetTestInstallers(parseResult, manifestUpdates: manifestsToUpdate);

            installManager.InstallWorkloads(new List<WorkloadId>(), false); // Don't actually do any installs, just update manifests

            installer.InstalledManifests.Should().BeEquivalentTo(manifestsToUpdate);
        }
		
        [Fact]
        public void GivenWorkloadInstallItCanDownloadToOfflineCache()
        {
            var mockWorkloadIds = new WorkloadId[] { new WorkloadId("xamarin-android") };
            var cachePath = "mockCachePath";
            var parseResult = Parser.GetWorkloadsInstance.Parse(new string[] { "dotnet", "workload", "install", "xamarin-android", "--download-to-cache", cachePath });
            (_, var installManager, var installer, _, _, _) = GetTestInstallers(parseResult);

            installManager.Execute();

            // 8 android pack packages
            installer.CachedPacks.Count.Should().Be(8);
            installer.CachedPacks.Where(pack => pack.Id.Contains("Android")).Count().Should().Be(8);
            installer.CachePath.Should().Be(cachePath);
        }

        [Fact]
        public void GivenWorkloadInstallItCanInstallFromOfflineCache()
        {
            var mockWorkloadIds = new WorkloadId[] { new WorkloadId("xamarin-android") };
            var cachePath = "mockCachePath";
            var parseResult = Parser.GetWorkloadsInstance.Parse(new string[] { "dotnet", "workload", "install", "xamarin-android", "--from-cache", cachePath });
            (_, var installManager, var installer, _, _, var nugetDownloader) = GetTestInstallers(parseResult);

            installManager.Execute();

            installer.GarbageCollectionCalled.Should().BeTrue();
            installer.CachePath.Should().Be(cachePath);
            installer.InstallationRecordRepository.WorkloadInstallRecord.Should().BeEquivalentTo(mockWorkloadIds);
            installer.InstalledPacks.Count.Should().Be(8);
            installer.InstalledPacks.Where(pack => pack.Id.Contains("Android")).Count().Should().Be(8);
            nugetDownloader.DownloadCallParams.Count().Should().Be(0);
        }
		
        [Fact]
		public void GivenWorkloadInstallItPrintsDownloadUrls()
        {
            var parseResult = Parser.GetWorkloadsInstance.Parse(new string[] { "dotnet", "workload", "install", "xamarin-android", "--print-download-link-only" });
            (_, var installManager, _, _, _, _) = GetTestInstallers(parseResult);

            installManager.Execute();

            _reporter.Lines.Should().Contain("==allPackageLinksJsonOutputStart==");
            string.Join(" ", _reporter.Lines).Should().Contain("mock-url-xamarin.android.sdk");
        }

        private (string, WorkloadInstallCommand, MockPackWorkloadInstaller, IWorkloadResolver, MockWorkloadManifestUpdater, MockNuGetPackageDownloader) GetTestInstallers(
            ParseResult parseResult, [CallerMemberName] string testName = "", string failingWorkload = null, IEnumerable<(ManifestId, ManifestVersion, ManifestVersion)> manifestUpdates =  null)
        {
            _reporter.Clear();
            var testDirectory = _testAssetsManager.CreateTestDirectory(testName: testName).Path;
            var dotnetRoot = Path.Combine(testDirectory, "dotnet");
            var installer = new MockPackWorkloadInstaller(failingWorkload);
            var workloadResolver = WorkloadResolver.CreateForTests(new MockManifestProvider(new[] { _manifestPath }), new string[] { dotnetRoot });
            var nugetDownloader = new MockNuGetPackageDownloader(dotnetRoot);
            var manifestUpdater = new MockWorkloadManifestUpdater(manifestUpdates);
            var installManager = new WorkloadInstallCommand(
                parseResult,
                reporter: _reporter,
                workloadResolver: workloadResolver,
                workloadInstaller: installer,
                nugetPackageDownloader: nugetDownloader,
                workloadManifestUpdater: manifestUpdater,
                userHome: testDirectory,
                version: "6.0.100");

            return (testDirectory, installManager, installer, workloadResolver, manifestUpdater, nugetDownloader);
        }
    }
}
