#!/usr/bin/env python3
"""Offline structural guards; Windows runtime/MSI tests remain required."""
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


class UpdateSafetyTests(unittest.TestCase):
    def test_msi_upgrade_is_transactional_and_restores_directory(self):
        package = ET.parse(ROOT / 'installer/Package.wxs').getroot()
        ns = {'w': 'http://wixtoolset.org/schemas/v4/wxs'}
        self.assertEqual('afterInstallInitialize', package.find('.//w:MajorUpgrade', ns).get('Schedule'))
        self.assertEqual('yes', package.find('.//w:MajorUpgrade', ns).get('AllowSameVersionUpgrades'))
        search = package.find('.//w:RegistrySearch', ns)
        self.assertEqual('InstallLocation', search.get('Name'))
        self.assertEqual('always64', search.get('Bitness'))
        assignment = package.find('.//w:SetProperty', ns)
        self.assertEqual('APPLICATIONFOLDER', assignment.get('Id'))
        self.assertEqual('both', assignment.get('Sequence'))
        self.assertIn('NOT APPLICATIONFOLDER', assignment.get('Condition'))

    def test_rollback_failure_hook_is_only_in_generated_fixture(self):
        production = (ROOT / 'installer/Package.wxs').read_text(encoding='utf-8')
        builder = (ROOT / 'scripts/Build-InstallerLifecycleFixtures.ps1').read_text(encoding='utf-8')
        self.assertNotIn('LifecycleForceRollback', production)
        self.assertNotIn('S3EXPLORER_LIFECYCLE_FIXTURE', production)
        self.assertIn('artifacts\\installer-lifecycle-fixtures', builder)
        self.assertIn('if ($stage -eq "rollback")', builder)
        self.assertNotIn('$source.CreateElement("InstallExecute", $namespace)', builder)
        self.assertIn('$custom.SetAttribute("After", "InstallExecute")', builder)
        self.assertIn('$version = if ($stage -eq "previous") { "0.0.1" } else { "0.0.2" }', builder)
        self.assertIn('lifecycle-$payloadGeneration-only.txt', builder)

    def test_lifecycle_requires_runtime_evidence_and_local_packages(self):
        lifecycle = (ROOT / 'scripts/Test-InstallerLifecycle.ps1').read_text(encoding='utf-8')
        workflow = (ROOT / '.github/workflows/installer-lifecycle.yml').read_text(encoding='utf-8')
        self.assertIn('pull_request:', workflow)
        self.assertIn('flavor: [self-contained, framework-dependent]', workflow)
        self.assertIn('Build-InstallerLifecycleFixtures.ps1', workflow)
        self.assertIn('-RollbackMsi $fixtures.rollbackMsi', workflow)
        self.assertNotIn('gh release', workflow)
        self.assertNotIn('previous_tag', workflow)
        self.assertNotIn('Publish.ps1', workflow)
        self.assertIn('-ExpectedExitCodes @(1603)', lifecycle)
        self.assertIn('Assert-RollbackLog -Path $rollbackLog', lifecycle)
        self.assertIn('$actionSequence.InstallExecute -ge $actionSequence.LifecycleForceRollback', lifecycle)
        self.assertIn('$actionSequence.LifecycleForceRollback -ge $actionSequence.InstallFinalize', lifecycle)
        self.assertIn('payload-before-rollback.json', lifecycle)
        self.assertIn('payload-after-rollback.json', lifecycle)
        self.assertIn('Assert-ProductState -ProductCode $identities.previous.ProductCode -Installed $true', lifecycle)
        self.assertIn('Assert-ProductState -ProductCode $identities.rollback.ProductCode -Installed $false', lifecycle)
        self.assertIn('Assert-FixturePayload -Generation previous', lifecycle)
        self.assertIn('Assert-FixturePayload -Generation current', lifecycle)
        self.assertIn('Refusing lifecycle test: related product', lifecycle)
        self.assertIn('-ReuseRegisteredDirectory:([bool]$PreviousMsi)', lifecycle)

    def test_lifecycle_shortcuts_use_unicode_shell_reader(self):
        lifecycle = (ROOT / 'scripts/Test-InstallerLifecycle.ps1').read_text(encoding='utf-8')
        self.assertIn('000214F9-0000-0000-C000-000000000046', lifecycle)
        self.assertIn('UnmanagedType.LPWStr', lifecycle)
        self.assertIn('[S3Explorer.Lifecycle.ShortcutInspector]::Read', lifecycle)
        self.assertNotIn('$shell.CreateShortcut', lifecycle)
        self.assertIn('Copy-Item -LiteralPath $startMenuShortcutPath', lifecycle)

    def test_rollback_log_parser_checks_order_and_codes_in_both_languages(self):
        lifecycle = (ROOT / 'scripts/Test-InstallerLifecycle.ps1').read_text(encoding='utf-8')
        section = lifecycle.split('$patterns = @(', 1)[1].split('    )', 1)[0]
        patterns = re.findall(r"^\s*'([^']+)'[,]?$", section, flags=re.MULTILINE)
        self.assertEqual(7, len(patterns))

        def accepts(lines):
            log = '\n'.join(lines)
            offset = 0
            for pattern in patterns:
                match = re.search(pattern, log[offset:])
                if match is None:
                    return False
                offset += match.end()
            return True

        for lines in (
            [
                'Action ended 06:50:11: RemoveExistingProducts. Return value 1.',
                'Action ended 06:50:11: InstallExecute. Return value 1.',
                'Action start 06:50:11: LifecycleForceRollback.',
                'S3EXPLORER_LIFECYCLE_FORCE_ROLLBACK_AFTER_INSTALL_EXECUTE',
                'Action ended 06:50:11: LifecycleForceRollback. Return value 3.',
                'MSI (s): Executing op: Header(Signature=1397708873,Version=500,ScriptType=2,ScriptAttributes=1)',
                'MSI (s): Executing op: RollbackInfo(,RollbackAction=Rollback,RollbackDescription=Rolling back action: [1],)',
            ],
            [
                '操作结束 6:50:11: RemoveExistingProducts。返回值 1。',
                '操作结束 6:50:11: InstallExecute。返回值 1。',
                '操作开始 6:50:11: LifecycleForceRollback。',
                'S3EXPLORER_LIFECYCLE_FORCE_ROLLBACK_AFTER_INSTALL_EXECUTE',
                '操作结束 6:50:11: LifecycleForceRollback。返回值 3。',
                'MSI (s): Executing op: Header(Signature=1397708873,Version=500,ScriptType=2,ScriptAttributes=1)',
                'MSI (s): Executing op: RollbackInfo(,RollbackAction=Rollback,RollbackDescription=正在回退操作: [1],)',
            ],
        ):
            self.assertTrue(accepts(lines))
            self.assertFalse(accepts(lines[:1] + lines[2:]))
            self.assertFalse(accepts([lines[1], lines[0]] + lines[2:]))
            self.assertFalse(accepts(lines[:1] + [lines[1].replace('1', '3')] + lines[2:]))
            self.assertFalse(accepts(lines[:-1]))
            self.assertFalse(accepts(lines[:5] + [lines[5].replace('ScriptType=2', 'ScriptType=1')] + lines[6:]))
            self.assertFalse(accepts(lines[:5] + ['Rollback: progress text alone is insufficient']))
            self.assertFalse(accepts(lines[5:] + lines[:5]))
            self.assertFalse(accepts(lines[:5] + [line.replace('Executing op:', 'Planned op:') for line in lines[5:]]))
            self.assertFalse(accepts(lines[:5] + [lines[5]]))

    def test_portable_flavor_survives_single_file_packaging(self):
        project = ET.parse(ROOT / 'src/S3Explorer.App/S3Explorer.App.csproj').getroot()
        attribute = project.find(".//AssemblyAttribute[@Include='System.Reflection.AssemblyMetadataAttribute']")
        self.assertEqual('$(SelfContained)', attribute.findtext('_Parameter2'))
        detector = (ROOT / 'src/S3Explorer.App/UpdatePackageDetector.cs').read_text(encoding='utf-8')
        self.assertIn('MatchesApplicationDirectory(Environment.ProcessPath, registeredLocation)', detector)
        self.assertNotIn('File.Exists', detector)
        self.assertNotIn('Environment.SpecialFolder.ProgramFiles', detector)

    def test_completion_follows_restart(self):
        updater = (ROOT / 'src/S3Explorer.Updater/Program.cs').read_text(encoding='utf-8')
        self.assertLess(updater.index('startApplication(options.ApplicationPath);'), updater.index('"completed"'))
        self.assertIn('APPLICATIONFOLDER=', updater)
        self.assertIn('failure = new AggregateException', updater)

    def test_updater_quotes_msi_property_value_not_entire_assignment(self):
        updater = (ROOT / 'src/S3Explorer.Updater/Program.cs').read_text(encoding='utf-8')
        launcher = updater.split('internal static Process StartInstaller', 1)[1].split('internal static void WriteResult', 1)[0]
        self.assertIn('Process.Start(BuildInstallerStartInfo(', launcher)
        self.assertIn('startInfo.Arguments =', launcher)
        self.assertIn('$"APPLICATIONFOLDER={QuoteMsiValue(directory)}"', launcher)
        self.assertNotIn('ArgumentList.Add', launcher)
        self.assertNotIn('Path.Combine(directory, ".")', launcher)

    def test_lifecycle_runs_production_updater_and_bidirectional_flavor_switch(self):
        lifecycle = (ROOT / 'scripts/Test-InstallerLifecycle.ps1').read_text(encoding='utf-8')
        workflow = (ROOT / '.github/workflows/installer-lifecycle.yml').read_text(encoding='utf-8')
        self.assertIn('Invoke-ProductionUpdater -MsiPath $CurrentMsi', lifecycle)
        self.assertIn('"--sha256", (Get-FileHash', lifecycle)
        self.assertIn('Assert-ProductState -ProductCode $identities.alternate.ProductCode -Installed $false', lifecycle)
        self.assertIn('PassedBothDirections', lifecycle)
        self.assertIn('S3EXPLORER_MSI_ARGUMENT_PROBE', workflow)
        self.assertIn("-InstallDirectory (Join-Path $env:ProgramFiles 'S3 Explorer')", workflow)

    def test_configuration_lock_recovers_owner_exit_and_archive_is_nonfatal(self):
        store = (ROOT / 'src/S3Explorer.Infrastructure.Configuration/ExplorerConfigurationStore.cs').read_text(encoding='utf-8')
        self.assertNotIn('new Semaphore(', store)
        self.assertNotIn('WaitHandle.WaitAny', store)
        self.assertIn('FileShare.None', store)
        self.assertIn('TimeSpan.FromSeconds(15)', store)
        self.assertIn('LegacyArchiveWarning =', store)
        self.assertIn('exception is IOException or UnauthorizedAccessException', store)

    def test_manual_tag_preparation_is_explicit_and_checks_validated_main(self):
        workflow = (ROOT / '.github/workflows/release.yml').read_text(encoding='utf-8')
        preparation = workflow.split('  prepare-tag:', 1)[1].split('  release:', 1)[0]
        self.assertIn("github.event_name == 'workflow_dispatch' && inputs.create_tag", preparation)
        self.assertIn('"$DISPATCH_REF" == refs/heads/main', preparation)
        self.assertIn('"$(git rev-parse HEAD)" == "$EXPECTED_MAIN_SHA"', preparation)
        self.assertIn('"$(git rev-parse origin/main)" == "$EXPECTED_MAIN_SHA"', preparation)
        self.assertIn('head_sha=$EXPECTED_MAIN_SHA', preparation)
        self.assertIn('.event == "push"', preparation)
        self.assertIn('.conclusion == "success"', preparation)
        self.assertIn('sort_by(.created_at) | last', preparation)
        self.assertIn('ref: refs/tags/${{ env.RELEASE_TAG }}', workflow)
        self.assertLess(preparation.index('Exact main commit has no successful Validate'), preparation.index('git tag -a'))
        self.assertLess(preparation.index('git show-ref --verify --quiet'), preparation.index('git tag -a'))
        self.assertIn('git tag -a "$RELEASE_TAG" "$EXPECTED_MAIN_SHA"', preparation)
        self.assertNotIn('--force', preparation)
        self.assertNotIn('git tag -f', preparation)
        self.assertIn("needs.prepare-tag.result == 'skipped' && !inputs.create_tag", workflow)

    def test_pages_gate_precedes_upload_and_release_completion_retriggers(self):
        pages = (ROOT / '.github/workflows/pages.yml').read_text(encoding='utf-8')
        release = (ROOT / '.github/workflows/release.yml').read_text(encoding='utf-8')
        self.assertIn('workflow_run:', pages)
        self.assertIn('workflows: ["Publish GitHub Release"]', pages)
        self.assertIn("github.event.workflow_run.conclusion == 'success'", pages)
        self.assertLess(pages.index('Verify-RemoteRelease.ps1 -MetadataOnly'), pages.index('uses: actions/upload-pages-artifact'))
        self.assertIn('ref: main', pages)
        self.assertIn('Verify-RemoteRelease.ps1 -Tag $env:RELEASE_TAG -SkipPages', release)
        verifier = (ROOT / 'scripts/Verify-RemoteRelease.ps1').read_text(encoding='utf-8')
        self.assertLess(verifier.index('GitHub digest does not match SHA256SUMS.txt'), verifier.index('if ($MetadataOnly)'))
        self.assertLess(verifier.index('if ($MetadataOnly)'), verifier.index('$frameworkPath = Download-Asset'))


if __name__ == '__main__':
    unittest.main()
