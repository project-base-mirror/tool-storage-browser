#!/usr/bin/env python3
"""Offline structural guards; Windows runtime/MSI tests remain required."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


class UpdateSafetyTests(unittest.TestCase):
    def test_msi_upgrade_is_transactional_and_restores_directory(self):
        package = ET.parse(ROOT / 'installer/Package.wxs').getroot()
        ns = {'w': 'http://wixtoolset.org/schemas/v4/wxs'}
        self.assertEqual('afterInstallInitialize', package.find('.//w:MajorUpgrade', ns).get('Schedule'))
        search = package.find('.//w:RegistrySearch', ns)
        self.assertEqual('InstallLocation', search.get('Name'))
        self.assertEqual('always64', search.get('Bitness'))
        assignment = package.find('.//w:SetProperty', ns)
        self.assertEqual('APPLICATIONFOLDER', assignment.get('Id'))
        self.assertEqual('both', assignment.get('Sequence'))
        self.assertIn('NOT APPLICATIONFOLDER', assignment.get('Condition'))

    def test_portable_flavor_survives_single_file_packaging(self):
        project = ET.parse(ROOT / 'src/S3Explorer.App/S3Explorer.App.csproj').getroot()
        attribute = project.find(".//AssemblyAttribute[@Include='System.Reflection.AssemblyMetadataAttribute']")
        self.assertEqual('$(SelfContained)', attribute.findtext('_Parameter2'))
        detector = (ROOT / 'src/S3Explorer.App/UpdatePackageDetector.cs').read_text()
        self.assertIn('MatchesApplicationDirectory(Environment.ProcessPath, registeredLocation)', detector)
        self.assertNotIn('File.Exists', detector)
        self.assertNotIn('Environment.SpecialFolder.ProgramFiles', detector)

    def test_completion_follows_restart(self):
        updater = (ROOT / 'src/S3Explorer.Updater/Program.cs').read_text()
        self.assertLess(updater.index('startApplication(options.ApplicationPath);'), updater.index('"completed"'))
        self.assertIn('APPLICATIONFOLDER=', updater)
        self.assertIn('failure = new AggregateException', updater)

    def test_pages_gate_precedes_upload_and_release_completion_retriggers(self):
        pages = (ROOT / '.github/workflows/pages.yml').read_text()
        release = (ROOT / '.github/workflows/release.yml').read_text()
        self.assertIn('workflow_run:', pages)
        self.assertIn('workflows: ["Publish GitHub Release"]', pages)
        self.assertIn("github.event.workflow_run.conclusion == 'success'", pages)
        self.assertLess(pages.index('Verify-RemoteRelease.ps1 -MetadataOnly'), pages.index('uses: actions/upload-pages-artifact'))
        self.assertIn('ref: main', pages)
        self.assertIn('Verify-RemoteRelease.ps1 -Tag $env:RELEASE_TAG -SkipPages', release)
        verifier = (ROOT / 'scripts/Verify-RemoteRelease.ps1').read_text()
        self.assertLess(verifier.index('GitHub digest does not match SHA256SUMS.txt'), verifier.index('if ($MetadataOnly)'))
        self.assertLess(verifier.index('if ($MetadataOnly)'), verifier.index('$frameworkPath = Download-Asset'))


if __name__ == '__main__':
    unittest.main()
